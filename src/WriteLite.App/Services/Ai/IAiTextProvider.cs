namespace WriteLite.Services.Ai;

/// <summary>
/// Provider-agnostic AI text analysis. Implementations must not log user text.
/// </summary>
public interface IAiTextProvider
{
    bool IsConfigured { get; }

    Task<AiTextAnalysisResponse> AnalyzeAsync(
        string text,
        CancellationToken cancellationToken = default);
}
