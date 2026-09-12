using System.IO;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Starts and stops a single local offline language-engine HTTP process (127.0.0.1 only).
/// Technical JAR/class names stay inside this implementation.
/// </summary>
public sealed class WriteLiteLanguageEngineHost : IWriteLiteLanguageEngineHost
{
    private readonly WriteLiteLanguageOptions _options;
    private readonly IWriteLiteJavaResolver _javaResolver;
    private readonly IWriteLiteProcessLauncher _launcher;
    private readonly IWriteLitePortAllocator _ports;
    private readonly IWriteLiteReadinessProbe _readiness;
    private readonly IWriteLiteProcessJob? _job;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IWriteLiteProcessHandle? _process;
    private WriteLiteLanguageEngineState _state;
    private string? _lastFailureCode;
    private int _boundPort;
    private bool _disposed;

    public WriteLiteLanguageEngineHost(
        WriteLiteLanguageOptions? options = null,
        IWriteLiteJavaResolver? javaResolver = null,
        IWriteLiteProcessLauncher? launcher = null,
        IWriteLitePortAllocator? ports = null,
        IWriteLiteReadinessProbe? readiness = null,
        IWriteLiteProcessJob? job = null)
    {
        _options = options ?? new WriteLiteLanguageOptions();
        _javaResolver = javaResolver ?? new WriteLiteJavaResolver();
        // Own a job by default so orphan children die with this process.
        _job = job ?? CreateDefaultJob();
        _launcher = launcher ?? new WriteLiteProcessLauncher(_job);
        _ports = ports ?? new WriteLitePortAllocator();
        _readiness = readiness ?? new WriteLiteReadinessProbe();
        _state = _options.EnableEngine
            ? WriteLiteLanguageEngineState.Stopped
            : WriteLiteLanguageEngineState.Disabled;
    }

    private static IWriteLiteProcessJob? CreateDefaultJob()
    {
        try
        {
            var job = new WindowsJobObject();
            return job.IsAvailable ? job : null;
        }
        catch (Exception exception)
        {
            // Job containment is a hardening layer, not a requirement; start without it.
            CompatibilityLogger.Technical("engine-job-create-failed", $"type={exception.GetType().Name}");
            return null;
        }
    }

    public WriteLiteLanguageEngineState State => _state;

    public bool IsReady =>
        _state == WriteLiteLanguageEngineState.Ready
        && _process is { HasExited: false }
        && _boundPort > 0;

    public int? Port => _boundPort > 0 ? _boundPort : null;

    public string? BaseAddress => _boundPort > 0
        ? $"http://{_options.HostAddress}:{_boundPort}"
        : null;

    public int? ProcessId => _process is { HasExited: false } ? _process.Id : null;

    public string? LastFailureCode => _lastFailureCode;

    public event EventHandler? StateChanged;

