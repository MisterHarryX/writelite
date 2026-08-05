namespace WriteLite.Services.LanguageEngine;

public interface IWriteLiteLanguageClient
{
    /// <summary>
    /// Sends a check request to a running local engine.
    /// Does not start the process — call host EnsureStartedAsync first.
    /// Never logs the full text payload.
    /// </summary>
    Task<WriteLiteLanguageResponseDto> CheckAsync(
        string text,
        string? language = null,
        CancellationToken cancellationToken = default);
}
