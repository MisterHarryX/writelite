namespace WriteLite.Language.Core;

/// <summary>Why a correction could not be bound to the text it claims to describe.</summary>
public enum CorrectionBindingStatus
{
    /// <summary>The span exists and holds exactly the recorded original.</summary>
    Bound,

    /// <summary>Start or Length is negative, or the span runs past the end of the text.</summary>
    OutOfRange,

    /// <summary>The span exists but holds something other than the recorded original.</summary>
    OriginalMismatch,

    /// <summary>A zero-length span was recorded together with a non-empty original.</summary>
    Malformed,
}

/// <summary>
/// The one representation of "replace exactly this with exactly that".
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> A correction was previously carried as four loose fields on
/// <see cref="object"/>-shaped records and re-derived at every stage — the card formatted one
/// pair of strings, the apply path resolved another, and the two could disagree without
/// anything noticing. The rule the product is held to is that the card and the write agree
/// character for character, and that rule needs a type it can be stated on.</para>
///
/// <para>An instance can only be obtained through <see cref="TryBind"/>, which is handed the
/// text the correction is supposed to apply to. There is therefore no such thing as a
/// canonical correction that has not been checked against a real string: a finding whose
/// span no longer holds its original cannot be turned into one, and every consumer —
/// display, dictionary, apply — takes the same bound value.</para>
///
/// <para>Offsets are UTF-16 code units, matching <see cref="string"/> indexing and every
/// Windows text API WriteLite talks to. <see cref="TryBind"/> additionally refuses a span
/// that begins or ends between the two halves of a surrogate pair, which is the one way a
/// code-unit offset can name a position that is not a character boundary.</para>
/// </remarks>
public sealed record CanonicalCorrection
{
    private CanonicalCorrection(int start, int length, string original, string replacement)
    {
        Start = start;
        Length = length;
        Original = original;
        Replacement = replacement;
    }

    /// <summary>Offset of the replaced span, in UTF-16 code units.</summary>
    public int Start { get; }

    /// <summary>Length of the replaced span, in UTF-16 code units. Zero for an insertion.</summary>
    public int Length { get; }

    /// <summary>Exactly the text at <see cref="Start"/>..<see cref="Start"/>+<see cref="Length"/>.</summary>
    public string Original { get; }

    /// <summary>Exactly the text that takes its place. Never a diff fragment.</summary>
    public string Replacement { get; }

    /// <summary>The span's exclusive end offset.</summary>
    public int End => Start + Length;

    /// <summary>True when the correction inserts without removing anything.</summary>
    public bool IsInsertion => Length == 0;

    /// <summary>
    /// Binds a claimed correction to <paramref name="text"/>, or explains why it does not fit.
    /// </summary>
    public static CorrectionBindingStatus TryBind(
        string? text,
        int start,
        int length,
        string? original,
        string? replacement,
        out CanonicalCorrection? correction)
    {
        correction = null;
        text ??= string.Empty;
        original ??= string.Empty;
        replacement ??= string.Empty;

        if (start < 0 || length < 0 || start > text.Length || start + length > text.Length)
        {
            return CorrectionBindingStatus.OutOfRange;
        }

        if (SplitsSurrogatePair(text, start) || SplitsSurrogatePair(text, start + length))
        {
            return CorrectionBindingStatus.OutOfRange;
        }

        if (length == 0)
        {
            return original.Length == 0
                ? Bind(start, 0, string.Empty, replacement, out correction)
                : CorrectionBindingStatus.Malformed;
        }

        return string.Equals(text.Substring(start, length), original, StringComparison.Ordinal)
            ? Bind(start, length, original, replacement, out correction)
            : CorrectionBindingStatus.OriginalMismatch;
    }

    /// <summary>The text as it stands after this correction is applied to <paramref name="text"/>.</summary>
    /// <remarks>
    /// The only place the resulting string is ever computed. Callers that need to know what a
    /// field will contain — the apply path's verification, a preview, a test — ask here rather
    /// than repeating <c>Remove</c>/<c>Insert</c> and risking a different answer.
    /// </remarks>
    public string ApplyTo(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Start + Length > text.Length)
        {
            throw new InvalidOperationException("The correction no longer fits the text it is applied to.");
        }

        return text.Remove(Start, Length).Insert(Start, Replacement);
    }

    /// <summary>True when applying this would leave the text exactly as it is.</summary>
    public bool IsNoOp => string.Equals(Original, Replacement, StringComparison.Ordinal);

    private static CorrectionBindingStatus Bind(
        int start,
        int length,
        string original,
        string replacement,
        out CanonicalCorrection? correction)
    {
        correction = new CanonicalCorrection(start, length, original, replacement);
        return CorrectionBindingStatus.Bound;
    }

    private static bool SplitsSurrogatePair(string text, int offset)
        => offset > 0
           && offset < text.Length
           && char.IsHighSurrogate(text[offset - 1])
           && char.IsLowSurrogate(text[offset]);
}
