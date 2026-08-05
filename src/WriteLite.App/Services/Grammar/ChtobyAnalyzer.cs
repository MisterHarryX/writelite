using System.Text.RegularExpressions;
using WriteLite.Models;

namespace WriteLite.Services.Grammar;

/// <summary>
/// Reliable rules for «чтобы» / «что бы» and mandatory comma before purpose clause.
/// </summary>
public static partial class ChtobyAnalyzer
{
    public static void Collect(string text, IReadOnlyList<(int Start, int End)> protectedSpans, ICollection<TextIssue> issues)
    {
        // Comma first so ranges stay stable; merge is orthography on the conjunction span.
        CollectCommaBeforePurposeConjunction(text, protectedSpans, issues);
        CollectChtoByMerge(text, protectedSpans, issues);
    }

    /// <summary>
    /// «меры, что бы минимизировать» → «чтобы» (слитно) in purpose clauses only.
    /// </summary>
    private static void CollectChtoByMerge(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        foreach (Match m in ChtoByRegex().Matches(text))
        {
            if (ProtectedTextSpans.Overlaps(m.Index, m.Length, protectedSpans))
            {
                continue;
            }

            // Do not merge interrogative / free-choice «что бы».
            if (IsInterrogativeOrFreeChoiceChtoBy(text, m.Index))
            {
                continue;
            }

            if (!LooksLikePurposeClause(text, m.Index + m.Length))
            {
                continue;
            }

            // Pure orthography fix: «что бы» → «чтобы». Comma is a separate rule when missing.
            issues.Add(new TextIssue(
                m.Index,
                m.Length,
                m.Value,
                "чтобы",
                "«чтобы» слитно",
                "В придаточной части цели союз «чтобы» пишется слитно.",
                IssueCategory.Grammar,
                IssueSeverity.Error,
                CanApplyAutomatically: true,
                RuleId: "ru.grammar.chtoby-solid",
                LinguisticCategory: LinguisticIssueCategory.JoinedOrSeparateSpelling));
        }
    }

    /// <summary>
    /// «меры чтобы …» / «меры что бы …» → insert comma before the purpose conjunction.
    /// Form: Start before conjunction, Length=0, Original="", Replacement=",".
    /// </summary>
    private static void CollectCommaBeforePurposeConjunction(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        // Match solid «чтобы» and split purpose «что бы» (not free-choice).
        foreach (Match m in PurposeConjunctionRegex().Matches(text))
        {
            if (ProtectedTextSpans.Overlaps(m.Index, m.Length, protectedSpans))
            {
                continue;
            }

            var isSplit = m.Value.Contains(' ', StringComparison.Ordinal)
                          || m.Value.Contains('\u00A0', StringComparison.Ordinal);

            if (isSplit && IsInterrogativeOrFreeChoiceChtoBy(text, m.Index))
            {
                continue;
            }

            if (HasCommaImmediatelyBefore(text, m.Index))
            {
                continue;
            }

            if (IsClauseStart(text, m.Index))
            {
                continue;
            }

            if (!LooksLikePurposeClause(text, m.Index + m.Length))
            {
                continue;
            }

            var prev = PreviousNonWhitespace(text, m.Index - 1);
            if (!char.IsLetter(prev) && prev is not '»' and not '"' and not '”' and not ')')
            {
                continue;
            }

            // Pure insert immediately before the conjunction (after any whitespace run).
            // "меры[ ]чтобы" + insert "," at index of space → "меры, чтобы"
            // "мерычтобы" + insert ", " at word index → "меры, чтобы"
            int start;
            string replacement;

            if (m.Index > 0 && char.IsWhiteSpace(text[m.Index - 1]))
            {
                start = m.Index - 1;
                while (start > 0 && char.IsWhiteSpace(text[start - 1]))
                {
                    start--;
                }

                replacement = ",";
            }
            else
            {
                start = m.Index;
                replacement = ", ";
            }

            if (ProtectedTextSpans.Overlaps(start, 0, protectedSpans))
            {
                continue;
            }

            issues.Add(new TextIssue(
                start,
                0,
                "",
                replacement,
                "Запятая перед «чтобы»",
                "Перед придаточной частью цели с союзом «чтобы» нужна запятая.",
                IssueCategory.Punctuation,
                IssueSeverity.Error,
                CanApplyAutomatically: true,
                RuleId: "ru.punctuation.comma-before-chtoby",
                LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation));
        }
    }

