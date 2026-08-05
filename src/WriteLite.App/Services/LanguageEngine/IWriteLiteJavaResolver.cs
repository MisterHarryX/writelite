namespace WriteLite.Services.LanguageEngine;

public interface IWriteLiteJavaResolver
{
    /// <summary>
    /// Resolves a Java executable once and may cache the result for the resolver lifetime.
    /// </summary>
    string? Resolve(WriteLiteLanguageOptions options);
}
