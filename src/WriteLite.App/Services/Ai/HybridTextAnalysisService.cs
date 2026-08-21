using System.Diagnostics;
using WriteLite.Models;
using WriteLite.Services.Diagnostics;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Settings;

namespace WriteLite.Services.Ai;

/// <summary>
/// Combines local WriteLite analyzers with optional AI analysis.
/// Local results are never replaced wholesale; AI supplements them.
/// Stale AI results are dropped via RequestId + text fingerprint.
/// </summary>
public sealed class HybridTextAnalysisService : IStagedTextAnalyzer, IDisposable
{
    private readonly ITextAnalyzer _local;
    private AiTextAnalysisService? _ai;
    private readonly WriteLiteIssueMerger _merger = new();
    private readonly IssueRenderingPipeline _issuePipeline = new();
    private readonly object _cacheGate = new();
    private WriteLiteAppSettings _settings = new();

    private string? _cacheText;
    private IReadOnlyList<TextIssue>? _cacheIssues;
    private DateTimeOffset _cacheUtc;
    private int _requestId;
    private CancellationTokenSource? _aiCts;

    private readonly ILocalAiRoutingPolicy? _routingPolicy;
    private readonly SentenceAnalysisCache _sentenceCache = new();
    private SentenceAiRouter? _router;

    /// <summary>Where the reader is, so the deep lane spends its budget there first.</summary>
    /// <remarks>
    /// Set by the editor from the caret and left at zero by the field monitor, whose text is
    /// one field and whose first sentence is the only one there is. §50 asks for the current
    /// sentence to be analysed before distant pages; this is the only input that needs.
    /// </remarks>
    private int _priorityOffset;

    /// <param name="routingPolicy">
    /// Decides when the local model is consulted and when its findings are believed.
    /// Null restores Phase 3 behaviour — consult always, accept everything — which is what
    /// the benchmark's unrestricted-Qwen configuration measures.
    /// </param>
    public HybridTextAnalysisService(
        ITextAnalyzer localAnalyzer,
        AiTextAnalysisService? aiAnalysisService = null,
        ILocalAiRoutingPolicy? routingPolicy = null)
    {
        _local = localAnalyzer ?? throw new ArgumentNullException(nameof(localAnalyzer));
        _ai = aiAnalysisService;
        _routingPolicy = routingPolicy;
    }

    /// <summary>Routing counters for this service instance.</summary>
    public AiRoutingMetrics Metrics { get; } = new();

    /// <summary>The most recent routing decision, for diagnostics. Contains no user text.</summary>
    public AiRoutingTrace? LastTrace { get; private set; }

    public ITextAnalyzer LocalAnalyzer => _local;

    public bool AiEnabled
        => _settings.AiCheckingEnabled
           && _ai is { IsAvailable: true };

    /// <summary>Tells the deep lane which part of the document the reader is looking at.</summary>
    public void SetPriorityOffset(int offset) => Volatile.Write(ref _priorityOffset, Math.Max(0, offset));

    /// <summary>The last analysis's per-sentence routing counters, for diagnostics and tests.</summary>
    public SentenceRoutingReport LastRoutingReport => _router?.LastReport ?? SentenceRoutingReport.Empty;

    public void SetAiService(AiTextAnalysisService? aiAnalysisService)
    {
        _ai = aiAnalysisService;
        _router = null;
        _sentenceCache.Clear();
        lock (_cacheGate)
        {
            _cacheText = null;
            _cacheIssues = null;
            _aiCts?.Cancel();
        }
    }

    public void CancelPending()
    {
        Interlocked.Increment(ref _requestId);
        lock (_cacheGate)
        {
            _aiCts?.Cancel();
            _aiCts?.Dispose();
            _aiCts = null;
            _cacheText = null;
            _cacheIssues = null;
        }
    }

    public void Dispose() => CancelPending();

    public void ApplySettings(WriteLiteAppSettings settings)
    {
        _settings = settings ?? new WriteLiteAppSettings();
        if (_local is WriteLiteOrchestratingAnalyzer orch)
        {
            orch.ApplySettings(_settings);
        }

        // Invalidate AI cache when settings change.
        lock (_cacheGate)
        {
            _cacheText = null;
            _cacheIssues = null;
        }
    }

    public IReadOnlyList<TextIssue> Analyze(string text)
        => AnalyzeAsync(text).GetAwaiter().GetResult();