    private static bool IsInterrogativeOrFreeChoiceChtoBy(string text, int index)
    {
        // Start of sentence / after ?!. / newline → typically «Что бы ты…»
        var i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
        {
            if (text[i] is '\n' or '\r')
            {
                return true;
            }

            i--;
        }

        if (i < 0)
        {
            return true;
        }

        if (text[i] is '?' or '!' or '.' or '…')
        {
            return true;
        }

        // «что бы ни …»
        var window = SafeSlice(text, index, Math.Min(text.Length - index, 32));
        if (ChtoByNiRegex().IsMatch(window))
        {
            return true;
        }

        // Free-choice / doubt contexts — keep separate spelling even with infinitive ahead.
        var left = SafeSlice(text, Math.Max(0, index - 48), Math.Min(48, index));
        if (ChoiceContextRegex().IsMatch(left))
        {
            return true;
        }

        // «что бы» + personal pronoun (ты/вы/он…) often free choice, not purpose.
        var after = SafeSlice(text, index + EstimateChtoByLength(text, index), Math.Min(16, text.Length - index));
        if (PronounAfterChtoByRegex().IsMatch(after) && !StrongPurposeLeft(left))
        {
            return true;
        }

        return false;
    }

    private static int EstimateChtoByLength(string text, int index)
    {
        var m = ChtoByRegex().Match(text, index);
        return m.Success && m.Index == index ? m.Length : 6;
    }

    private static bool StrongPurposeLeft(string left)
        => PurposeLeftRegex().IsMatch(left);

    private static bool LooksLikePurposeClause(string text, int afterConjunction)
    {
        var ahead = SafeSlice(text, afterConjunction, Math.Min(56, text.Length - afterConjunction));
        // Purpose: infinitive soon (минимизировать, поговорить, избежать…).
        if (PurposeInfinitiveRegex().IsMatch(ahead))
        {
            return true;
        }

        // Personal forms less reliable — only if clearly verbal purpose without pronoun free-choice.
        return false;
    }

    private static bool HasCommaImmediatelyBefore(string text, int index)
    {
        var i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
        {
            i--;
        }

        return i >= 0 && text[i] == ',';
    }

    private static bool IsClauseStart(string text, int index)
    {
        if (index <= 0)
        {
            return true;
        }

        var i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
        {
            if (text[i] is '\n' or '\r')
            {
                return true;
            }

            i--;
        }

        if (i < 0)
        {
            return true;
        }

        return text[i] is '.' or '!' or '?' or '…' or ':' or ';' or '—' or '(' or '«' or '"';
    }

    private static char PreviousNonWhitespace(string text, int index)
    {
        while (index >= 0)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                return text[index];
            }

            index--;
        }

        return '\0';
    }

    private static string SafeSlice(string text, int start, int length)
    {
        if (start < 0 || start >= text.Length || length <= 0)
        {
            return string.Empty;
        }

        length = Math.Min(length, text.Length - start);
        return text.Substring(start, length);
    }

    [GeneratedRegex(@"\bчто\s+бы\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChtoByRegex();

    /// <summary>Solid «чтобы» or split purpose candidate «что бы».</summary>
    [GeneratedRegex(@"\b(?:чтобы|что\s+бы)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PurposeConjunctionRegex();

    [GeneratedRegex(@"\bчто\s+бы\s+ни\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChtoByNiRegex();

    // Free-choice / doubt left contexts — always keep «что бы» separate.
    [GeneratedRegex(@"(?i)(не\s+знаю|не\s+понимаю|не\s+понял|не\s+реш|выбра|спрашив|думаю|интересно|хотел\s+бы\s+знать)", RegexOptions.CultureInvariant)]
    private static partial Regex ChoiceContextRegex();

    [GeneratedRegex(@"^\s+(ты|вы|он|она|они|мы|я|кто|кого|кому)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PronounAfterChtoByRegex();

    [GeneratedRegex(@"(?i)(мер[аыуе]|предпринять|принят|сделать|сделал|нужно|надо|чтобы|цель|для\s+того)", RegexOptions.CultureInvariant)]
    private static partial Regex PurposeLeftRegex();

    [GeneratedRegex(@"^\s*[\p{L}-]*?(ть|ти|чь)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PurposeInfinitiveRegex();
}
