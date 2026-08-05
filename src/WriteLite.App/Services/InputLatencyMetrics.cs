using System.Collections.Concurrent;
using System.Diagnostics;

namespace WriteLite.Services;

public sealed class InputLatencyMetrics
{
    private readonly ConcurrentQueue<double> _samples = new();
    private readonly int _capacity;

    public InputLatencyMetrics(int capacity = 1024) => _capacity = Math.Max(32, capacity);

    public void Record(TimeSpan duration)
    {
        _samples.Enqueue(Math.Max(0, duration.TotalMilliseconds));
        while (_samples.Count > _capacity) _samples.TryDequeue(out _);
    }

    public IDisposable Measure() => new Measurement(this);

    public InputLatencySnapshot Snapshot()
    {
        var values = _samples.ToArray();
        Array.Sort(values);
        if (values.Length == 0) return new InputLatencySnapshot(0, 0, 0, 0);
        return new InputLatencySnapshot(values.Length, Percentile(values, 0.50), Percentile(values, 0.95), values[^1]);
    }

    private static double Percentile(double[] values, double percentile)
        => values[Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1)];

    private sealed class Measurement(InputLatencyMetrics owner) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        public void Dispose() => owner.Record(Stopwatch.GetElapsedTime(_started));
    }
}

public readonly record struct InputLatencySnapshot(int Count, double P50Milliseconds, double P95Milliseconds, double MaxMilliseconds);

public static class InputSignalScheduler
{
    public static void Signal(
        ref CancellationTokenSource? current,
        ref int version,
        InputLatencyMetrics metrics,
        Action<int, CancellationToken> schedule)
    {
        using var measurement = metrics.Measure();
        current?.Cancel();
        current?.Dispose();
        var cancellation = new CancellationTokenSource();
        current = cancellation;
        schedule(++version, cancellation.Token);
    }
}
