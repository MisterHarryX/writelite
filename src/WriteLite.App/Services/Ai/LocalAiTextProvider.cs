using System.IO;
using WriteLite.AI.Contracts;
using WriteLite.AI.Local;

namespace WriteLite.Services.Ai;

/// <summary>
/// Bridges <see cref="ILocalAiTextAnalyzer"/> into the existing <see cref="IAiTextProvider"/> pipeline.
/// When Qwen loopback is healthy, analysis goes through WriteLite-Qwen; otherwise Lite rules assist.
/// </summary>
public sealed class LocalAiTextProvider : IAiTextProvider, IAsyncDisposable, IDisposable
{
    private readonly LocalAiTextAnalyzer _analyzer;
    private readonly AiModelProfile _profile;
    private bool _disposed;

    public LocalAiTextProvider(LocalAiOptions? options = null)
    {
        options ??= new LocalAiOptions();
        _profile = options.Profile;
        var modelDir = options.ModelDirectory
                       ?? Path.Combine(AppContext.BaseDirectory, "models", "writelight-qwen");
        var qwen = new QwenModelBackend(modelDir, options.QwenEndpoint);
        if (options.TimeoutSeconds is > 0)
        {
            qwen.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds.Value);
        }

        // Ретраи/восстановление Qwen: значения из engine-config (model-секция) доходят до
        // бэкенда только здесь — AI.Local не ссылается на WriteLiteDefaults. Дефолты бэкенда
        // совпадают с конфигом, поэтому без этой завязки правки конфига молча терялись.
        qwen.WedgeThreshold = WriteLiteDefaults.Model.QwenWedgeThreshold;
        qwen.RecoveryCooldown = WriteLiteDefaults.Model.QwenRecoveryCooldown;
        qwen.MaxRecoveryAttempts = WriteLiteDefaults.Model.QwenMaxRecoveryAttempts;
        qwen.HealthProbeTimeout = WriteLiteDefaults.Model.QwenHealthProbeTimeout;
        qwen.ServerStartupPollInterval = WriteLiteDefaults.Model.QwenServerStartupPollInterval;
        qwen.ServerStartupPollAttempts = WriteLiteDefaults.Model.QwenServerStartupPollAttempts;
        qwen.ProcessKillWaitTimeout = WriteLiteDefaults.Model.QwenProcessKillWait;

