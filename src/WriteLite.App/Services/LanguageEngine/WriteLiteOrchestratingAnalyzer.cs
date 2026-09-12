using System.Diagnostics;
using System.Text.RegularExpressions;
using WriteLite.Models;
using WriteLite.Services.Grammar;
using WriteLite.Services.Lexical;
using WriteLite.Services.Stylistics;
using WriteLite.Services.Settings;
using WriteLite.Services.Spelling;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Composes built-in WriteLite analyzers with the extended engine, merge, dictionary and ignore filters.
/// </summary>
public sealed class WriteLiteOrchestratingAnalyzer : ITextAnalyzer, ITextAnalysisProgressSource, IDisposable
{
    private readonly ITextAnalyzer _rules;
    private readonly ITextAnalyzer _spell;
    private readonly WriteLiteLanguageEngine _engine;
    private readonly WriteLiteIssueMerger _merger = new();
    private readonly IssueRenderingPipeline _issuePipeline = new();
    private readonly UserDictionaryService _dictionary;
    private readonly WriteLiteIgnoreService _ignore;
    private readonly Func<string, bool>? _isKnownWord;
    private readonly ILexicalSignalSource? _signals;
    /// <summary>
    /// WriteLite-Punctuation-v1, loaded on first use and retained.
    /// </summary>
    /// <remarks>
    /// <para>Lazy because loading costs 343–428 ms of ONNX session construction, and paying it
    /// during startup would put it on the path to the first window — for a layer that is not
    /// consulted until an orchestrated analysis runs, which is at least one debounce interval
    /// after the user starts typing.</para>
    ///
    /// <para>Retained afterwards because §35 asks whether a 29 MB model is cheaper kept loaded
    /// than reloaded: measured, the answer is yes by a wide margin — 48 MB of RSS held once
    /// against 400 ms of load latency on every analysis pass. <c>LazyThreadSafetyMode</c> is
    /// left at its default, so the two analysis passes that can race here construct one
    /// session between them rather than two.</para>
    /// </remarks>
    private readonly Lazy<PunctuationModelAnalyzer?>? _punctuation;

    /// <summary>
    /// One punctuation inference at a time, process-wide for this analyzer.
    /// </summary>
    /// <remarks>
    /// The ONNX session is created with <c>IntraOpNumThreads = 1</c> so that a background
    /// analysis never takes the machine away from the person typing. Letting several analyses
    /// enter it concurrently would give that budget back one thread at a time, and analyses do
    /// overlap: a keystroke burst cancels a pass that has already started, and the replacement
    /// pass begins before the old one has noticed. The gate is what keeps the measured cost of
    /// this layer — +48 MB, +10 ms p50 — a property of the layer rather than of typing speed.
    /// </remarks>
    private readonly SemaphoreSlim _punctuationGate = new(1, 1);
    private readonly object _progressGate = new();
    private WriteLiteAppSettings _settings = new();
    private TextAnalysisProgress _lastProgress = TextAnalysisProgress.Complete(string.Empty);
    private bool _disposed;

    /// <param name="isKnownWord">
    /// Asks WriteLite's own Russian lexicon whether a word exists. Optional; when null the
    /// analyzer behaves exactly as before.
    /// </param>
    /// <remarks>
    /// The lexicon predicate exists to settle disagreements about *whether a word is a
    /// word*. WriteLite indexes 3.09 M Russian surface forms including a curated modern
    /// vocabulary pack — internet and gaming slang, anglicisms, obscenities. LanguageTool
    /// ships a smaller general dictionary and does not know that register, so it reports
    /// spelling errors on words the user spelled correctly.
    ///
    /// Measured on the frozen corpus: enabling LanguageTool moved slang preservation from
    /// 0.957 to 0.717 and profanity preservation from 0.885 to 0.385. Since
    /// ExtendedChecking defaults to true, that is what users get.
    /// </remarks>
    /// <param name="punctuationModelFactory">
    /// Constructs WriteLite-Punctuation-v1, or returns null when it is not deployed. Called at
    /// most once, on the first analysis that wants it. Suggestion-only: the layer never
    /// auto-applies, never removes a comma, and yields to any layer that already has an
    /// opinion about the same boundary. Absent, the deterministic pipeline is unchanged.
    /// </param>
    public WriteLiteOrchestratingAnalyzer(
        ITextAnalyzer rules,
        ITextAnalyzer spell,
        WriteLiteLanguageEngine engine,
        UserDictionaryService dictionary,
        WriteLiteIgnoreService ignore,
        Func<string, bool>? isKnownWord = null,
        ILexicalSignalSource? lexicalSignals = null,
        Func<PunctuationModelAnalyzer?>? punctuationModelFactory = null)
    {
        _rules = rules;
        _spell = spell;
        _engine = engine;
        _dictionary = dictionary;
        _ignore = ignore;
        _isKnownWord = isKnownWord;
        _signals = lexicalSignals;
        _punctuation = punctuationModelFactory is null
            ? null
            : new Lazy<PunctuationModelAnalyzer?>(punctuationModelFactory);
        _engine.AnalysisProgressChanged += (_, progress) => ReportProgress(progress);
    }

