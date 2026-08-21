using System.Text.RegularExpressions;

namespace WriteLite.Services.Ai;

/// <summary>
/// Rejects edits that change what a span asserts rather than how it is written.
/// </summary>
/// <remarks>
/// <para>
/// This is the product-side counterpart to the benchmark's whole-sentence semantic check,
/// and it exists because the acceptance policy needed it: adversarial tests showed that an
/// unsupported model edit claiming confidence 0.99 was allowed to turn <c>15%</c> into
/// <c>50%</c> and <c>WriteLite</c> into <c>Райтлайт</c>, because a confidence floor alone
/// cannot tell a spelling fix from a fact change.
/// </para>
/// <para>
/// It compares the two sides of one edit, not two whole documents, so it is cheap enough to
/// run on every candidate. The checks are deliberately blunt: any change to digits,
/// negation, Latin identifiers, percentages or currency is refused. A blunt check is right
/// here because the costs are asymmetric — a refused good suggestion costs the user one
/// missed comma, while an accepted bad one silently rewrites what they said.
/// </para>
/// <para>
/// Not checked: word order, synonyms, and punctuation, which change text without changing
/// claims. Folding those in would reject most legitimate corrections.
/// </para>
/// </remarks>
public static partial class SemanticEditGuard
{
    private static readonly HashSet<string> NegationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "не", "ни", "нет", "нельзя", "никогда", "никто", "ничто", "ничего", "никак", "без",
        "not", "no", "never", "none", "cannot",
    };

    /// <param name="original">The text currently in the document.</param>
    /// <param name="replacement">What the model proposes to put there.</param>
    /// <returns>Null when the edit is safe; otherwise the reason it is not.</returns>
    public static string? Reject(string? original, string? replacement)
    {
        var from = original ?? string.Empty;
        var to = replacement ?? string.Empty;

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return null;
        }

        if (!SameMultiset(Digits(from), Digits(to)))
        {
            return "changes-numbers";
        }

        if (CountNegations(from) != CountNegations(to))
        {
            return "changes-negation";
        }

        // Latin tokens inside Russian prose are identifiers, product names, technical terms
        // and URLs. Rewriting one is never a Russian-language correction.
        if (!SameMultiset(LatinTokens(from), LatinTokens(to)))
        {
            return "changes-latin-identifier";
        }

        if (!SameMultiset(Percentages(from), Percentages(to)))
        {
            return "changes-percentage";
        }

        if (!SameMultiset(Currency(from), Currency(to)))
        {
            return "changes-currency";
        }

        return null;
    }

    /// <summary>
    /// Counts negation, treating a word-initial «не»/«ни» the same whether it is written
    /// joined or separated.
    /// </summary>
    /// <remarks>
    /// Counting whole word tokens was wrong, and a test caught it: «небыл» → «не был» is
    /// the single most common Russian correction in the benchmark, and it scored 0 → 1
    /// because the joined form has no standalone «не». The guard rejected the correction it
    /// most needed to allow.
    ///
    /// Matching word-initial position instead makes joining and splitting equivalent. It
    /// over-counts on unrelated words that merely start with those letters — «небо»,
    /// «никель» — but the comparison is between the two sides of one edit, so a consistent
    /// over-count on both sides cancels and only a genuine appearance or disappearance of
    /// negation registers.
    /// </remarks>
    private static int CountNegations(string text)
    {
        var count = NegationPrefix().Matches(text).Count;

        // Standalone negations that are not «не»/«ни» prefixes still count as words.
        count += WordToken().Matches(text)
            .Count(m => NegationWords.Contains(m.Value)
                        && !m.Value.StartsWith("не", StringComparison.OrdinalIgnoreCase)
                        && !m.Value.StartsWith("ни", StringComparison.OrdinalIgnoreCase));

        return count;
    }

    private static List<string> Digits(string text)
        => Sorted(NumberToken().Matches(text).Select(m => m.Value));

    private static List<string> LatinTokens(string text)
        => Sorted(LatinToken().Matches(text).Select(m => m.Value));

    private static List<string> Percentages(string text)
        => Sorted(PercentToken().Matches(text).Select(m => m.Value));

    private static List<string> Currency(string text)
        => Sorted(CurrencyToken().Matches(text).Select(m => m.Value));

    private static List<string> Sorted(IEnumerable<string> values)
    {
        var list = values.ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static bool SameMultiset(List<string> a, List<string> b)
        => a.Count == b.Count && a.SequenceEqual(b, StringComparer.Ordinal);

    [GeneratedRegex(@"\p{L}+", RegexOptions.CultureInvariant)]
    private static partial Regex WordToken();

    /// <summary>Word-initial «не»/«ни», joined or separated alike.</summary>
    [GeneratedRegex(@"(?<!\p{L})(не|ни)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NegationPrefix();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9._\-]*", RegexOptions.CultureInvariant)]
    private static partial Regex LatinToken();

    [GeneratedRegex(@"\d+(?:[.,]\d+)?\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentToken();

    [GeneratedRegex(
        @"(?:[$€£₽]\s*\d+(?:[.,]\d+)?|\d+(?:[.,]\d+)?\s*(?:руб\w*|долл\w*|евро|USD|EUR|RUB))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyToken();
}
