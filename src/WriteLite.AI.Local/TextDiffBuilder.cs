using WriteLite.AI.Contracts;
using WriteLite.Language.Core;

namespace WriteLite.AI.Local;

/// <summary>
/// Builds an issue list from original vs corrected text by aligning the two on words and
/// punctuation. Indices are UTF-16 code units (same as .NET string indexing).
/// </summary>
/// <remarks>
/// Alignment is <see cref="LinguisticDiff"/>'s, shared with the app-side diff service, so
/// both AI paths cut a rewrite into the same units. This one previously trimmed the common
/// character prefix and suffix and reported the whole remaining middle as a single issue,
/// which turned every multi-word rewrite into one span the user could only take or leave.
/// </remarks>
public static class TextDiffBuilder
{
    public static IReadOnlyList<AiTextIssue> BuildIssues(
        string original,
        string corrected,
        Func<string, string, AiIssueType>? typeInferrer = null,
        Func<string, string, string>? explanationFactory = null)
    {
        if (string.IsNullOrEmpty(original) || corrected is null)
        {
            return [];
        }

        if (string.Equals(original, corrected, StringComparison.Ordinal))
        {
            return [];
        }

        typeInferrer ??= InferType;
        explanationFactory ??= DefaultExplanation;

        var issues = new List<AiTextIssue>();
        foreach (var edit in LinguisticDiff.Compute(original, corrected))
        {
            var type = TypeFor(edit, typeInferrer);
            var confidence = ConfidenceFor(type, edit.Original, edit.Replacement);
            if (edit.IsPhraseLevel)
            {
                // The alignment found no smaller unit. That is the least trustworthy shape
                // this can produce and it should not read as an ordinary word fix.
                confidence *= 0.8;
            }

            issues.Add(new AiTextIssue(
                edit.Start,
                edit.Length,
                edit.Original,
                edit.Replacement,
                type,
                explanationFactory(edit.Original, edit.Replacement),
                confidence,
                SafeToApply: confidence >= 0.8 && !edit.IsPhraseLevel && edit.Original.Length <= 48,
                LowConfidence: confidence < 0.7));
        }

        return issues;
    }

    /// <summary>
    /// The edit type answers most of the classification; the caller's inferrer is consulted
    /// only where the shape genuinely leaves the question open.
    /// </summary>
    private static AiIssueType TypeFor(TextEdit edit, Func<string, string, AiIssueType> inferrer) => edit.EditType switch
    {
        TextEditType.PunctuationInsertion
            or TextEditType.PunctuationDeletion
            or TextEditType.PunctuationReplacement => AiIssueType.Punctuation,
        TextEditType.WhitespaceCorrection => AiIssueType.Spacing,
        TextEditType.WordInsertion => AiIssueType.MissingWord,
        TextEditType.WordDeletion => AiIssueType.ExtraWord,
        _ => inferrer(edit.Original, edit.Replacement),
    };

    public static string ApplyIssuesRightToLeft(string original, IReadOnlyList<AiTextIssue> issues)
    {
        var ordered = issues
            .Where(i => i.Start >= 0 && i.Length >= 0 && i.Start + i.Length <= original.Length)
            .Where(i => string.Equals(original.Substring(i.Start, i.Length), i.Original, StringComparison.Ordinal))
            .OrderByDescending(i => i.Start)
            .ThenByDescending(i => i.Length)
            .ToList();

        var text = original;
        foreach (var issue in ordered)
        {
            text = text.Substring(0, issue.Start) + issue.Replacement + text.Substring(issue.Start + issue.Length);
        }

        return text;
    }

    public static AiIssueType InferType(string original, string replacement)
    {
        var oLetters = new string(original.Where(char.IsLetter).ToArray());
        var rLetters = new string(replacement.Where(char.IsLetter).ToArray());

        if (string.IsNullOrEmpty(original) && replacement.All(ch => char.IsPunctuation(ch) || char.IsWhiteSpace(ch)))
        {
            return AiIssueType.Punctuation;
        }

        if (original.All(ch => char.IsWhiteSpace(ch) || char.IsPunctuation(ch))
            && replacement.All(ch => char.IsWhiteSpace(ch) || char.IsPunctuation(ch)))
        {
            return AiIssueType.Punctuation;
        }

        if (string.Equals(oLetters, rLetters, StringComparison.Ordinal)
            && !string.Equals(original, replacement, StringComparison.Ordinal))
        {
            if (original.Any(char.IsPunctuation) || replacement.Any(char.IsPunctuation)
                || original.Contains(' ') != replacement.Contains(' ')
                || Count(original, ',') != Count(replacement, ',')
                || Count(original, '.') != Count(replacement, '.')
                || Count(original, '?') != Count(replacement, '?')
                || Count(original, '!') != Count(replacement, '!'))
            {
                return AiIssueType.Punctuation;
            }

            return AiIssueType.Spacing;
        }

        if (string.Equals(oLetters, rLetters, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(oLetters, rLetters, StringComparison.Ordinal))
        {
            return AiIssueType.Capitalization;
        }

        if (string.Equals(original.Replace(" ", "", StringComparison.Ordinal),
                replacement.Replace(" ", "", StringComparison.Ordinal),
                StringComparison.OrdinalIgnoreCase)
            && original.Contains(' ') != replacement.Contains(' '))
        {
            return AiIssueType.Spelling;
        }

        if (oLetters.Length > 0 && rLetters.Length > 0
            && Levenshtein(oLetters.ToLowerInvariant(), rLetters.ToLowerInvariant()) <= 2)
        {
            return AiIssueType.Typo;
        }

        if (string.Equals(original, replacement + original, StringComparison.OrdinalIgnoreCase)
            || string.Equals(replacement, original + original, StringComparison.OrdinalIgnoreCase))
        {
            return AiIssueType.Repetition;
        }

        return AiIssueType.Grammar;
    }

    private static string DefaultExplanation(string original, string replacement)
    {
        var type = InferType(original, replacement);
        return type switch
        {
            AiIssueType.Punctuation => "Исправлена пунктуация.",
            AiIssueType.Capitalization => "Исправлен регистр букв.",
            AiIssueType.Spelling => "Исправлена орфография.",
            AiIssueType.Typo => "Исправлена опечатка.",
            AiIssueType.Spacing => "Исправлены пробелы.",
            AiIssueType.Repetition => "Удалён повтор слова.",
            AiIssueType.Agreement => "Исправлено согласование.",
            _ => "Предложено грамматическое исправление."
        };
    }

    private static double ConfidenceFor(AiIssueType type, string original, string replacement)
    {
        return type switch
        {
            AiIssueType.Punctuation => 0.88,
            AiIssueType.Capitalization => 0.95,
            AiIssueType.Spelling => 0.92,
            AiIssueType.Typo => 0.85,
            AiIssueType.Spacing => 0.9,
            AiIssueType.Repetition => 0.93,
            AiIssueType.Agreement => 0.82,
            AiIssueType.Grammar => original.Length <= 24 ? 0.78 : 0.7,
            _ => 0.65
        };
    }

    private static int Count(string s, char c)
    {
        var n = 0;
        foreach (var ch in s)
        {
            if (ch == c) n++;
        }

        return n;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }
}