    /// <summary>
    /// The deployed punctuation model's version, or null when none is loaded.
    /// </summary>
    /// <remarks>Does not force the load: reports what is loaded, not what could be.</remarks>
    public string? PunctuationModelVersion
        => _punctuation is { IsValueCreated: true } lazy ? lazy.Value?.ModelVersion : null;

    /// <summary>
    /// Decides whether an issue raised by the external engine survives WriteLite's own
    /// lexical knowledge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three rules, in order of how strongly they protect the user's text:
    /// </para>
    /// <list type="number">
    /// <item><b>User dictionary wins outright.</b> A word the user explicitly added is not
    /// an error of any kind, and no external engine gets to say otherwise.</item>
    /// <item><b>Protected register survives spelling and style claims.</b> Slang, obscenity,
    /// proper names, abbreviations and borrowings are absent from a general Russian
    /// dictionary, so LanguageTool reports them as misspellings. Measured: enabling it
    /// dropped slang preservation 0.957 → 0.717 and profanity 0.885 → 0.385.</item>
    /// <item><b>Otherwise the lexicon settles existence questions only.</b> A word the
    /// 3.09 M-form index knows is not a spelling error — but the engine keeps its grammar
    /// and punctuation claims, which are about arrangement, not existence.</item>
    /// </list>
    /// <para>
    /// Deliberately narrow: this never suppresses a punctuation or grammar finding, because
    /// knowing a word exists says nothing about whether it belongs where it was written.
    /// </para>
    /// </remarks>
    private bool KeepEngineIssue(TextIssue issue)
    {
        var original = issue.Original?.Trim() ?? string.Empty;
        if (original.Length == 0)
        {
            return true;
        }

        if (_dictionary.Contains(original))
        {
            return false;
        }

        var signals = SignalsForSpan(original);

        // Obscenity is recognised vocabulary, never a correction target. Sanitising what
        // someone deliberately wrote is a worse failure than missing a real error.
        if (signals is { IsObscene: true })
        {
            return false;
        }

        if (signals is { HasProtectedRegister: true }
            && issue.Category is IssueCategory.Orthography or IssueCategory.Style)
        {
            return false;
        }

        if (issue.Category == IssueCategory.Orthography && IsLexiconWord(original))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Signals for a flagged span. Single-token spans only: a multi-word span has no single
    /// register, and guessing one would suppress findings that deserve to be seen.
    /// </summary>
    private LexicalSignals? SignalsForSpan(string original)
    {
        if (_signals is null) return null;

        var parts = original.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 1) return null;

        var word = parts[0].Trim('.', ',', '!', '?', ';', ':', '"', '«', '»', '(', ')', '—', '-');
        if (word.Length == 0 || !word.All(char.IsLetter)) return null;

        try
        {
            return _signals.For(word);
        }
        catch (Exception exception)
        {
            // A transient engine failure simply yields no signal; log it so repeated misses are visible.
            CompatibilityLogger.Technical("orchestrator-signals-lookup-failed", exception);
            return null;
        }
    }

    /// <summary>
    /// True when every alphabetic token the engine flagged is a word WriteLite's lexicon
    /// knows. Multi-word spans must be wholly known — one unknown token is enough for the
    /// engine's claim to stand.
    /// </summary>
    private bool IsLexiconWord(string original)
    {
        if (_isKnownWord is null) return false;

        var token = original.Trim();
        if (token.Length == 0) return false;

        var any = false;
        foreach (var part in token.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = part.Trim('.', ',', '!', '?', ';', ':', '"', '«', '»', '(', ')', '—', '-');
            if (word.Length == 0 || !word.All(char.IsLetter)) continue;

            any = true;
            try
            {
                if (!_isKnownWord(word)) return false;
            }
            catch (Exception exception)
            {
                // A lexicon lookup failure cannot prove a word known; treat as unknown.
                CompatibilityLogger.Technical("orchestrator-lexicon-lookup-failed", exception);
                return false;
            }
        }

        return any;
    }

    public WriteLiteLanguageEngine Engine => _engine;

    public UserDictionaryService Dictionary => _dictionary;

