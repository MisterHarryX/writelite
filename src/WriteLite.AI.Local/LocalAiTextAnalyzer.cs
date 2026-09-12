using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using WriteLite.AI.Contracts;
using WriteLite.Language.Russian;

namespace WriteLite.AI.Local;

/// <summary>
/// Local offline AI analyzer.
/// Layering:
/// 1) Always available: <see cref="LocalCorrectionEngine"/> (rules assist / Lite).
/// 2) Optional WriteLite-Qwen via <see cref="QwenModelBackend"/> when pack+server present.
/// 3) Deterministic diff + <see cref="AiResultValidator"/> — model offsets are never trusted blindly.
/// On any neural failure: Lite engine result (or empty issues if disabled path).
/// </summary>
public sealed class LocalAiTextAnalyzer : ILocalAiTextAnalyzer
{
    private readonly LocalCorrectionEngine _engine;
    private readonly AiResultValidator _validator;
    private readonly LocalAiHardwareProbe _probe;
    private readonly QwenModelBackend _qwen;
    private readonly AiCallRouter _router;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Utc, AiTextAnalysisResult Result)> _cache = new();
    private AiModelProfile _activeProfile = AiModelProfile.Lite;
    private bool _warmed;
    private bool _disposed;

    public LocalAiTextAnalyzer(
        LocalCorrectionEngine? engine = null,
        AiResultValidator? validator = null,
        string? modelDirectory = null,
        QwenModelBackend? qwen = null,
        AiCallRouter? router = null)
    {
        _engine = engine ?? new LocalCorrectionEngine();
        _validator = validator ?? new AiResultValidator();
        _probe = new LocalAiHardwareProbe();
        _qwen = qwen ?? new QwenModelBackend(modelDirectory);
        _router = router ?? new AiCallRouter();
        _activeProfile = LocalAiHardwareProbe.SelectProfile(
            AiModelProfile.Auto,
            _probe.Probe(),
            _qwen.IsAvailable);
    }

    public bool IsAvailable => !_disposed;

    public string ModelVersion => _qwen.IsAvailable ? _qwen.ModelVersion : _engine.ModelVersion;

    public AiModelProfile ActiveProfile => _activeProfile;

    public QwenModelBackend Qwen => _qwen;

    /// <summary>
    /// Last analysis backend: writelight-qwen | lite | lite-fallback.
    /// </summary>
    /// <remarks>
    /// This is an <em>attribution</em>, not a statement about where the text came from.
    /// It reads <c>writelight-qwen</c> whenever the neural route was attempted, including
    /// when the model's answer was rejected by the validator and the deterministic engine
    /// produced the final text — see <see cref="LastNeuralOutputUsed"/> for that question.
    /// </remarks>
    public string LastBackend { get; private set; } = "lite";

    /// <summary>
    /// True only when the model's own output survived validation and reached the result.
    /// False when the neural route was skipped, unreachable, or answered unusably.
    /// </summary>
    public bool LastNeuralOutputUsed { get; private set; }

    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(10);

    public int MaxInputChars { get; set; } = 12_000;

    public int MinInputChars { get; set; } = 1;

    /// <summary>When true, always run Lite engine even if Qwen succeeds (merge via re-diff of Qwen text).</summary>
    public bool AlwaysRunLiteAssist { get; set; } = true;

    /// <summary>When true, Auto/Standard prefer live Qwen whenever health/pack is available.</summary>
    public bool PreferQwen { get; set; } = true;

    /// <summary>
    /// Prepares the local AI path.
    /// </summary>
    /// <param name="startBackend">
    /// Whether to start the generative model server, or only to check whether one is already
    /// there. False on the application startup path.
    /// </param>
    /// <remarks>
    /// <para><b>Why the distinction exists.</b> Measured on the Phase 7 release build: warming
    /// with <c>startBackend: true</c> at startup launched llama-server and left it holding
    /// 481 MB resident for a session in which no Smart Action was ever invoked. Combined idle
    /// footprint was 1.52 GB across the three processes. §34 of the brief is explicit that the
    /// generative model should not consume large memory merely because WriteLite is open, and
    /// §39 asks for no multi-GB WriteAI RAM while unused.</para>
    ///
    /// <para>Availability is still established: <see cref="QwenModelBackend.RefreshAvailability"/>
    /// checks that the model files are present and notices a server that is already running,
    /// which is everything the status UI needs to say «WriteAI готов» without a model in
    /// memory. The server starts on the first request that actually needs it — the rewrite and
    /// analysis paths both call <c>EnsureServerAsync</c> already — which turns a cost every
    /// user pays into one only the users of the feature pay.</para>
    ///
    /// <para>The deterministic Lite engine is still exercised here. It is in-process, costs
    /// microseconds, and is what answers when the generative path is unavailable.</para>
    /// </remarks>
    public async Task WarmupAsync(bool startBackend = true, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_warmed)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = _engine.Correct("Привет мир");
            _qwen.RefreshAvailability();
            if (startBackend)
            {
                await _qwen.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            }

            LocalAiDiagnostics.Technical(
                "local-ai-warmup",
                $"qwenAvailable={(_qwen.IsAvailable ? 1 : 0)} endpoint={_qwen.ActiveEndpoint} "
                + $"backend={_qwen.BackendName} startBackend={(startBackend ? 1 : 0)}");

            // Only a warmup that started the backend has finished the job; an
            // availability-only pass must not stop a later one from starting it.
            _warmed = startBackend;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AiTextAnalysisResult> AnalyzeAsync(
        AiTextAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sw = Stopwatch.StartNew();

        if (request.SchemaVersion != 0 && request.SchemaVersion != AiSchema.CurrentVersion)
        {
            return Empty(request, sw, AiErrorCodes.SchemaMismatch, "Несовместимая schemaVersion.");
        }

        var text = request.Text ?? "";
        if (text.Length < MinInputChars)
        {
            return Empty(request, sw, AiErrorCodes.TextTooShort, null);
        }

        if (text.Length > MaxInputChars)
        {
            return Empty(request, sw, AiErrorCodes.TextTooLong, "Текст слишком длинный для локальной модели.");
        }

        if (LanguageDetector.Detect(text) != RussianLanguageProfile.IsoCode)
        {
            // English prose is outside the product scope. Preserve Latin-only
            // technical fragments byte-for-byte and never route them to Qwen.
            return Empty(request, sw, AiErrorCodes.Ok, null);
        }

        var requestedProfile = request.Profile;
        var hw = _probe.Probe();

        // Refresh live health so a started server is detected without restart.
        // Lite profile never calls Qwen, so skip the health probe to avoid blocking offline users.
        if (requestedProfile != AiModelProfile.Lite)
        {
            _qwen.RefreshAvailability();
        }

        var profile = LocalAiHardwareProbe.SelectProfile(requestedProfile, hw, _qwen.IsAvailable);
        // Prefer Qwen path whenever server/pack is available (unless user forced Lite).
        if (PreferQwen && _qwen.IsAvailable && requestedProfile != AiModelProfile.Lite)
        {
            profile = profile == AiModelProfile.Quality ? AiModelProfile.Quality : AiModelProfile.Standard;
        }

        _activeProfile = profile;
        _router.MinChars = Math.Min(MinInputChars, 8);
        _router.MaxChars = MaxInputChars;
        var cacheKey = CreateCacheKey(text, request.Language, profile);
        if (_cache.TryGetValue(cacheKey, out var cached)
            && DateTimeOffset.UtcNow - cached.Utc < CacheTtl)
        {
            LocalAiDiagnostics.Technical("qwen-cache-hit", $"profile={profile}");
            sw.Stop();
            return cached.Result with { ProcessingTime = sw.Elapsed };
        }
        LocalAiDiagnostics.Technical("qwen-cache-miss", $"profile={profile}");

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Empty(request, sw, AiErrorCodes.Cancelled, null);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1) Deterministic Lite assist (never blocks; always offline).
            var (liteCorrected, _, liteUncertain) = _engine.Correct(text);
            const string language = RussianLanguageProfile.IsoCode;

            string corrected = AlwaysRunLiteAssist ? liteCorrected : text;
            bool uncertain = liteUncertain;
            string backend = "lite";
            bool neuralOutputUsed = false;
            string version = _engine.ModelVersion;
            string? warning = null;

            // 2) WriteLite-Qwen when available and router allows (Standard/Quality/Auto).
            var neuralReady = _qwen.IsAvailable && requestedProfile != AiModelProfile.Lite;
            var callQwen = _router.ShouldCallNeural(
                text,
                profile,
                neuralAvailable: neuralReady,
                forceDeepCheck: profile is AiModelProfile.Quality || PreferQwen);

            LocalAiDiagnostics.Technical(
                "local-ai-route",
                $"preferQwen={(PreferQwen ? 1 : 0)} qwenAvailable={(_qwen.IsAvailable ? 1 : 0)} profile={profile} callQwen={(callQwen ? 1 : 0)} textLength={text.Length}");

            if (callQwen)
            {
                await _qwen.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
                var context = TextAnalysisContext.ExtractInteractive(text);
                LocalAiDiagnostics.Technical("qwen-input-char-count", $"count={context.Length} fullLength={text.Length}");
                var neural = await _qwen.InferAsync(context.Text, language, cancellationToken).ConfigureAwait(false);
                if (neural is not null && context.Start != 0)
                {
                    neural = neural with { CorrectedText = text[..context.Start] + neural.CorrectedText };
                }
                if (neural is not null
                    && _validator.IsRewriteSane(text, neural.CorrectedText)
                    && _validator.PreservesProtectedTokens(text, neural.CorrectedText)
                    && SameScriptFamily(text, neural.CorrectedText))
                {
                    // Neural first, then deterministic Lite polish (dictionaries/agreement pairs)
                    // so known high-precision fixes are not lost when the model is under-trained.
                    var polished = neural.CorrectedText;
                    try
                    {
                        var (liteOnNeural, _, _) = _engine.Correct(neural.CorrectedText);
                        if (_validator.IsRewriteSane(text, liteOnNeural)
                            && _validator.PreservesProtectedTokens(text, liteOnNeural)
                            && SameScriptFamily(text, liteOnNeural))
                        {
                            polished = liteOnNeural;
                        }
                    }
                    catch
                    {
                        // keep neural as-is
                    }

                    corrected = polished;
                    uncertain = neural.Uncertain || uncertain;
                    backend = "writelight-qwen";
                    neuralOutputUsed = true;
                    version = neural.ModelVersion;
                    if (neural.Uncertain)
                    {
                        warning = "Модель не уверена в части исправлений.";
                    }
                }
                else
                {
                    // Preserve the neural route when the live Qwen endpoint was actually attempted,
                    // even if its payload is malformed or the rewrite is not accepted. The safe
                    // local Lite engine still produces the final text, so the user sees correction
                    // instead of a hard failure, and the backend remains attributable.
                    backend = "writelight-qwen";
                    version = _engine.ModelVersion;
                    var reason = neural is null
                        ? (_qwen.LastError ?? "null-response")
                        : "validation-rejected";
                    LocalAiDiagnostics.Technical(
                        "qwen-response-rejected",
                        $"reason={reason} httpStatus={_qwen.LastHttpStatus} durationMs={_qwen.LastDurationMs}");
                    LocalAiDiagnostics.Technical("qwen-fallback-used", $"reason={reason}");
                    warning = "Локальная WriteLite-Qwen была вызвана, но ответ не прошёл валидацию; использованы безопасные правила.";
                }
            }
            else if (!_qwen.IsAvailable && profile is AiModelProfile.Standard or AiModelProfile.Quality)
            {
                backend = "lite-fallback";
                warning = "WriteLite-Qwen недоступна; использованы правила.";
                LocalAiDiagnostics.Technical("qwen-fallback-used", "reason=not-available");
            }
            else if (!callQwen)
            {
                LocalAiDiagnostics.Technical("qwen-fallback-used", "reason=router-skip");
            }

            LastBackend = backend;
            LastNeuralOutputUsed = neuralOutputUsed;

            // 3) Deterministic diff from original → corrected (ignore model offsets).
            var rawIssues = TextDiffBuilder.BuildIssues(text, corrected);
            rawIssues = EnrichExplanations(rawIssues);

            var candidate = new AiTextAnalysisResult(
                CorrectedText: corrected,
                DetectedLanguage: language,
                Issues: rawIssues,
                ModelVersion: version,
                SchemaVersion: AiSchema.CurrentVersion,
                ProcessingTime: sw.Elapsed,
                ProfileUsed: backend.StartsWith("writelight", StringComparison.Ordinal) ? profile : AiModelProfile.Lite,
                Backend: backend,
                Uncertain: uncertain,
                Warning: warning);

            var validated = _validator.Validate(text, candidate);
            sw.Stop();
            validated = validated with { ProcessingTime = sw.Elapsed };
            _activeProfile = validated.ProfileUsed;

            _cache[cacheKey] = (DateTimeOffset.UtcNow, validated);
            TrimCache();
            return validated;
        }
        catch (OperationCanceledException)
        {
            return Empty(request, sw, AiErrorCodes.Cancelled, null);
        }
        catch (OutOfMemoryException)
        {
            return Empty(request, sw, AiErrorCodes.OutOfMemory, "Недостаточно памяти для локальной модели.");
        }
        catch
        {
            // Ultimate fallback: Lite only
            try
            {
                var (corrected, language, uncertain) = _engine.Correct(text);
                var issues = TextDiffBuilder.BuildIssues(text, corrected);
                var candidate = new AiTextAnalysisResult(
                    corrected,
                    language,
                    issues,
                    _engine.ModelVersion,
                    AiSchema.CurrentVersion,
                    sw.Elapsed,
                    AiModelProfile.Lite,
                    "lite-fallback",
                    uncertain,
                    "Сбой нейросети; использованы локальные правила.");
                return _validator.Validate(text, candidate);
            }
            catch
            {
                return Empty(request, sw, AiErrorCodes.BackendFailed, "Локальный ИИ временно недоступен.");
            }
        }
        finally
        {
            if (_gate.CurrentCount == 0)
            {
                try { _gate.Release(); } catch { /* ignore */ }
            }
        }
    }

    private static IReadOnlyList<AiTextIssue> EnrichExplanations(IReadOnlyList<AiTextIssue> issues)
    {
        var list = new List<AiTextIssue>(issues.Count);
        foreach (var issue in issues)
        {
            var expl = issue.Explanation;
            if (issue.Type == AiIssueType.Spelling
                && issue.Original.Replace(" ", "", StringComparison.Ordinal)
                    .Equals(issue.Replacement.Replace(" ", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)
                && issue.Original.Contains(' ') != issue.Replacement.Contains(' '))
            {
                expl = "Частица или слово пишется раздельно/слитно по правилам орфографии.";
            }
            else if (issue.Type == AiIssueType.Capitalization)
            {
                expl = "Первое слово предложения должно начинаться с заглавной буквы.";
            }
            else if (issue.Type == AiIssueType.Punctuation && issue.Replacement.Contains(','))
            {
                expl = "Добавлена пропущенная запятая.";
            }
            else if (issue.Type == AiIssueType.Punctuation
                     && (issue.Replacement.EndsWith('.') || issue.Replacement.EndsWith('?') || issue.Replacement.EndsWith('!')))
            {
                expl = "Добавлен знак конца предложения.";
            }

            list.Add(issue with { Explanation = expl });
        }

        return list;
    }

    /// <summary>
    /// Rejects rewrites that flip Cyrillic prose to unrelated Latin (or vice versa).
    /// Protects against base-model hallucinations during early fine-tunes.
    /// </summary>
    private static bool SameScriptFamily(string original, string corrected)
    {
        static (int Cyr, int Lat) Counts(string s)
        {
            var cyr = 0;
            var lat = 0;
            foreach (var ch in s)
            {
                if (ch is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё') cyr++;
                else if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z') lat++;
            }

            return (cyr, lat);
        }

        var (oc, ol) = Counts(original);
        var (cc, cl) = Counts(corrected);
        if (oc >= 8 && ol <= 2 && cc <= 2 && cl >= 8)
        {
            return false;
        }

        if (ol >= 8 && oc <= 2 && cl <= 2 && cc >= 8)
        {
            return false;
        }

        return true;
    }

    private AiTextAnalysisResult Empty(
        AiTextAnalysisRequest request,
        Stopwatch sw,
        string code,
        string? warning)
    {
        sw.Stop();
        LastBackend = "lite";
        LastNeuralOutputUsed = false;
        return new AiTextAnalysisResult(
            CorrectedText: request.Text ?? "",
            DetectedLanguage: LanguageDetector.Detect(request.Text ?? ""),
            Issues: [],
            ModelVersion: ModelVersion,
            SchemaVersion: AiSchema.CurrentVersion,
            ProcessingTime: sw.Elapsed,
            ProfileUsed: _activeProfile,
            Backend: "lite",
            Uncertain: code != AiErrorCodes.Ok && code != AiErrorCodes.TextTooShort,
            Warning: warning,
            ErrorCode: code);
    }

    private void TrimCache()
    {
        if (_cache.Count <= 64)
        {
            return;
        }

        var stale = _cache
            .OrderBy(kv => kv.Value.Utc)
            .Take(_cache.Count - 48)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in stale)
        {
            _cache.TryRemove(key, out _);
        }
    }

    private string CreateCacheKey(string text, string? language, AiModelProfile profile)
    {
        var material = string.Join("|", text, language ?? "und", profile, ModelVersion, "prompt-v2", AiSchema.CurrentVersion);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Clear();
        await _qwen.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
