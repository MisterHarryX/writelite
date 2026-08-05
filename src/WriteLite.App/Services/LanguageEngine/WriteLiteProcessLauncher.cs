using System.Diagnostics;

namespace WriteLite.Services.LanguageEngine;

public sealed class WriteLiteProcessLauncher : IWriteLiteProcessLauncher
{
    private readonly IWriteLiteProcessJob? _job;

    public WriteLiteProcessLauncher(IWriteLiteProcessJob? job = null)
    {
        _job = job;
    }

    public IWriteLiteProcessHandle Start(WriteLiteProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var psi = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        var process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new WriteLiteLanguageEngineException(
                "process-start-failed",
                "Local language engine process could not be created.");
        }

        // Assign to job so orphan children die if WriteLite terminates unexpectedly.
        try
        {
            _job?.TryAssign(process);
        }
        catch
        {
            // Job failure must not break engine start.
        }

        // Drain pipes asynchronously so the child cannot block on full buffers.
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return new ProcessHandle(process);
    }

    private sealed class ProcessHandle : IWriteLiteProcessHandle
    {
        private readonly Process _process;
        private bool _disposed;

        public ProcessHandle(Process process)
        {
            _process = process;
            _process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
        }

        public int Id => _process.Id;

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch
                {
                    return true;
                }
            }
        }

        public int ExitCode
        {
            get
            {
                try
                {
                    return _process.HasExited ? _process.ExitCode : -1;
                }
                catch
                {
                    return -1;
                }
            }
        }

        public event EventHandler? Exited;

        public bool TryCloseGracefully()
        {
            try
            {
                if (_process.HasExited)
                {
                    return true;
                }

                // Headless Java servers usually have no main window; still attempt.
                return _process.CloseMainWindow();
            }
            catch
            {
                return false;
            }
        }

        public void KillTree()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // only our child
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (_process.HasExited)
                {
                    return Task.CompletedTask;
                }

                return _process.WaitForExitAsync(cancellationToken);
            }
            catch
            {
                return Task.CompletedTask;
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
                _process.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }
}
