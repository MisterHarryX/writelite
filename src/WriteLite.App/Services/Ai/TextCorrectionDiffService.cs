using WriteLite.Models;

namespace WriteLite.Services.Ai;

/// <summary>
/// Character-level Myers-like diff simplified to contiguous replace/insert/delete blocks.
/// Produces apply-safe TextIssue spans for AI full-text corrections.
/// </summary>
public sealed class TextCorrectionDiffService : ITextCorrectionDiffService
{
    private readonly AnalysisResultValidator _validator;

    public TextCorrectionDiffService(AnalysisResultValidator? validator = null)
    {
        _validator = validator ?? new AnalysisResultValidator();
    }

    public IReadOnlyList<TextIssue> BuildIssues(string original, string corrected)
    {
        if (string.IsNullOrEmpty(original) || corrected is null)
        {
            return [];
        }

        if (string.Equals(original, corrected, StringComparison.Ordinal))
        {
            return [];
        }

        if (!_validator.IsCorrectedTextSane(original, corrected))
        {
            return [];
        }

        // Longest common prefix / suffix first — cheap path for pure punctuation/edge edits.
        var prefix = CommonPrefixLength(original, corrected);
        var a = original.AsSpan(prefix);
        var b = corrected.AsSpan(prefix);
        var suffix = CommonSuffixLength(a, b);
        if (suffix > 0)
        {
            a = a[..^suffix];
            b = b[..^suffix];
        }

        var start = prefix;
        var origMid = a.ToString();
        var corrMid = b.ToString();

        // Single contiguous change (most punctuation restorations).
        if (origMid.Length == 0 && corrMid.Length == 0)
        {
            return [];
        }

        // Split mid into word-ish hunks when both sides are long and multi-change.
        var hunks = SplitIntoHunks(origMid, corrMid);
        if (hunks.Count == 0)
        {
            hunks.Add((0, origMid.Length, 0, corrMid.Length));
        }

        var issues = new List<TextIssue>();
        foreach (var (oStart, oLen, cStart, cLen) in hunks)
        {
            var oFrag = origMid.Substring(oStart, oLen);
            var cFrag = corrMid.Substring(cStart, cLen);
            if (string.Equals(oFrag, cFrag, StringComparison.Ordinal))
            {
                continue;
            }

            if (!_validator.IsReplacementSane(oFrag, cFrag))
            {
                continue;
            }

            var absStart = start + oStart;
            var category = InferCategory(oFrag, cFrag);
            var (cat, ling) = category;
            issues.Add(new TextIssue(
                absStart,
                oLen,
                oFrag,
                cFrag,
                TitleFor(cat),
                "Исправление по результатам расширенного анализа.",
                cat,
                IssueSeverity.Error,
                CanApplyAutomatically: IsSafeAuto(oFrag, cFrag),
                RuleId: "WL-AI-DIFF-" + cat.ToString().ToUpperInvariant(),
                LinguisticCategory: ling));
        }

        // If we couldn't produce sane hunks, fall back to one whole-mid issue when short enough.
        if (issues.Count == 0 && origMid.Length > 0 && origMid.Length <= 120
            && _validator.IsReplacementSane(origMid, corrMid))
        {
            var (cat, ling) = InferCategory(origMid, corrMid);
            issues.Add(new TextIssue(
                start,
                origMid.Length,
                origMid,
                corrMid,
                TitleFor(cat),
                "Исправление по результатам расширенного анализа.",
                cat,
                IssueSeverity.Error,
                CanApplyAutomatically: IsSafeAuto(origMid, corrMid),
                RuleId: "WL-AI-DIFF-BLOCK",
                LinguisticCategory: ling));
        }

        return issues;
    }

