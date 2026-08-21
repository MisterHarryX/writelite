using WriteLite.Models;

namespace LangBench;

/// <summary>
/// Measures the <em>shape</em> of what the pipeline emits, independently of whether it is
/// right.
/// </summary>
/// <remarks>
/// Precision and recall cannot see this. A pipeline that produces the correct final text
/// through a stream of sub-word slices scores identically to one that produces the same text
/// through recognisable word corrections, and the first is unusable: the user is shown
/// <c>'' → 'ы'</c> and asked to decide.
///
/// Phase 4 measured 22 of the local model's 123 false positives (18 %) as that shape,
/// produced by WriteLite's own character diff rather than by the model. These counters exist
/// so a return of it is a number that moves rather than something someone has to notice by
/// reading a report.
///
/// Everything here is derived from the issue span against the source text, not from the
/// diff's own edit type. That is deliberate: it measures what the UI receives, and it
/// measures every layer on the same footing — a spelling suggestion with a half-word span
/// is exactly as unusable as an AI one.
/// </remarks>
internal static class EditQuality
{
    /// <summary>Significant tokens an edit may span before it stops being a correction.</summary>
    /// <remarks>Matches <c>LinguisticDiff.OverWideWordCount</c>; kept local so the harness
    /// scores historical reports and future builds by one fixed rule.</remarks>
    private const int OverWideWordCount = 6;

    internal static EditShape Classify(string source, TextIssue issue)
    {
        var replacement = issue.Replacement ?? string.Empty;
        var start = Math.Clamp(issue.Start, 0, source.Length);
        var end = Math.Clamp(issue.Start + issue.Length, start, source.Length);

        if (issue.Length == 0)
        {
            return ClassifyInsertion(source, start, replacement);
        }

        // A span that begins or ends in the middle of a word is a slice of a word, whatever
        // it was meant to be. "сторонами" cut at "сторон" is the canonical case.
        var splitsAWord = (start > 0 && IsWordChar(source[start - 1]) && IsWordChar(source[start]))
                          || (end < source.Length && IsWordChar(source[end - 1]) && IsWordChar(source[end]));

        var words = Math.Max(CountTokens(source[start..end]), CountTokens(replacement));
        return new EditShape(
            SubWordFragment: splitsAWord,
            ZeroLengthNonPunctuationInsertion: false,
            PhraseLevel: words > 1,
            OverWide: words > OverWideWordCount,
            Meaningful: !splitsAWord && !string.Equals(source[start..end], replacement, StringComparison.Ordinal));
    }

    /// <summary>
    /// An insertion is legitimate when what it inserts is a mark or a spaced-off word, and a
    /// fragment when it is loose letters glued to the word beside it.
    /// </summary>
    private static EditShape ClassifyInsertion(string source, int at, string replacement)
    {
        if (replacement.Length == 0)
        {
            return new EditShape(false, false, false, false, Meaningful: false);
        }

        var carriesLetters = replacement.Any(char.IsLetterOrDigit);
        if (!carriesLetters)
        {
            // Punctuation or whitespace: the missing-comma shape, and a real correction.
            return new EditShape(false, false, false, false, Meaningful: true);
        }

        // Letters being inserted are only a word if they are separated from what surrounds
        // them. Without that they fuse into the neighbouring token, which is the
        // '' → 'ы' shape.
        var spacedLeft = replacement[0] == ' ' || at == 0 || !IsWordChar(source[at - 1]);
        var spacedRight = replacement[^1] == ' ' || at >= source.Length || !IsWordChar(source[at]);
        var fragment = !spacedLeft || !spacedRight;

        var words = CountTokens(replacement);
        return new EditShape(
            SubWordFragment: fragment,
            ZeroLengthNonPunctuationInsertion: fragment,
            PhraseLevel: words > 1,
            OverWide: words > OverWideWordCount,
            Meaningful: !fragment);
    }

    private static int CountTokens(string text)
    {
        var count = 0;
        var inToken = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                inToken = false;
                continue;
            }

            if (!IsWordChar(ch))
            {
                // Punctuation is its own token.
                count++;
                inToken = false;
                continue;
            }

            if (!inToken)
            {
                count++;
                inToken = true;
            }
        }

        return count;
    }

    private static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
}

internal readonly record struct EditShape(
    bool SubWordFragment,
    bool ZeroLengthNonPunctuationInsertion,
    bool PhraseLevel,
    bool OverWide,
    bool Meaningful);
