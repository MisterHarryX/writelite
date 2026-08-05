using System.Diagnostics;
using System.Windows.Threading;

namespace WriteLite.Services;

public enum UiResponsivenessLevel
{
    Normal,
    Delayed,
    HangSuspected
}

public sealed class UiResponsivenessSnapshot
{
    public UiResponsivenessLevel Level { get; init; }
    public double LastDelayMs { get; init; }
    public double MaxDelayMs { get; init; }
    public int DelayOver250Count { get; init; }
    public int DelayOver2000Count { get; init; }
    public DateTimeOffset LastSampleUtc { get; init; }
}

/// <summary>
/// Lightweight dispatcher lag probe. Does not kill the process; only measures and logs metadata (no user text).
///
/// The probe MUST run on its own background thread. The previous implementation
/// used a DispatcherTimer — a timer that fires on the very dispatcher being
/// measured. While the dispatcher was blocked the timer could not tick, and
/// once it unblocked, tick and probe ran back to back with a near-zero gap.
/// The result: two OS-recorded AppHangB1 events in one afternoon and not a
/// single ui-delay line in the log. A watchdog that shares the watched thread
/// can never see that thread stop.
///
/// Now a plain Thread posts a probe onto the dispatcher and measures the
/// round-trip from OUTSIDE. When the probe has not returned after the hang
/// threshold, the background thread logs immediately — the one moment logging
/// on the dispatcher itself would be impossible.
/// </summary>
public sealed class UiResponsivenessWatchdog : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _stopSignal = new(false);
    private Thread? _thread;
    private double _lastDelayMs;
    private double _maxDelayMs;
    private int _over250;
    private int _over2000;
    private DateTimeOffset _lastSampleUtc = DateTimeOffset.UtcNow;
    private Func<string>? _contextProvider;

    /// <summary>Probe round-trips above this are logged as delays.</summary>
    private const double DelayThresholdMs = 250;

    /// <summary>Probes outstanding this long are logged as suspected hangs.</summary>
    private const double HangThresholdMs = 2000;

    public UiResponsivenessWatchdog(Dispatcher dispatcher, TimeSpan? interval = null)
    {
        _dispatcher = dispatcher;
        _interval = interval ?? TimeSpan.FromMilliseconds(400);
    }

    public event EventHandler<UiResponsivenessSnapshot>? SnapshotChanged;

    public void SetContextProvider(Func<string> provider) => _contextProvider = provider;

    public void Start()
    {
        if (_thread is not null) return;
        _stopSignal.Reset();
        _thread = new Thread(ProbeLoop)
        {
            Name = "WriteLite.UiWatchdog",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _stopSignal.Set();
        _thread = null;
    }

    private void ProbeLoop()
    {
        while (!_stopSignal.Wait(_interval))
        {
            if (_dispatcher.HasShutdownStarted) return;

            var probe = new ManualResetEventSlim(false);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                _ = _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(probe.Set));
            }
            catch (Exception)
            {
                // Dispatcher shutting down between the check and the post.
                return;
            }

            // Wait in hang-threshold slices so a genuine hang is reported the
            // moment it crosses the line, not after the dispatcher recovers.
            var reportedHang = false;
            while (!probe.Wait(TimeSpan.FromMilliseconds(HangThresholdMs)))
            {
                if (_stopSignal.IsSet || _dispatcher.HasShutdownStarted) return;
                if (!reportedHang)
                {
                    reportedHang = true;
                    // Logging happens HERE, on the background thread, because
                    // the dispatcher — the usual logging context — is exactly
                    // what is stuck right now.
                    CompatibilityLogger.Technical(
                        "ui-hang-suspected",
                        $"outstandingMs={stopwatch.Elapsed.TotalMilliseconds:F0} {SafeContext()}");
                }
            }

            stopwatch.Stop();
            RecordSample(stopwatch.Elapsed.TotalMilliseconds, reportedHang);
        }
    }

    private void RecordSample(double delayMs, bool hangAlreadyLogged)
    {
        UiResponsivenessSnapshot snap;
        lock (_gate)
        {
            _lastDelayMs = delayMs;
            _maxDelayMs = Math.Max(_maxDelayMs, delayMs);
            _lastSampleUtc = DateTimeOffset.UtcNow;
            if (delayMs >= DelayThresholdMs) _over250++;
            if (delayMs >= HangThresholdMs) _over2000++;
            snap = BuildSnapshotUnlocked();
        }

        if (delayMs >= HangThresholdMs)
        {
            if (!hangAlreadyLogged)
            {
                CompatibilityLogger.Technical(
                    "ui-hang-suspected",
                    $"delayMs={delayMs:F0} maxMs={_maxDelayMs:F0} {SafeContext()}");
            }
            else
            {
                // The in-flight report carried no total; record how long the
                // stall actually lasted once the probe finally lands.
                CompatibilityLogger.Technical(
                    "ui-hang-recovered",
                    $"delayMs={delayMs:F0} maxMs={_maxDelayMs:F0}");
            }
        }
        else if (delayMs >= DelayThresholdMs)
        {
            CompatibilityLogger.Technical("ui-delay", $"delayMs={delayMs:F0} {SafeContext()}");
        }

        SnapshotChanged?.Invoke(this, snap);
    }

    public UiResponsivenessSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return BuildSnapshotUnlocked();
            }
        }
    }

    private string SafeContext()
    {
        try { return _contextProvider?.Invoke() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private UiResponsivenessSnapshot BuildSnapshotUnlocked()
    {
        var level = _lastDelayMs >= 2000
            ? UiResponsivenessLevel.HangSuspected
            : _lastDelayMs >= 250
                ? UiResponsivenessLevel.Delayed
                : UiResponsivenessLevel.Normal;

        return new UiResponsivenessSnapshot
        {
            Level = level,
            LastDelayMs = _lastDelayMs,
            MaxDelayMs = _maxDelayMs,
            DelayOver250Count = _over250,
            DelayOver2000Count = _over2000,
            LastSampleUtc = _lastSampleUtc
        };
    }

    public void Dispose()
    {
        _stopSignal.Set();
        _thread = null;
        _stopSignal.Dispose();
    }
}