        _analyzer = new LocalAiTextAnalyzer(
            modelDirectory: modelDir,
            qwen: qwen);
        _analyzer.MaxInputChars = Math.Clamp(options.MaxInputChars, 64, 100_000);
        _analyzer.MinInputChars = Math.Max(1, options.MinInputChars);
        _analyzer.PreferQwen = options.PreferQwen;
        Enabled = options.Enabled;
    }

    public bool Enabled { get; set; } = true;

    public bool IsConfigured => Enabled && _analyzer.IsAvailable && !_disposed;

    public string ModelVersion => _analyzer.ModelVersion;

    public AiModelProfile ActiveProfile => _analyzer.ActiveProfile;

    public string LastBackend => _analyzer.LastBackend;

    /// <summary>
    /// True only when the local model's own output reached the result. <see cref="LastBackend"/>
    /// reports which route was attempted, which stays <c>writelight-qwen</c> even when the
    /// answer was rejected and the deterministic engine produced the text.
    /// </summary>
    public bool LastNeuralOutputUsed => _analyzer.LastNeuralOutputUsed;

    public string? LastErrorCode { get; private set; }

    public string QwenEndpoint => _analyzer.Qwen.ActiveEndpoint;

    public bool QwenAvailable => _analyzer.Qwen.IsAvailable;

    /// <summary>The address the WriteAI local server is answering on.</summary>
    /// <remarks>
    /// Product-facing name for <see cref="QwenEndpoint"/>. The transport type keeps its own
    /// name because it is the name of the protocol it speaks; §3 keeps provenance honest
    /// internally while the product says WriteAI.
    /// </remarks>
    public string WriteAiEndpoint => QwenEndpoint;

    /// <summary>Whether WriteAI's local model is reachable.</summary>
    public bool WriteAiAvailable => QwenAvailable;

    /// <summary>
    /// The local runtime, shared with the editor's rewriting service.
    /// </summary>
    /// <remarks>
    /// Exposed rather than duplicated: a second backend would probe, and possibly
    /// start, a second copy of the loopback server, doubling the memory the local
    /// model costs for no benefit. One process, one runtime.
    /// </remarks>
    public QwenModelBackend Backend => _analyzer.Qwen;

    /// <param name="startBackend">
    /// Whether to start the WriteAI model server, or only to notice one that is already
    /// running. False from application startup — see the remarks on
    /// <see cref="LocalAiTextAnalyzer.WarmupAsync"/> for the 481 MB this saves.
    /// </param>
    public async Task WarmupAsync(bool startBackend = true, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return;
        }

        await _analyzer.WarmupAsync(startBackend, cancellationToken).ConfigureAwait(false);
        CompatibilityLogger.Technical(
            "local-ai-provider-warmup",
            $"qwenAvailable={(QwenAvailable ? 1 : 0)} endpoint={QwenEndpoint} profile={ActiveProfile}");
    }

    public async Task<AiTextAnalysisResponse> AnalyzeAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        LastErrorCode = null;
        if (!IsConfigured || string.IsNullOrWhiteSpace(text))
        {
            CompatibilityLogger.Technical("local-ai-provider-skip", "reason=not-configured-or-empty");
            return new AiTextAnalysisResponse { Issues = [] };
        }

        CompatibilityLogger.Technical(
            "local-ai-provider-analyze",
            $"textLength={text.Length} profile={_profile} preferQwen={(_analyzer.PreferQwen ? 1 : 0)}");

        try
        {
            var result = await _analyzer.AnalyzeAsync(
                new AiTextAnalysisRequest(
                    Text: text,
                    Language: null,
                    Mode: AiAnalysisMode.Correct,
                    SchemaVersion: AiSchema.CurrentVersion,
                    Profile: _profile),
                cancellationToken).ConfigureAwait(false);

            LastErrorCode = result.ErrorCode is AiErrorCodes.Ok or null ? null : result.ErrorCode;

            CompatibilityLogger.Technical(
                "local-ai-provider-result",
                $"backend={result.Backend} profile={result.ProfileUsed} issues={result.Issues.Count} " +
                $"correctedLength={result.CorrectedText.Length} durationMs={result.ProcessingTime.TotalMilliseconds:F0} " +
                $"uncertain={(result.Uncertain ? 1 : 0)} error={result.ErrorCode ?? "OK"}");

            // Whether the model's own output reached the answer, or whether the local rules
            // engine produced it after the model's reply was rejected. Without this the trace
            // could see an inference happen and a result come back, and still not tell the two
            // apart — which is exactly the state a truncated reply leaves the pipeline in.
            Diagnostics.CheckPipelineTracing.Current?.Note(
                $"ai-backend={result.Backend} neuralOutputUsed={(LastNeuralOutputUsed ? 1 : 0)} "
                + $"error={result.ErrorCode ?? "OK"}"
                + (string.IsNullOrWhiteSpace(result.Warning) ? string.Empty : $" warning={result.Warning}"));

            if (result.ErrorCode is AiErrorCodes.Cancelled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var issues = result.Issues.Select(i => new AiTextIssueDto
            {
                Start = i.Start,
                Length = i.Length,
                Original = i.Original,
                Replacement = i.Replacement,
                Type = AiIssueTypeCatalog.ToWireId(i.Type),
                Message = i.Explanation,
                Confidence = i.Confidence,
                SafeToApply = i.SafeToApply && !i.LowConfidence
            }).ToList();

            return new AiTextAnalysisResponse
            {
                CorrectedText = result.CorrectedText,
                Issues = issues
            };
        }
        catch (OperationCanceledException)
        {
            LastErrorCode = AiErrorCodes.Cancelled;
            throw;
        }
        catch (Exception ex)
        {
            LastErrorCode = AiErrorCodes.BackendFailed;
            CompatibilityLogger.Technical(
                "local-ai-provider-error",
                $"type={ex.GetType().Name} fallback=empty");
            return new AiTextAnalysisResponse { Issues = [] };
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _analyzer.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _analyzer.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class LocalAiOptions
{
    public bool Enabled { get; set; } = true;
    public AiModelProfile Profile { get; set; } = AiModelProfile.Standard;
    public string? ModelDirectory { get; set; }
    /// <summary>OpenAI-compatible loopback base, e.g. http://127.0.0.1:8742</summary>
    public string? QwenEndpoint { get; set; }
    public bool PreferQwen { get; set; } = true;
    public int? TimeoutSeconds { get; set; }
    public int MaxInputChars { get; set; } = 12_000;
    public int MinInputChars { get; set; } = 1;
}
