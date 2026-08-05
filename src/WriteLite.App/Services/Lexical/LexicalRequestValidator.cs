namespace WriteLite.Services.Lexical;

/// <summary>
/// Keeps lexical popup actions tied to the exact target and text generation that
/// created the card.  A stale card must never replace a word in a later field.
/// </summary>
public static class LexicalRequestValidator
{
    public static bool IsCurrent(
        string? currentTargetId,
        int currentGenerationId,
        long currentTextVersion,
        string? requestTargetId,
        int requestGenerationId,
        long requestTextVersion) =>
        !string.IsNullOrWhiteSpace(currentTargetId)
        && string.Equals(currentTargetId, requestTargetId, StringComparison.Ordinal)
        && currentGenerationId == requestGenerationId
        && currentTextVersion == requestTextVersion;
}
