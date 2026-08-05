namespace WriteLite.Services.LanguageEngine;

public interface IWriteLiteProcessLauncher
{
    IWriteLiteProcessHandle Start(WriteLiteProcessStartRequest request);
}

public sealed class WriteLiteProcessStartRequest
{
    public required string FileName { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required string WorkingDirectory { get; init; }
}

public interface IWriteLiteProcessHandle : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    int ExitCode { get; }

    event EventHandler? Exited;

    /// <summary>Best-effort graceful close (may no-op for headless processes).</summary>
    bool TryCloseGracefully();

    void KillTree();

    Task WaitForExitAsync(CancellationToken cancellationToken = default);
}
