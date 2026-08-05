namespace WriteLite.Services.LanguageEngine;

public interface IWriteLiteLanguageEngineHost : IAsyncDisposable, IDisposable
{
    WriteLiteLanguageEngineState State { get; }

    bool IsReady { get; }

    int? Port { get; }

    string? BaseAddress { get; }

    int? ProcessId { get; }

    string? LastFailureCode { get; }

    event EventHandler? StateChanged;

    /// <summary>Idempotent start. Concurrent calls share a single process.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Alias for StartAsync for call sites that prefer EnsureStarted naming.</summary>
    Task EnsureStartedAsync(CancellationToken cancellationToken = default);

    /// <summary>Idempotent stop of the owned process only.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
