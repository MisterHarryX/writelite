using System.Globalization;

namespace WriteLite.Language.Core;

/// <summary>
/// The four fragments a correction card draws, and the guarantee that they still spell the
/// correction.
/// </summary>
/// <param name="Prefix">Text both sides begin with. Rendered once, unemphasised.</param>
/// <param name="ChangedOriginal">The part of the original that is going away.</param>
/// <param name="ChangedReplacement">The part of the replacement that is arriving.</param>
/// <param name="Suffix">Text both sides end with. Rendered once, unemphasised.</param>
public readonly record struct CorrectionDisplayDiff(
    string Prefix,
    string ChangedOriginal,
    string ChangedReplacement,
    string Suffix)
{
    /// <summary>The original, reassembled from what the card shows.</summary>
    public string ReconstructedOriginal => Prefix + ChangedOriginal + Suffix;

    /// <summary>The replacement, reassembled from what the card shows.</summary>
    public string ReconstructedReplacement => Prefix + ChangedReplacement + Suffix;

    /// <summary>True when nothing at all is shared and the card must show both sides whole.</summary>
    public bool IsWholeTokenChange => Prefix.Length == 0 && Suffix.Length == 0;
}

/// <summary>
/// Splits a correction into shared and changed fragments for display, and nothing else.
/// </summary>
/// <remarks>
/// <para><b>What this is not.</b> It is not a source of truth. The text that gets written is
/// <see cref="CanonicalCorrection.Replacement"/>, always; this type exists so a card can
/// emphasise the letters that actually differ without the emphasis becoming the correction.
/// Rebuilding a replacement out of styled fragments is the specific mistake that turned a
/// perfectly good <c>роботает → работает</c> into something that read as two pieces of a
/// word, and <see cref="Compute"/> is written so that the fragments can always be put back
/// together — asserted by <see cref="CorrectionDisplayDiff.ReconstructedOriginal"/> and
/// <see cref="CorrectionDisplayDiff.ReconstructedReplacement"/> and by property tests over
/// generated string pairs.</para>
///
/// <para><b>Why prefix/suffix and not an alignment.</b> A card has one line. The only
/// question it can usefully answer is "which bit changed", and for the corrections this
/// product makes — a letter, an ending, a mark — the longest common prefix and suffix answer
/// it exactly. Where they do not (a phrase rewrite, a transposition), both sides collapse to
/// whole strings, which is honest rather than clever.</para>
///
/// <para>Boundaries are moved back off surrogate pairs and off combining marks, so a shared
/// prefix never ends inside a character the user perceives as one.</para>
/// </remarks>
public static class CorrectionDiff
{
    /// <summary>Splits <paramref name="original"/> and <paramref name="replacement"/> for display.</summary>
    public static CorrectionDisplayDiff Compute(string? original, string? replacement)
    {
        var left = original ?? string.Empty;
        var right = replacement ?? string.Empty;

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return new CorrectionDisplayDiff(left, string.Empty, string.Empty, string.Empty);
        }

        var limit = Math.Min(left.Length, right.Length);
        var prefix = 0;
        while (prefix < limit && left[prefix] == right[prefix]) prefix++;
        prefix = RetreatToBoundary(left, right, prefix);

        var suffix = 0;
        while (suffix < limit - prefix
               && left[left.Length - 1 - suffix] == right[right.Length - 1 - suffix])
        {
            suffix++;
        }

        suffix = RetreatSuffixToBoundary(left, right, prefix, suffix);

        return new CorrectionDisplayDiff(
            left[..prefix],
            left[prefix..(left.Length - suffix)],
            right[prefix..(right.Length - suffix)],
            left[(left.Length - suffix)..]);
    }

    /// <summary>
    /// The invariant every caller depends on: the fragments still spell both sides.
    /// </summary>
    /// <remarks>
    /// Exposed rather than kept private because it is the assertion the regression suite
    /// makes over hundreds of generated pairs, and because a UI that wants to be paranoid
    /// before drawing can make it too.
    /// </remarks>
    public static bool Reconstructs(CorrectionDisplayDiff diff, string? original, string? replacement)
        => string.Equals(diff.ReconstructedOriginal, original ?? string.Empty, StringComparison.Ordinal)
           && string.Equals(diff.ReconstructedReplacement, replacement ?? string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Walks a prefix length back until it sits on a text-element boundary in both strings.
    /// </summary>
    private static int RetreatToBoundary(string left, string right, int prefix)
    {
        while (prefix > 0 && (!IsBoundary(left, prefix) || !IsBoundary(right, prefix)))
        {
            prefix--;
        }

        return prefix;
    }

    private static int RetreatSuffixToBoundary(string left, string right, int prefix, int suffix)
    {
        while (suffix > 0)
        {
            var leftOffset = left.Length - suffix;
            var rightOffset = right.Length - suffix;
            if (leftOffset >= prefix
                && rightOffset >= prefix
                && IsBoundary(left, leftOffset)
                && IsBoundary(right, rightOffset))
            {
                break;
            }

            suffix--;
        }

        return suffix;
    }

    /// <summary>
    /// True when <paramref name="offset"/> is between two user-perceived characters.
    /// </summary>
    /// <remarks>
    /// Surrogate pairs are the case that matters for correctness — half of an emoji is not a
    /// string anyone can render. Combining marks are handled for the same reason at a lower
    /// stake: «й» written as и + U+0306 must not be cut in two by a diff boundary.
    /// </remarks>
    private static bool IsBoundary(string value, int offset)
    {
        if (offset <= 0 || offset >= value.Length) return true;
        if (char.IsLowSurrogate(value[offset])) return false;
        return CharUnicodeInfo.GetUnicodeCategory(value, offset) is not
            (UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark);
    }
}
