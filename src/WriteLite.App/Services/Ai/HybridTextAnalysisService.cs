using System.Diagnostics;
using WriteLite.Models;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Settings;

namespace WriteLite.Services.Ai;

/// <summary>
/// Combines local WriteLite analyzers with optional AI analysis.
/// Local results are never replaced wholesale; AI supplements them.
/// Stale AI results are dropped via RequestId + text fingerprint.
/// </summary>
public sealed class HybridTextAnalysisService : ITextAnalyzer, IDisposable
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

    public HybridTextAnalysisService(
        ITextAnalyzer localAnalyzer,
        AiTextAnalysisService? aiAnalysisService = null)
    {
        _local = localAnalyzer ?? throw new ArgumentNullException(nameof(localAnalyzer));
        _ai = aiAnalysisService;
    }

    public ITextAnalyzer LocalAnalyzer => _local;

    public bool AiEnabled
        => _settings.AiCheckingEnabled
           && _ai is { IsAvailable: true };

    public void SetAiService(AiTextAnalysisService? aiAnalysisService)
    {
        _ai = aiAnalysisService;
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

    public async Task<IReadOnlyList<TextIssue>> AnalyzeAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !_settings.CheckingEnabled)
        {
            return [];
        }

        var requestId = Interlocked.Increment(ref _requestId);
        var sw = Stopwatch.StartNew();

        // Always run local pipeline first (rules + spell + offline engine).
        var localTask = _local.AnalyzeAsync(text, cancellationToken);
        var aiTask = AiEnabled
            ? AnalyzeAiCachedAsync(text, requestId, cancellationToken)
            : Task.FromResult<IReadOnlyList<TextIssue>>([]);

        await Task.WhenAll(localTask, aiTask).ConfigureAwait(false);

        // Drop stale hybrid composition if a newer request started.
        if (requestId != Volatile.Read(ref _requestId))
        {
            CompatibilityLogger.State("analysis-cancelled");
            return await localTask.ConfigureAwait(false);
        }

        var local = await localTask.ConfigureAwait(false);
        var ai = await aiTask.ConfigureAwait(false);

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
        sw.Stop();
        CompatibilityLogger.Technical(
            "hybrid-analysis-completed",
            $"textLength={text.Length} localCount={local.Count} aiCount={ai.Count} mergedCount={validated.Count} " +
            $"durationMs={sw.ElapsedMilliseconds} aiEnabled={(AiEnabled ? 1 : 0)}");

        return validated;
    }

    private async Task<IReadOnlyList<TextIssue>> AnalyzeAiCachedAsync(
        string text,
        int requestId,
        CancellationToken cancellationToken)
    {
        if (_ai is null || !AiEnabled)
        {
            return [];
        }

        if (text.Length < Math.Max(8, _settings.AiMinTextLength)
            || text.Length > _settings.AiMaxTextLength)
        {
            return [];
        }

        lock (_cacheGate)
        {
            if (_cacheText is not null
                && string.Equals(_cacheText, text, StringComparison.Ordinal)
                && _cacheIssues is not null
                && DateTimeOffset.UtcNow - _cacheUtc < TimeSpan.FromMinutes(10))
            {
                CompatibilityLogger.Technical("ai-cache-hit", $"textLength={text.Length}");
                return _cacheIssues;
            }
        }

        // Debounce local inference so typing never queues stale Qwen/Lite work.
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

        // Cancel previous in-flight local AI inference.
        CancellationTokenSource linked;
        lock (_cacheGate)
        {
            _aiCts?.Cancel();
            _aiCts?.Dispose();
            _aiCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked = _aiCts;
        }

        try
        {
            CompatibilityLogger.Technical(
                "hybrid-ai-request-started",
                $"textLength={text.Length} requestId={requestId}");
            var issues = await _ai.AnalyzeAsync(text, linked.Token).ConfigureAwait(false);
            if (requestId != Volatile.Read(ref _requestId))
            {
                CompatibilityLogger.Technical("hybrid-ai-stale", $"requestId={requestId}");
                return [];
            }

            lock (_cacheGate)
            {
                _cacheText = text;
                _cacheIssues = issues;
                _cacheUtc = DateTimeOffset.UtcNow;
            }

            CompatibilityLogger.Technical(
                "hybrid-ai-request-completed",
                $"requestId={requestId} issueCount={issues.Count}");
            return issues;
        }
        catch (OperationCanceledException)
        {
            CompatibilityLogger.Technical("hybrid-ai-cancelled", $"requestId={requestId}");
            return [];
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("hybrid-ai-error", $"type={ex.GetType().Name}");
            return [];
        }
    }
}