    private static List<(int OStart, int OLen, int CStart, int CLen)> SplitIntoHunks(string a, string b)
    {
        // Simple word-boundary alignment using LCS of tokens when both sides aren't huge.
        if (a.Length > 800 || b.Length > 800)
        {
            return [(0, a.Length, 0, b.Length)];
        }

        var aTokens = Tokenize(a);
        var bTokens = Tokenize(b);
        if (aTokens.Count == 0 || bTokens.Count == 0)
        {
            return [(0, a.Length, 0, b.Length)];
        }

        var lcs = BuildLcsMap(aTokens.Select(t => t.Value).ToList(), bTokens.Select(t => t.Value).ToList());
        var hunks = new List<(int, int, int, int)>();
        var ai = 0;
        var bi = 0;
        var pair = 0;
        while (ai < aTokens.Count || bi < bTokens.Count)
        {
            if (pair < lcs.Count && ai == lcs[pair].Ai && bi == lcs[pair].Bi)
            {
                ai++;
                bi++;
                pair++;
                continue;
            }

            var aHunkStart = ai < aTokens.Count ? aTokens[ai].Start : a.Length;
            var bHunkStart = bi < bTokens.Count ? bTokens[bi].Start : b.Length;
            var aEnd = aHunkStart;
            var bEnd = bHunkStart;

            while (ai < aTokens.Count && (pair >= lcs.Count || ai < lcs[pair].Ai))
            {
                aEnd = aTokens[ai].Start + aTokens[ai].Value.Length;
                ai++;
            }

            while (bi < bTokens.Count && (pair >= lcs.Count || bi < lcs[pair].Bi))
            {
                bEnd = bTokens[bi].Start + bTokens[bi].Value.Length;
                bi++;
            }

            // Include intervening separators between tokens in the hunk.
            if (ai > 0 && ai <= aTokens.Count)
            {
                // expand aEnd to next token start - 0 or end
                var nextA = ai < aTokens.Count ? aTokens[ai].Start : a.Length;
                // keep only up to aEnd already
                _ = nextA;
            }

            var oStart = aHunkStart;
            var oLen = Math.Max(0, aEnd - aHunkStart);
            var cStart = bHunkStart;
            var cLen = Math.Max(0, bEnd - bHunkStart);
            if (oLen > 0 || cLen > 0)
            {
                hunks.Add((oStart, oLen, cStart, cLen));
            }
        }

        return hunks.Count > 0 ? hunks : [(0, a.Length, 0, b.Length)];
    }

    private static List<(int Ai, int Bi)> BuildLcsMap(List<string> a, List<string> b)
    {
        var n = a.Count;
        var m = b.Count;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                if (string.Equals(a[i], b[j], StringComparison.Ordinal))
                {
                    dp[i, j] = dp[i + 1, j + 1] + 1;
                }
                else
                {
                    dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
                }
            }
        }

        var path = new List<(int, int)>();
        var x = 0;
        var y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(a[x], b[y], StringComparison.Ordinal))
            {
                path.Add((x, y));
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return path;
    }

    private static List<(int Start, string Value)> Tokenize(string text)
    {
        var list = new List<(int, string)>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                var s = i;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                list.Add((s, text[s..i]));
                continue;
            }

            if (char.IsLetterOrDigit(text[i]) || text[i] is 'ё' or 'Ё')
            {
                var s = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is 'ё' or 'Ё' || text[i] == '-'))
                {
                    i++;
                }

                list.Add((s, text[s..i]));
                continue;
            }

            list.Add((i, text[i].ToString()));
            i++;
        }

        return list;
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    private static int CommonSuffixLength(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[a.Length - 1 - i] == b[b.Length - 1 - i]) i++;
        // Avoid eating entire string into suffix when strings equal mid.
        if (i == a.Length && i == b.Length) return 0;
        return i;
    }

    private static (IssueCategory, LinguisticIssueCategory) InferCategory(string original, string replacement)
    {
        var oLetters = original.Where(char.IsLetter).ToArray();
        var rLetters = replacement.Where(char.IsLetter).ToArray();
        var oLetterStr = new string(oLetters);
        var rLetterStr = new string(rLetters);

        if (string.Equals(oLetterStr, rLetterStr, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(original, replacement, StringComparison.Ordinal))
        {
            // Same letters (approx) → punctuation / typography.
            if (original.Any(char.IsPunctuation) || replacement.Any(char.IsPunctuation)
                || original.Contains(' ') || replacement.Contains(','))
            {
                return (IssueCategory.Punctuation, LinguisticIssueCategory.PunctuationRecommendation);
            }

            return (IssueCategory.Readability, LinguisticIssueCategory.ExtraSpace);
        }

        if (string.Equals(oLetterStr, rLetterStr, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(oLetterStr, rLetterStr, StringComparison.Ordinal))
        {
            return (IssueCategory.Orthography, LinguisticIssueCategory.EndingError); // capitalization-ish
        }

        if (Math.Abs(original.Length - replacement.Length) <= 2
            && oLetterStr.Length > 0
            && Levenshtein(oLetterStr.ToLowerInvariant(), rLetterStr.ToLowerInvariant()) <= 2)
        {
            return (IssueCategory.Orthography, LinguisticIssueCategory.Typo);
        }

        return (IssueCategory.Grammar, LinguisticIssueCategory.AgreementError);
    }

    private static string TitleFor(IssueCategory cat) => cat switch
    {
        IssueCategory.Punctuation => "Пунктуация",
        IssueCategory.Orthography => "Орфография",
        IssueCategory.Grammar => "Грамматика",
        IssueCategory.Style => "Стиль",
        IssueCategory.Readability => "Оформление",
        _ => "Замечание"
    };

    private static bool IsSafeAuto(string original, string replacement)
    {
        // Prefer auto-apply for small punctuation / single-word spelling fixes.
        if (original.Length <= 40 && replacement.Length <= 48)
        {
            var letterDelta = Math.Abs(
                original.Count(char.IsLetter) - replacement.Count(char.IsLetter));
            return letterDelta <= 3;
        }

        return false;
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
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }
}
