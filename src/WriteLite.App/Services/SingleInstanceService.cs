using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;

namespace WriteLite.Services;

/// <summary>
/// Enforces a single WriteLite process via named mutex and activates the first instance via named pipe.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    public const string MutexName = @"Global\WriteLite.Application.SingleInstance";
    public const string PipeName = "WriteLite.Application.Activate";
    public const string ShowMainWindowCommand = "SHOW_MAIN_WINDOW";

    private readonly Mutex? _mutex;
    private readonly bool _isPrimary;
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;
    private Action? _onShowMainWindow;

    private SingleInstanceService(Mutex? ownedMutex, bool isPrimary)
    {
        _mutex = ownedMutex;
        _isPrimary = isPrimary;
    }

    public bool IsPrimaryInstance => _isPrimary;

    /// <summary>
    /// Tries to become the primary instance. If another instance owns the mutex,
    /// signals it to show the main window and returns a secondary handle (not primary).
    /// </summary>
    public static SingleInstanceService Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out var createdNew);
        if (createdNew)
        {
            return new SingleInstanceService(mutex, isPrimary: true);
        }

        try
        {
            // Another process holds the mutex — do not wait forever (stale owners are rare with abandon).
            if (mutex.WaitOne(TimeSpan.FromMilliseconds(200)))
            {
                // We acquired after abandonment.
                return new SingleInstanceService(mutex, isPrimary: true);
            }
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstanceService(mutex, isPrimary: true);
        }

        mutex.Dispose();
        // Signal existing instance, then return secondary marker (no mutex ownership).
        try
        {
            SignalShowMainWindow(TimeSpan.FromMilliseconds(800));
        }
        catch
        {
            // best-effort activation
        }

        // Secondary instances do not own the global mutex.
        return new SingleInstanceService(ownedMutex: null, isPrimary: false);
    }

    public void StartActivationServer(Action onShowMainWindow, Dispatcher dispatcher)
    {
        if (!_isPrimary) return;
        _onShowMainWindow = onShowMainWindow;
        _serverCts = new CancellationTokenSource();
        var token = _serverCts.Token;
        _serverTask = Task.Run(() => RunServerLoopAsync(dispatcher, token), token);
    }

    public static bool SignalShowMainWindow(TimeSpan timeout)
    {
        using var client = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        try
        {
            client.Connect((int)Math.Max(50, timeout.TotalMilliseconds));
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(ShowMainWindowCommand);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task RunServerLoopAsync(Dispatcher dispatcher, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (string.Equals(line?.Trim(), ShowMainWindowCommand, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await dispatcher.InvokeAsync(() => _onShowMainWindow?.Invoke(), DispatcherPriority.Normal);
                    }
                    catch
                    {
                        // dispatcher may be shutting down
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(150, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _serverCts?.Cancel();
            _serverTask?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // ignore
        }

        try
        {
            if (_isPrimary && _mutex is not null)
            {
                try { _mutex.ReleaseMutex(); } catch { /* already released */ }
                _mutex.Dispose();
            }
        }
        catch
        {
            // ignore
        }

        _serverCts?.Dispose();
    }
}

/// <summary>Exit codes for secondary instance short-circuit.</summary>
public static class WriteLiteExitCodes
{
    public const int SecondaryInstanceActivatedPrimary = 0;
}