    public Task<IReadOnlyList<TextIssue>> AnalyzeAsync(
        string text,
        CancellationToken cancellationToken = default)
        => AnalyzeStagedAsync(text, static (_, _) => { }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// The deterministic answer is handed to <paramref name="publish"/> the moment it exists,
    /// before the model is consulted at all. That ordering is the whole point: on a 480-word
    /// document the deterministic layers finish in ~1.2 s and the model lane in ~53 s, and
    /// nothing the model returns is allowed to decide when the first 22 findings reach a
    /// sidebar.
    /// </remarks>
    public async Task<IReadOnlyList<TextIssue>> AnalyzeStagedAsync(
        string text,
        Action<AnalysisLane, IReadOnlyList<TextIssue>> publish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publish);
        var trace = CheckPipelineTracing.Current;

        if (string.IsNullOrWhiteSpace(text) || !_settings.CheckingEnabled)
        {
            trace?.Note(string.IsNullOrWhiteSpace(text)
                ? "hybrid-skipped: empty text"
                : "hybrid-skipped: CheckingEnabled=false");
            return [];
        }

        var requestId = Interlocked.Increment(ref _requestId);
        var sw = Stopwatch.StartNew();

        // The deterministic pipeline runs first and alone, because the routing decision is
        // a function of what it found. Phase 3 ran the two in parallel, which meant every
        // sentence paid for an inference whether or not the answer could help — and that
        // was measurably worse: precision 0.945 → 0.713 for no gain in corrections made.
        var local = await _local.AnalyzeAsync(text, cancellationToken).ConfigureAwait(false);

        if (trace is not null)
        {
            trace.DeterministicFindings = local.Count;
            trace.DeterministicReported = true;
        }

        if (requestId != Volatile.Read(ref _requestId))
        {
            CompatibilityLogger.State("analysis-cancelled");
            return local;
        }

        // The deterministic answer leaves for the UI here, not after the model. Everything
        // below this line is supplementary and may take a minute; none of it gates this.
        var deterministic = _issuePipeline.Filter(text, _merger.Merge(local, [], []).Issues);
        publish(AnalysisLane.Deterministic, deterministic);
        CompatibilityLogger.Technical(
            "hybrid-deterministic-published",
            $"requestId={requestId} count={deterministic.Count} elapsedMs={sw.ElapsedMilliseconds}");

        var ai = await ConsultAiAsync(text, local, requestId, cancellationToken).ConfigureAwait(false);

        // Drop stale hybrid composition if a newer request started.
        if (requestId != Volatile.Read(ref _requestId))
        {
            CompatibilityLogger.State("analysis-cancelled");
            return local;
        }

        // Re-check text fingerprint: if caller reused service with different generation, AI must match `text`.
        if (ai.Count > 0)
        {
            ai = ai.Where(i =>
                    i.Start >= 0
                    && i.Start + i.Length <= text.Length
                    && string.Equals(text.Substring(i.Start, i.Length), i.Original, StringComparison.Ordinal))
                .ToList();
        }

        // Local has priority in merger (sourceRank 0/1), AI as engine-like tertiary.
        var merged = _merger.Merge(local, [], ai);
        var validated = _issuePipeline.Filter(text, merged.Issues);

        if (trace is not null)
        {
            trace.MergedAfterDeduplication = validated.Count;
        }

        sw.Stop();
        CompatibilityLogger.Technical(
            "hybrid-analysis-completed",
            $"textLength={text.Length} localCount={local.Count} aiCount={ai.Count} mergedCount={validated.Count} " +
            $"durationMs={sw.ElapsedMilliseconds} aiEnabled={(AiEnabled ? 1 : 0)}");

        return validated;
    }

    /// <summary>
    /// Routes to the local model when the policy says it can help, then keeps only the
    /// findings that clear the acceptance bar.
    /// </summary>
    /// <remarks>
    /// Two separate decisions, deliberately: calling the model and believing it are not the
    /// same question. Phase 3 conflated them and paid for it — every finding the model
    /// produced was merged as a peer of dictionary-backed results.
    /// </remarks>
    private async Task<IReadOnlyList<TextIssue>> ConsultAiAsync(
        string text,
        IReadOnlyList<TextIssue> local,
        int requestId,
        CancellationToken cancellationToken)
    {
        var trace = CheckPipelineTracing.Current;

        if (!AiEnabled)
        {
            trace?.Note(
                $"ai-disabled: AiCheckingEnabled={_settings.AiCheckingEnabled} "
                + $"aiService={(_ai is null ? "none" : "present")} "
                + $"providerConfigured={(_ai is { IsAvailable: true } ? 1 : 0)}");
            if (trace is not null)
            {
                trace.AiRoutingReason = "ai-disabled";
            }

            return [];
        }

        if (_ai is null)
        {
            return [];
        }

        // Debounce before spending anything: typing must never queue inferences that a
        // keystroke two hundred milliseconds later makes irrelevant.
        var aiDebounce = TimeSpan.FromMilliseconds(Math.Clamp(_settings.AiDebounceMs, 200, 2000));
        try
        {
            await Task.Delay(aiDebounce, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return [];
        }

        if (requestId != Volatile.Read(ref _requestId))
        {
            return [];
        }

        CancellationTokenSource linked;
        lock (_cacheGate)
        {
            _aiCts?.Cancel();
            _aiCts?.Dispose();
            _aiCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked = _aiCts;
        }

        var router = _router ??= new SentenceAiRouter(_ai, _routingPolicy, _sentenceCache);

        IReadOnlyList<TextIssue> accepted;
        try
        {
            accepted = await router
                .RouteAsync(text, local, _settings, Volatile.Read(ref _priorityOffset), linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CompatibilityLogger.Technical("hybrid-ai-cancelled", $"requestId={requestId}");
            return [];
        }
        catch (Exception ex)
        {
            // §63: the deterministic findings the caller already has are not affected.
            CompatibilityLogger.Technical("hybrid-ai-error", $"type={ex.GetType().Name}");
            return [];
        }

        if (requestId != Volatile.Read(ref _requestId))
        {
            CompatibilityLogger.Technical("hybrid-ai-stale", $"requestId={requestId}");
            return [];
        }

        var report = router.LastReport;
        Metrics.RecordRouting(report);

        if (trace is not null)
        {
            trace.AiRoutingCandidates = report.Considered;
            trace.AiConsulted = report.Consulted > 0;
            trace.AiRoutingReason = report.Consulted > 0
                ? $"sentence-routing: {report.Consulted}/{report.Sentences} consulted"
                : "sentence-routing: no sentence qualified";
            trace.AiAccepted = accepted.Count;
        }

        LastTrace = new AiRoutingTrace(
            report.Consulted > 0,
            $"sentences={report.Sentences} consulted={report.Consulted}",
            text.Length,
            local.Count,
            report.Offered,
            accepted.Count,
            []);

        return accepted;
    }
}