    public Task EnsureStartedAsync(CancellationToken cancellationToken = default)
        => StartAsync(cancellationToken);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_options.EnableEngine)
        {
            SetState(WriteLiteLanguageEngineState.Disabled);
            CompatibilityLogger.State("language-engine-unavailable");
            _lastFailureCode = "engine-disabled";
            throw new WriteLiteLanguageEngineException(
                "engine-disabled",
                "Local language engine is disabled.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReady)
            {
                return;
            }

            // Process alive but not ready — continue waiting.
            if (_process is { HasExited: false } && _boundPort > 0)
            {
                SetState(WriteLiteLanguageEngineState.Starting);
                await WaitUntilReadyAsync(_boundPort, cancellationToken).ConfigureAwait(false);
                MarkReady(_boundPort);
                return;
            }

            CleanupProcessHandle();
            ValidateRuntimeLayout();

            var javaPath = _javaResolver.Resolve(_options);
            if (javaPath is null)
            {
                _lastFailureCode = "java-not-found";
                SetState(WriteLiteLanguageEngineState.Unavailable);
                CompatibilityLogger.State("language-engine-unavailable");
                throw new WriteLiteLanguageEngineException(
                    "java-not-found",
                    "Local language engine runtime prerequisites are missing.");
            }

            SetState(WriteLiteLanguageEngineState.Starting);
            CompatibilityLogger.State("language-engine-starting");

            Exception? lastError = null;
            var attempts = 0;
            foreach (var port in _ports.AllocateCandidates(_options.PreferredPort, _options.PortSearchRange))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempts++ >= WriteLiteDefaults.Networking.EngineMaxStartAttempts)
                {
                    break;
                }

                try
                {
                    await StartOnPortAsync(javaPath, port, cancellationToken).ConfigureAwait(false);
                    MarkReady(port);
                    return;
                }
                catch (OperationCanceledException)
                {
                    await StopOwnedProcessAsync(force: true).ConfigureAwait(false);
                    _lastFailureCode = "startup-cancelled";
                    SetState(WriteLiteLanguageEngineState.Stopped);
                    CompatibilityLogger.State("language-engine-timeout");
                    throw;
                }
                catch (WriteLiteLanguageEngineException ex) when (ex.Code is "port-bind-failed" or "process-exited")
                {
                    lastError = ex;
                    await StopOwnedProcessAsync(force: true).ConfigureAwait(false);
                    // try next port
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    await StopOwnedProcessAsync(force: true).ConfigureAwait(false);
                    break;
                }
            }

            _lastFailureCode = lastError is WriteLiteLanguageEngineException wle
                ? wle.Code
                : "engine-start-failed";
            SetState(WriteLiteLanguageEngineState.Failed);
            CompatibilityLogger.State("language-engine-failed");
            CompatibilityLogger.Technical(
                "language-engine-failed",
                $"errorCode={_lastFailureCode}");

            if (lastError is WriteLiteLanguageEngineException known)
            {
                throw known;
            }

            if (lastError is not null)
            {
                throw new WriteLiteLanguageEngineException(
                    "engine-start-failed",
                    "Local language engine failed to start.",
                    lastError);
            }

            throw new WriteLiteLanguageEngineException(
                "engine-start-failed",
                "Local language engine failed to start.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is null && _state is WriteLiteLanguageEngineState.Stopped
                or WriteLiteLanguageEngineState.Disabled
                or WriteLiteLanguageEngineState.Unavailable)
            {
                return;
            }

            SetState(WriteLiteLanguageEngineState.Stopping);
            CompatibilityLogger.State("language-engine-stopping");
            await StopOwnedProcessAsync(force: false).ConfigureAwait(false);
            _boundPort = 0;
            _options.SetBoundPort(0);
            SetState(_options.EnableEngine
                ? WriteLiteLanguageEngineState.Stopped
                : WriteLiteLanguageEngineState.Disabled);
            CompatibilityLogger.State("language-engine-stopped");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            StopOwnedProcessAsync(force: true).GetAwaiter().GetResult();
        }
        catch
        {
            // best effort
        }

        if (_readiness is IDisposable disposableProbe)
        {
            disposableProbe.Dispose();
        }

        _job?.Dispose();
        _gate.Dispose();
        if (_state is not WriteLiteLanguageEngineState.Disabled)
        {
            SetState(WriteLiteLanguageEngineState.Stopped);
            CompatibilityLogger.State("language-engine-stopped");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await StopOwnedProcessAsync(force: true).ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }

        if (_readiness is IDisposable disposableProbe)
        {
            disposableProbe.Dispose();
        }

        _job?.Dispose();
        _gate.Dispose();
        if (_state is not WriteLiteLanguageEngineState.Disabled)
        {
            SetState(WriteLiteLanguageEngineState.Stopped);
            CompatibilityLogger.State("language-engine-stopped");
        }
    }

    private async Task StartOnPortAsync(string javaPath, int port, CancellationToken cancellationToken)
    {
        var classpath = _options.ResolveClasspath();
        var args = new[]
        {
            $"-Xmx{_options.JavaMaxHeap}",
            "-cp",
            classpath,
            "org.languagetool.server.HTTPServer",
            "--port",
            port.ToString()
        };

        var handle = _launcher.Start(new WriteLiteProcessStartRequest
        {
            FileName = javaPath,
            Arguments = args,
            WorkingDirectory = _options.RuntimeDirectory
        });

        _process = handle;
        _boundPort = port;
        _options.SetBoundPort(port);
        handle.Exited += OnOwnedProcessExited;

        CompatibilityLogger.Technical(
            "language-engine-process",
            $"pid={handle.Id} port={port}");

        // Brief settle — if bind failed the process often exits immediately.
        await Task.Delay(WriteLiteDefaults.Networking.LanguageEnginePortBindSettle, cancellationToken).ConfigureAwait(false);
        if (handle.HasExited)
        {
            var exitCode = handle.ExitCode;
            CleanupProcessHandle();
            CompatibilityLogger.Technical(
                "language-engine-failed",
                $"errorCode=port-bind-failed exitCode={exitCode}");
            throw new WriteLiteLanguageEngineException(
                "port-bind-failed",
                "Local language engine could not bind the selected port.");
        }

        try
        {
            await WaitUntilReadyAsync(port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // leave cleanup to caller
            throw;
        }
    }

    private async Task WaitUntilReadyAsync(int port, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _options.StartupTimeout;
        var baseAddress = $"http://{_options.HostAddress}:{port}";
        WriteLiteLoopbackGuard.EnsureAllowed(baseAddress);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_process is null || _process.HasExited)
            {
                var exit = _process?.ExitCode;
                CleanupProcessHandle();
                CompatibilityLogger.Technical(
                    "language-engine-failed",
                    $"errorCode=process-exited exitCode={exit ?? -1}");
                throw new WriteLiteLanguageEngineException(
                    "process-exited",
                    "Local language engine process exited during startup.");
            }

            try
            {
                // Ready only if OUR process is still alive AND endpoint returns valid languages with Russian.
                if (await _readiness.IsReadyAsync(baseAddress, cancellationToken).ConfigureAwait(false))
                {
                    if (_process is { HasExited: false })
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // not ready
            }

            await Task.Delay(_options.ReadyPollInterval, cancellationToken).ConfigureAwait(false);
        }

        _lastFailureCode = "startup-timeout";
        CompatibilityLogger.State("language-engine-timeout");
        throw new WriteLiteLanguageEngineException(
            "startup-timeout",
            "Local language engine did not become ready in time.");
    }

    private void MarkReady(int port)
    {
        _boundPort = port;
        _options.SetBoundPort(port);
        _lastFailureCode = null;
        SetState(WriteLiteLanguageEngineState.Ready);
        CompatibilityLogger.State("language-engine-ready");
        CompatibilityLogger.Technical(
            "language-engine-ready",
            $"port={port} pid={_process?.Id ?? 0}");
    }

    private void OnOwnedProcessExited(object? sender, EventArgs e)
    {
        // Unexpected death while running or ready: surface failed state so recovery can act.
        if (_state is WriteLiteLanguageEngineState.Ready
            or WriteLiteLanguageEngineState.Starting
            or WriteLiteLanguageEngineState.Busy)
        {
            _lastFailureCode = "process-exited";
            _boundPort = 0;
            try
            {
                _options.SetBoundPort(0);
            }
            catch
            {
                // ignore
            }

            SetState(WriteLiteLanguageEngineState.Failed);
            CompatibilityLogger.State("language-engine-failed");
            CompatibilityLogger.Technical(
                "language-engine-failed",
                $"errorCode=process-exited exitCode={_process?.ExitCode ?? -1}");
        }
    }

    private async Task StopOwnedProcessAsync(bool force)
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        process.Exited -= OnOwnedProcessExited;

        try
        {
            if (!process.HasExited)
            {
                if (!force)
                {
                    process.TryCloseGracefully();
                    using var softCts = new CancellationTokenSource(_options.ShutdownTimeout);
                    try
                    {
                        await process.WaitForExitAsync(softCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // fall through to hard kill
                    }
                }

                if (!process.HasExited)
                {
                    process.KillTree();
                    using var hardCts = new CancellationTokenSource(_options.ShutdownTimeout);
                    try
                    {
                        await process.WaitForExitAsync(hardCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // best effort
                    }
                }
            }
        }
        catch
        {
            // never kill unrelated processes
        }
        finally
        {
            CleanupProcessHandle();
        }
    }

    private void CleanupProcessHandle()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            process.Exited -= OnOwnedProcessExited;
            process.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private void ValidateRuntimeLayout()
    {
        var runtime = _options.RuntimeDirectory;
        if (!Directory.Exists(runtime))
        {
            _lastFailureCode = "runtime-missing";
            SetState(WriteLiteLanguageEngineState.Unavailable);
            CompatibilityLogger.State("language-engine-unavailable");
            throw new WriteLiteLanguageEngineException(
                "runtime-missing",
                "Local language engine files are not available.");
        }

        if (!File.Exists(_options.ResolveServerJarPath()))
        {
            _lastFailureCode = "runtime-jar-missing";
            SetState(WriteLiteLanguageEngineState.Unavailable);
            CompatibilityLogger.State("language-engine-unavailable");
            throw new WriteLiteLanguageEngineException(
                "runtime-jar-missing",
                "Local language engine files are not available.");
        }

        if (!Directory.Exists(Path.Combine(runtime, "libs")))
        {
            _lastFailureCode = "runtime-libs-missing";
            SetState(WriteLiteLanguageEngineState.Unavailable);
            CompatibilityLogger.State("language-engine-unavailable");
            throw new WriteLiteLanguageEngineException(
                "runtime-libs-missing",
                "Local language engine files are not available.");
        }
    }

    private void SetState(WriteLiteLanguageEngineState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // Test helper kept for port unit tests.
    public static int FindFreePort(int preferred, int range)
        => WriteLitePortAllocator.FindFreePort(preferred, range);
}
