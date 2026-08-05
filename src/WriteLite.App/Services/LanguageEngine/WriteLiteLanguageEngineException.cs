namespace WriteLite.Services.LanguageEngine;

public sealed class WriteLiteLanguageEngineException : Exception
{
    public WriteLiteLanguageEngineException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public WriteLiteLanguageEngineException(string code, string message, Exception inner)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary>Stable internal error code safe for diagnostics (no user text).</summary>
    public string Code { get; }
}
