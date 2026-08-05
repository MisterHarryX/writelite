namespace WriteLite.Services.LanguageEngine;

public interface IWriteLiteReadinessProbe
{
    /// <summary>
    /// Returns true only when the local engine endpoint is ready (HTTP 200, valid JSON, Russian present).
    /// </summary>
    Task<bool> IsReadyAsync(string baseAddress, CancellationToken cancellationToken = default);
}
