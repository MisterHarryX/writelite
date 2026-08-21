using System.Text.RegularExpressions;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// The sentence a correction is about, plus the one before and the one after.
/// </summary>
/// <param name="Start">Offset of the current sentence in the document.</param>
/// <param name="Length">Length of the current sentence.</param>
/// <param name="Previous">Preceding sentence, or empty at the start of the document.</param>
/// <param name="Current">The sentence containing the offset that was asked about.</param>
/// <param name="Next">Following sentence, or empty at the end of the document.</param>
public readonly record struct SentenceWindow(
    int Start,
    int Length,
    string Previous,
    string Current,
    string Next)
{
    public bool IsEmpty => string.IsNullOrEmpty(Current);

    /// <summary>The three sentences joined, for a model prompt or an acceptability score.</summary>
    public string Joined => string.Join(
        ' ',
        new[] { Previous, Current, Next }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>Characters of context either side, for budgeting.</summary>
    public int ContextLength => Previous.Length + Next.Length;
}

/// <summary>
/// Splits text into sentences and hands back a bounded window around any offset.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately the smallest useful document-context layer, not a document AI.
/// Everything WriteLite currently needs context for — whether a pronoun agrees, whether a
/// word is repeated across a boundary, whether a real-word substitution reads correctly,
/// whether a sentence actually ended — is answerable from the immediate neighbours.
/// </para>
/// <para>
/// The window is bounded twice: three sentences, and a character budget. A 100-page
/// document therefore costs the same per correction as a one-line note, which is the
/// property that keeps large documents responsive and keeps whole documents out of the
/// model's 768-token context.
/// </para>
/// <para>
/// Abbreviations are the reason this is not a one-line regex: splitting on every period
/// would cut "т. е." and "и т. д." in half and produce windows that are worse than no
/// context at all.
/// </para>
/// </remarks>
public static partial class SentenceWindowBuilder
{
    /// <summary>Characters of neighbouring context kept on each side.</summary>
    public const int DefaultContextBudget = 400;

    /// <summary>
    /// Abbreviations that introduce a following word, so their period never ends a
    /// sentence even when a capital letter follows.
    /// </summary>
    /// <remarks>
    /// The distinction matters and is not cosmetic. "проф. Иванов" and "г. Москва" are one
    /// sentence — the capital belongs to the name the abbreviation introduces. But
    /// "…и т. д. Завтра пойдём" is two, because "и т. д." *ends* a list rather than
    /// introducing anything. Treating every abbreviation as non-terminal merges those two
    /// sentences and hands the model a window twice the size it asked for.
    ///
    /// Only this list overrides the capital-letter rule. Everything else — "т. е.",
    /// "и др.", "etc." — is left to the general heuristic, which correctly keeps the
    /// sentence open when a lowercase word follows and closes it when a capital does.
    /// </remarks>
    private static readonly string[] IntroducingAbbreviations =
    [
        "проф", "акад", "тов", "им", "ул", "пр", "пер", "обл", "респ",
        "г", "гг", "стр", "рис", "табл", "гл",
        "Mr", "Mrs", "Ms", "Dr", "Prof", "St",
    ];

    /// <summary>Sentence spans over the whole text, in order, without gaps.</summary>
    public static IReadOnlyList<(int Start, int Length)> Split(string text)
    {
        var spans = new List<(int Start, int Length)>();
        if (string.IsNullOrEmpty(text))
        {
            return spans;
        }

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?' or '…' and var _))
            {
                // Paragraph breaks end a sentence even without punctuation.
                if (text[i] == '\n' && i + 1 < text.Length)
                {
                    if (i + 1 > start)
                    {
                        spans.Add((start, i + 1 - start));
                        start = i + 1;
                    }
                }

                continue;
            }

            if (!IsSentenceEnd(text, i))
            {
                continue;
            }

            // Absorb closing quotes/brackets and the run of terminators.
            var end = i + 1;
            while (end < text.Length && (text[end] is '.' or '!' or '?' or '…' or '"' or '»' or ')' or '\''))
            {
                end++;
            }

            spans.Add((start, end - start));
            start = end;
            i = end - 1;
        }

        if (start < text.Length)
        {
            spans.Add((start, text.Length - start));
        }

        return spans;
    }

    /// <summary>
    /// The sentence containing <paramref name="offset"/>, with its neighbours, trimmed to
    /// <paramref name="contextBudget"/> characters per side.
    /// </summary>
    public static SentenceWindow Around(string text, int offset, int contextBudget = DefaultContextBudget)
    {
        if (string.IsNullOrEmpty(text))
        {
            return default;
        }

        var spans = Split(text);
        if (spans.Count == 0)
        {
            return default;
        }

        var clamped = Math.Clamp(offset, 0, text.Length - 1);
        var index = 0;
        for (var i = 0; i < spans.Count; i++)
        {
            var (spanStart, spanLength) = spans[i];
            if (clamped >= spanStart && clamped < spanStart + spanLength)
            {
                index = i;
                break;
            }

            if (clamped >= spanStart)
            {
                index = i;
            }
        }

        var (start, length) = spans[index];
        return new SentenceWindow(
            start,
            length,
            index > 0 ? Tail(Slice(text, spans[index - 1]), contextBudget) : string.Empty,
            Slice(text, spans[index]).Trim(),
            index + 1 < spans.Count ? Head(Slice(text, spans[index + 1]), contextBudget) : string.Empty);
    }

    private static string Slice(string text, (int Start, int Length) span)
        => text.Substring(span.Start, Math.Min(span.Length, text.Length - span.Start));

    /// <summary>Keeps the end of the previous sentence — the part nearest the current one.</summary>
    private static string Tail(string value, int budget)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= budget ? trimmed : trimmed[^budget..];
    }

    /// <summary>Keeps the start of the next sentence, for the same reason.</summary>
    private static string Head(string value, int budget)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= budget ? trimmed : trimmed[..budget];
    }

    /// <summary>
    /// True when the terminator at <paramref name="index"/> really ends a sentence.
    /// </summary>
    private static bool IsSentenceEnd(string text, int index)
    {
        // A decimal point, a version number, an ellipsis mid-clause: not an ending.
        if (text[index] == '.'
            && index > 0 && index + 1 < text.Length
            && char.IsDigit(text[index - 1]) && char.IsDigit(text[index + 1]))
        {
            return false;
        }

        // An abbreviation that introduces a following word never ends a sentence.
        if (EndsWithIntroducingAbbreviation(text, index))
        {
            return false;
        }

        // Must be followed by whitespace then something that can start a sentence.
        var next = index + 1;
        while (next < text.Length && (text[next] is '.' or '!' or '?' or '…' or '"' or '»' or ')' or '\''))
        {
            next++;
        }

        if (next >= text.Length)
        {
            return true;
        }

        if (!char.IsWhiteSpace(text[next]))
        {
            return false;
        }

        while (next < text.Length && char.IsWhiteSpace(text[next]))
        {
            next++;
        }

        return next >= text.Length || !char.IsLower(text[next]);
    }

    private static bool EndsWithIntroducingAbbreviation(string text, int index)
    {
        foreach (var abbreviation in IntroducingAbbreviations)
        {
            var startOfWord = index - abbreviation.Length;
            if (startOfWord < 0) continue;

            if (string.Compare(text, startOfWord, abbreviation, 0, abbreviation.Length,
                    StringComparison.OrdinalIgnoreCase) != 0)
            {
                continue;
            }

            // Must be a whole token, not a suffix of a longer word.
            if (startOfWord == 0 || !char.IsLetter(text[startOfWord - 1]))
            {
                return true;
            }
        }

        return false;
    }
}
