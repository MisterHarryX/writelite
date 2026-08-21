using System.Globalization;
using System.Text.RegularExpressions;

namespace LangBench;

/// <summary>
/// Detects the class of change that is never an acceptable correction: one that
/// alters what the text asserts rather than how it is spelled.
/// </summary>
/// <remarks>
/// This is the §27 check. It is deliberately blunt — it compares multisets of
/// meaning-bearing tokens between the original and the corrected text and reports
/// any difference. A blunt check is the right shape here because the cost of the
/// two error types is wildly asymmetric: a spurious flag costs a line in a report,
/// while a missed negation flip ships a sentence that says the opposite of what
/// the user wrote.
///
/// Deliberately *not* checked: word order, synonym substitution, and punctuation.
/// Those change the text without changing its claims, and folding them in here
/// would drown the signal this is meant to carry.
/// </remarks>
internal static partial class SemanticGuard
{
    /// <summary>Russian and English negation, plus the modal words that invert obligation.</summary>
    private static readonly HashSet<string> NegationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "не", "ни", "нет", "нельзя", "никогда", "никто", "ничто", "ничего", "никак",
        "без", "кроме", "отнюдь",
        "not", "no", "never", "none", "cannot", "n't",
    };

    /// <summary>
    /// Reasons the produced text changed the meaning of the source in ways the gold
    /// correction does not account for.
    /// </summary>
    /// <remarks>
    /// Comparing produced against source alone is not enough, because some correct
    /// fixes are *supposed* to change meaning-bearing tokens. Repairing a wrong-keyboard
    /// word — "Ghbdtn" → "Привет" — removes a Latin token, and a guard that only looked
    /// at the source would call every correct layout fix a corruption. Measured on the
    /// frozen corpus, that mistake accounted for 60 of 88 reported corruptions.
    ///
    /// So the expected change is subtracted: whatever the gold target does to the source
    /// is licensed, and only what the pipeline does *beyond* that counts.
    /// </remarks>
    public static SemanticVerdict Compare(string source, string produced, string goldTarget)
    {
        var actual = ReasonsFor(source, produced);
        if (actual.Count == 0)
        {
            return SemanticVerdict.Clean;
        }

        var licensed = ReasonsFor(source, goldTarget);
        var unlicensed = actual.Where(r => !licensed.Contains(r)).ToList();

        return unlicensed.Count == 0
            ? SemanticVerdict.Clean
            : new SemanticVerdict(true, unlicensed);
    }

    private static List<string> ReasonsFor(string original, string corrected)
    {
        if (string.Equals(original, corrected, StringComparison.Ordinal))
        {
            return [];
        }

        var reasons = new List<string>();

        if (CountNegations(original) != CountNegations(corrected))
        {
            reasons.Add("negation");
        }

        if (!SameMultiset(Numbers(original), Numbers(corrected)))
        {
            reasons.Add("number");
        }

        // Percentages and money are numbers, and so are already covered above, but they
        // are reported separately because "15 % became 50 %" is a different conversation
        // with a user than "a digit changed somewhere".
        if (!SameMultiset(Quantities(original, PercentToken()), Quantities(corrected, PercentToken())))
        {
            reasons.Add("percentage");
        }

        if (!SameMultiset(Quantities(original, CurrencyToken()), Quantities(corrected, CurrencyToken())))
        {
            reasons.Add("currency");
        }

        if (CountFrom(original, ModalWords) != CountFrom(corrected, ModalWords))
        {
            reasons.Add("modality");
        }

        if (CountFrom(original, ComparativeWords) != CountFrom(corrected, ComparativeWords))
        {
            reasons.Add("comparative");
        }

        if (!SameMultiset(Years(original), Years(corrected)))
        {
            reasons.Add("date");
        }

        if (!SameMultiset(ProperNames(original), ProperNames(corrected)))
        {
            reasons.Add("name");
        }

        if (!SameMultiset(LatinTokens(original), LatinTokens(corrected)))
        {
            reasons.Add("latin_token");
        }

        return reasons;
    }

    /// <summary>Obligation and permission: swapping these rewrites what the text commits to.</summary>
    private static readonly HashSet<string> ModalWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "должен", "должна", "должно", "должны", "обязан", "обязана", "обязаны",
        "может", "могут", "можно", "нужно", "надо", "следует", "необходимо",
        "требуется", "стоит", "придётся", "придется",
        "must", "should", "may", "can", "shall", "need",
    };

    /// <summary>Direction of comparison — "больше" becoming "меньше" inverts the claim.</summary>
    private static readonly HashSet<string> ComparativeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "больше", "меньше", "выше", "ниже", "лучше", "хуже", "быстрее", "медленнее",
        "раньше", "позже", "дороже", "дешевле", "чаще", "реже", "сильнее", "слабее",
        "more", "less", "fewer", "greater", "higher", "lower", "better", "worse",
    };

    private static int CountNegations(string text)
        => CountFrom(text, NegationWords);

    private static int CountFrom(string text, HashSet<string> vocabulary)
        => WordToken().Matches(text).Count(m => vocabulary.Contains(m.Value));

    private static List<string> Quantities(string text, Regex pattern)
    {
        var found = pattern.Matches(text).Select(m => m.Value).ToList();
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static List<string> Numbers(string text)
        => NumberToken().Matches(text).Select(m => m.Value).ToList();

    private static List<string> Years(string text)
        => NumberToken().Matches(text)
            .Select(m => m.Value)
            .Where(v => v.Length == 4
                        && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var y)
                        && y is >= 1000 and <= 2999)
            .ToList();

    /// <summary>
    /// Capitalised words that are not sentence-initial. Sentence-initial words are
    /// excluded because capitalisation there is grammatical rather than onomastic,
    /// and a legitimate sentence-start fix would otherwise read as a name change.
    /// </summary>
    private static List<string> ProperNames(string text)
    {
        var names = new List<string>();
        foreach (Match match in WordToken().Matches(text))
        {
            if (!char.IsUpper(match.Value[0]))
            {
                continue;
            }

            if (IsSentenceInitial(text, match.Index))
            {
                continue;
            }

            names.Add(match.Value);
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static bool IsSentenceInitial(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch) || ch is '"' or '«' or '(' or '\'')
            {
                continue;
            }

            return ch is '.' or '!' or '?' or '…' or ':' or ';';
        }

        return true;
    }

    private static List<string> LatinTokens(string text)
    {
        var tokens = LatinToken().Matches(text).Select(m => m.Value).ToList();
        tokens.Sort(StringComparer.Ordinal);
        return tokens;
    }

    private static bool SameMultiset(List<string> a, List<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        var left = a.OrderBy(x => x, StringComparer.Ordinal);
        var right = b.OrderBy(x => x, StringComparer.Ordinal);
        return left.SequenceEqual(right, StringComparer.Ordinal);
    }

    [GeneratedRegex(@"\p{L}+", RegexOptions.CultureInvariant)]
    private static partial Regex WordToken();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9._\-]*", RegexOptions.CultureInvariant)]
    private static partial Regex LatinToken();

    [GeneratedRegex(@"\d+(?:[.,]\d+)?\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentToken();

    [GeneratedRegex(@"(?:[$€£₽]\s*\d+(?:[.,]\d+)?|\d+(?:[.,]\d+)?\s*(?:руб\w*|долл\w*|евро|USD|EUR|RUB))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyToken();
}

/// <param name="Corrupted">True when the correction changed what the text asserts.</param>
/// <param name="Reasons">Which checks fired: negation, number, date, name, latin_token.</param>
internal sealed record SemanticVerdict(bool Corrupted, IReadOnlyList<string> Reasons)
{
    public static readonly SemanticVerdict Clean = new(false, []);
}
