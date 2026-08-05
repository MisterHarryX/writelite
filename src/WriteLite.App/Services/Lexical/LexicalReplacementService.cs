namespace WriteLite.Services.Lexical;

/// <summary>
/// Safe single-word replacement for synonym apply. Re-validates target, range, and original.
/// </summary>
public sealed class LexicalReplacementService(IWordMorphologyService? morphology = null) : ILexicalReplacementService
{
    private readonly IWordMorphologyService _morphology = morphology ?? new WordMorphologyService();

    public LexicalReplacementResult TryReplace(LexicalReplacementRequest request)
    {
        if (request.IsPassword)
            return Fail("password field", offerCopy: true);
        if (request.IsReadOnly)
            return Fail("read-only field", offerCopy: true);
        if (!request.SupportsDirectWrite)
            return Fail("write not supported", offerCopy: true);
        if (string.IsNullOrEmpty(request.TargetId))
            return Fail("missing target", offerCopy: true);
        if (string.IsNullOrEmpty(request.CurrentText))
            return Fail("empty text", offerCopy: true);
        if (request.Start < 0 || request.Length <= 0
            || request.Start + request.Length > request.CurrentText.Length)
            return Fail("invalid range", offerCopy: true);

        var currentFragment = request.CurrentText.Substring(request.Start, request.Length);
        if (!string.Equals(currentFragment, request.OriginalWord, StringComparison.Ordinal))
            return Fail("stale range", offerCopy: true);

        if (string.IsNullOrWhiteSpace(request.ReplacementLemmaOrForm))
            return Fail("empty replacement", offerCopy: true);

        // Surrogate-pair safety: do not split pairs at boundaries.
        if (IsInsideSurrogatePair(request.CurrentText, request.Start)
            || IsInsideSurrogatePair(request.CurrentText, request.Start + request.Length))
            return Fail("utf16 boundary", offerCopy: true);

        var language = new LexicalLanguageDetector().DetectWord(request.OriginalWord);
        var applied = _morphology.InflectLike(request.OriginalWord, request.ReplacementLemmaOrForm, language);
        if (string.IsNullOrEmpty(applied))
            return Fail("cannot form safe replacement", offerCopy: true);

        // Refuse if replacement would equal original (no-op) after casing.
        if (string.Equals(applied, request.OriginalWord, StringComparison.Ordinal))
            return Fail("same as original", offerCopy: true);

        var newText = request.CurrentText
            .Remove(request.Start, request.Length)
            .Insert(request.Start, applied);

        // Protect surrounding spaces/punctuation: only the word range was rewritten.
        return new LexicalReplacementResult(
            Success: true,
            NewText: newText,
            CaretIndex: request.Start + applied.Length,
            AppliedForm: applied,
            FailureReason: null);
    }

    private static bool IsInsideSurrogatePair(string text, int index)
    {
        if (index <= 0 || index >= text.Length) return false;
        return char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1]);
    }

    private static LexicalReplacementResult Fail(string reason, bool offerCopy)
        => new(false, null, -1, "", reason, offerCopy);
}