    public WriteLiteIgnoreService Ignore => _ignore;

    public event EventHandler<TextAnalysisProgress>? AnalysisProgressChanged;

    public TextAnalysisProgress GetProgress(string text)
    {
        lock (_progressGate)
        {
            return string.Equals(_lastProgress.Text, text ?? string.Empty, StringComparison.Ordinal)
                ? _lastProgress
                : TextAnalysisProgress.Pending(text ?? string.Empty);
        }
    }

    public void ApplySettings(WriteLiteAppSettings settings)
    {
        _settings = settings ?? new WriteLiteAppSettings();
    }

    public IReadOnlyList<TextIssue> Analyze(string text)
        => AnalyzeAsync(text).GetAwaiter().GetResult();

    public async Task<IReadOnlyList<TextIssue>> AnalyzeAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !_settings.CheckingEnabled)
        {
            return [];
        }

        ReportProgress(TextAnalysisProgress.Pending(text));
        _ignore.NotifyText(text);
        var sw = Stopwatch.StartNew();

        // Local analyzers always run (baseline WriteLite).
        var rulesTask = Task.Run(() => _rules.AnalyzeAsync(text, cancellationToken), cancellationToken);
        var spellTask = Task.Run(() => _spell.AnalyzeAsync(text, cancellationToken), cancellationToken);
        var engineTask = _settings.ExtendedChecking
            ? _engine.AnalyzeAsync(text, cancellationToken)
            : Task.FromResult<IReadOnlyList<TextIssue>>([]);

        await Task.WhenAll(rulesTask, spellTask, engineTask).ConfigureAwait(false);

        var rules = await rulesTask.ConfigureAwait(false);
        var spell = await spellTask.ConfigureAwait(false);
        var engine = await engineTask.ConfigureAwait(false);

        var engineFiltered = engine.Where(KeepEngineIssue).ToList();

        var spellFiltered = spell
            .Where(i => !_dictionary.Contains(i.Original.Trim()))
            .ToList();

        // Style runs beside the other layers rather than through the merge: its findings are
        // never replacements competing for a span, they are observations about spans other
        // layers have no opinion on, and putting them through conflict resolution would only
        // give a grammar finding the chance to suppress an unrelated stylistic note.
        var style = _settings.EnableStyle
            ? StyleFindings(text)
            : [];

        var merged = _merger.Merge(rules, spellFiltered, engineFiltered);
        var filtered = _ignore.Filter(style.Count == 0
            ? merged.Issues
            : merged.Issues.Concat(style).OrderBy(i => i.Start).ToList());

        // Also drop dictionary words from final list.
        filtered = filtered
            .Where(i => i.Category != IssueCategory.Orthography
                        || !_dictionary.Contains(i.Original.Trim()))
            .Where(IsCategoryEnabled)
            .Select(SanitizeLocalIssue)
            .Where(i => i is not null)
            .Select(i => i!)
            .ToList();

        filtered = _issuePipeline.Filter(text, filtered).ToList();

        var punctuation = await RunPunctuationModelAsync(text, filtered, cancellationToken)
            .ConfigureAwait(false);
        if (punctuation.Count > 0)
        {
            filtered = filtered.Concat(punctuation).OrderBy(i => i.Start).ToList();
        }

        // When "safe apply all" is on, leave CanApplyAutomatically as mapped.
        // When off, allow auto-apply for any issue that has a replacement (still sanitized).
        if (!_settings.SafeApplyAll)
        {
            filtered = filtered
                .Select(i => string.IsNullOrEmpty(i.Replacement)
                    ? i
                    : i with { CanApplyAutomatically = true })
                .ToList();
        }

        sw.Stop();
        CompatibilityLogger.Technical(
            "language-engine-request-completed",
            $"textLength={text.Length} issueCount={filtered.Count} safeCount={filtered.Count(i => i.CanApplyAutomatically)} durationMs={sw.ElapsedMilliseconds} status={_engine.EngineState}");

        var engineProgress = _settings.ExtendedChecking
            ? _engine.GetProgress(text)
            : TextAnalysisProgress.Complete(text);
        ReportProgress(engineProgress.IsComplete
            ? TextAnalysisProgress.Complete(text)
            : new TextAnalysisProgress(
                text,
                engineProgress.CheckedCharacters,
                text.Length,
                IsComplete: false,
                IncompleteReason: engineProgress.IncompleteReason ?? "extended-analysis-incomplete"));

        return filtered;
    }

    /// <summary>
    /// Stylistic observations for the profile the user is writing in.
    /// </summary>
    /// <remarks>
    /// Constructed per call because the profile is a setting and can change between analyses;
    /// the type holds one enum and its patterns are static, so this is not a per-analysis cost
    /// worth caching around. Failure is contained for the same reason as the punctuation
    /// model: a stylistic note is the least important thing on the card and must never be the
    /// reason a spelling correction did not arrive.
    /// </remarks>
    private IReadOnlyList<TextIssue> StyleFindings(string text)
    {
        try
        {
            var analyzer = new RussianStyleAnalyzer(
                StyleProfiles.Parse(_settings.StyleProfile),
                _signals);
            return analyzer.Analyze(text, ProtectedTextSpans.Find(text));
        }
        catch (RegexMatchTimeoutException)
        {
            CompatibilityLogger.Technical("style-analysis-timeout", $"textLength={text.Length}");
            return [];
        }
    }

    /// <summary>
    /// WriteLite-Punctuation-v1 over what the deterministic layers left, off the caller's
    /// thread and one at a time.
    /// </summary>
    /// <remarks>
    /// <para>Runs last and sees the merged, filtered list, which is how it stands down where a
    /// rule already spoke. It is appended rather than merged back through
    /// <see cref="WriteLiteIssueMerger"/> because the merge has already decided which layer
    /// owns each span, and re-running it would let a probability displace an explanation.</para>
    ///
    /// <para><b>Failure is contained here.</b> §33 of the Phase 7 brief: a model that throws,
    /// times out or was never deployed must leave the editor and the deterministic pipeline
    /// working. Cancellation is re-thrown because that is the caller's own signal; everything
    /// else is logged and swallowed, and the pass returns what the deterministic layers
    /// found.</para>
    /// </remarks>
    private async Task<IReadOnlyList<TextIssue>> RunPunctuationModelAsync(
        string text,
        IReadOnlyList<TextIssue> existing,
        CancellationToken cancellationToken)
    {
        if (_punctuation is null || _disposed) return [];
        if (!_settings.EnablePunctuation || !_settings.PunctuationModelEnabled) return [];

        try
        {
            await _punctuationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            return [];
        }

        try
        {
            var protectedSpans = ProtectedTextSpans.Find(text);
            return await Task.Run(
                () =>
                {
                    // Inside Task.Run so that the one-off session construction is off the
                    // caller's thread too, not only the inference.
                    var analyzer = _punctuation.Value;
                    return analyzer is null
                        ? []
                        : analyzer.Analyze(text, existing, protectedSpans, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical(
                "punctuation-model-failed",
                $"type={ex.GetType().Name} message={ex.Message}");
            return [];
        }
        finally
        {
            try { _punctuationGate.Release(); }
            catch (ObjectDisposedException) { } // gate may be disposed concurrently during shutdown
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_punctuation is { IsValueCreated: true } lazy) lazy.Value?.Dispose();
        _punctuationGate.Dispose();
    }

    private void ReportProgress(TextAnalysisProgress progress)
    {
        lock (_progressGate)
        {
            _lastProgress = progress;
        }

        AnalysisProgressChanged?.Invoke(this, progress);
    }

    private static TextIssue? SanitizeLocalIssue(TextIssue issue)
    {
        var replacement = issue.Replacement;
        if (replacement is not null)
        {
            replacement = TextOutputSanitizer.SanitizeReplacement(issue.Original, replacement);
            if (replacement is null)
            {
                return null;
            }
        }

        var safe = issue.CanApplyAutomatically;
        if (safe && TextOutputSanitizer.MessageContradictsSafeApply(issue.Explanation, safe))
        {
            safe = false;
        }

        if (string.Equals(issue.Original, replacement, StringComparison.Ordinal))
        {
            return null;
        }

        return issue with { Replacement = replacement, CanApplyAutomatically = safe };
    }

    private bool IsCategoryEnabled(TextIssue issue)
    {
        // Repeats
        if (issue.LinguisticCategory == LinguisticIssueCategory.RepeatedWord)
        {
            return _settings.EnableRepeats;
        }

        // Typography (spaces / readability formatting)
        if (issue.Category == IssueCategory.Readability
            || issue.LinguisticCategory is LinguisticIssueCategory.ExtraSpace
                or LinguisticIssueCategory.MissingSpace)
        {
            return _settings.EnableTypography || _settings.EnableStyle;
        }

        return issue.Category switch
        {
            IssueCategory.Orthography => _settings.EnableOrthography,
            IssueCategory.Punctuation => _settings.EnablePunctuation,
            IssueCategory.Grammar => _settings.EnableGrammar,
            IssueCategory.Style => _settings.EnableStyle,
            _ => true
        };
    }
}
