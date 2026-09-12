using System.Diagnostics;

namespace WriteLite.Services;

public sealed class DebouncedTextAnalyzer
{
    private readonly ITextAnalyzer _analyzer;
    private TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _analysisGate = new(1, 1);
    private CancellationTokenSource? _current;
    private int _latestRequestId;
    private int _activeAnalyses;
    private readonly InputLatencyMetrics _durations = new();

    public DebouncedTextAnalyzer(ITextAnalyzer analyzer, TimeSpan debounce)
    {
        _analyzer = analyzer;
        _debounce = debounce;
    }

    public int StartedCount { get; private set; }
    public int CancelledCount { get; private set; }
    public int CompletedCount { get; private set; }
    public int DroppedAsStaleCount { get; private set; }
    public int MaxConcurrentAnalyses { get; private set; }
    public int ActiveAnalyses => Volatile.Read(ref _activeAnalyses);
    /// <summary>At most one pending (waiting debounce) request: the latest supersedes previous.</summary>
    public int PendingDebounceCount
    {
        get { lock (_gate) return _current is null ? 0 : 1; }
    }
    public TimeSpan MaxDuration { get; private set; }
    public TimeSpan LastDuration { get; private set; }
    public InputLatencySnapshot DurationSnapshot => _durations.Snapshot();

    /// <summary>Updates debounce delay without restarting the monitor.</summary>
    public void SetDebounce(TimeSpan debounce)
    {
        if (debounce < WriteLiteDefaults.Debounce.DebounceSetClampMin)
        {
            debounce = WriteLiteDefaults.Debounce.DebounceSetClampMin;
        }

        if (debounce > WriteLiteDefaults.Debounce.DebounceSetClampMax)
        {
            debounce = WriteLiteDefaults.Debounce.DebounceSetClampMax;
        }

        _debounce = debounce;
    }

    public Task<TextAnalysisResult?> StartAsync(
        string text,
        CancellationToken cancellationToken = default,
        Action? onDebounceElapsed = null)
    {
        CancellationTokenSource linked;
        int requestId;

        lock (_gate)
        {
            _current?.Cancel();
            _current?.Dispose();
            _current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked = _current;
            requestId = ++_latestRequestId;
        }

        return RunAsync(requestId, text, linked.Token, onDebounceElapsed);
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _current?.Cancel();
        }
    }

    private async Task<TextAnalysisResult?> RunAsync(
        int requestId,
        string text,
        CancellationToken cancellationToken,
        Action? onDebounceElapsed)
    {
        try
        {
            await Task.Delay(_debounce, cancellationToken).ConfigureAwait(false);
            onDebounceElapsed?.Invoke();

            await _analysisGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            var active = Interlocked.Increment(ref _activeAnalyses);
            StartedCount++;
            MaxConcurrentAnalyses = Math.Max(MaxConcurrentAnalyses, active);
            CompatibilityLogger.State("analysis-started");

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var issues = await Task.Run(
                    async () => await _analyzer.AnalyzeAsync(text, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);

                stopwatch.Stop();
                _durations.Record(stopwatch.Elapsed);
                LastDuration = stopwatch.Elapsed;
                MaxDuration = MaxDuration > stopwatch.Elapsed ? MaxDuration : stopwatch.Elapsed;
                CompatibilityLogger.State("analysis-duration-ms_" + stopwatch.Elapsed.TotalMilliseconds.ToString("F1"));
                var durationSnapshot = _durations.Snapshot();
                if (durationSnapshot.Count % 16 == 0)
                {
                    CompatibilityLogger.Technical("analysis-latency-percentiles",
                        $"samples={durationSnapshot.Count} p50={durationSnapshot.P50Milliseconds:F1} p95={durationSnapshot.P95Milliseconds:F1} p99={durationSnapshot.P99Milliseconds:F1} max={durationSnapshot.MaxMilliseconds:F1}");
                }

                if (requestId != Volatile.Read(ref _latestRequestId))
                {
                    DroppedAsStaleCount++;
                    CompatibilityLogger.Technical("analysis-dropped-stale", $"request={requestId}");
                    return new TextAnalysisResult(requestId, issues, stopwatch.Elapsed, IsStale: true);
                }

                CompletedCount++;
                CompatibilityLogger.State("analysis-completed");
                return new TextAnalysisResult(requestId, issues, stopwatch.Elapsed, IsStale: false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeAnalyses);
                _analysisGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            CancelledCount++;
            CompatibilityLogger.State("analysis-cancelled");
            return null;
        }
    }
}
