using WriteLite.Models;

namespace WriteLite.Services;

/// <summary>User-facing labels for correction cards (popup + panel).</summary>
public static class CorrectionPresentation
{
    public static string FormatChange(TextIssue issue)
    {
        if (IsTerminalPunctuationInsert(issue) || (issue.Length == 0 && !string.IsNullOrEmpty(issue.Replacement)))
        {
            var mark = issue.Replacement ?? string.Empty;
            return mark switch
            {
                "." => "Добавить точку",
                "," => "Добавить запятую",
                "!" => "Добавить восклицательный знак",
                "?" => "Добавить вопросительный знак",
                "…" => "Добавить многоточие",
                "—" => "Добавить тире",
                _ => $"Вставить «{mark}»"
            };
        }

        // Punctuation that currently stores "а" → "а." (legacy): show as insert.
        if (issue.Category == IssueCategory.Punctuation
            && issue.Length == 1
            && !string.IsNullOrEmpty(issue.Replacement)
            && issue.Replacement.Length == 2
            && issue.Replacement.StartsWith(issue.Original, StringComparison.Ordinal)
            && IsPunctuationChar(issue.Replacement[^1]))
        {
            return FormatChange(issue with
            {
                Start = issue.Start + 1,
                Length = 0,
                Original = string.Empty,
                Replacement = issue.Replacement[^1].ToString()
            });
        }

        if (string.IsNullOrWhiteSpace(issue.Replacement))
        {
            return string.IsNullOrEmpty(issue.Original) ? "Рекомендация" : issue.Original;
        }

        return $"{issue.Original} → {issue.Replacement}";
    }

    public static string FormatChipLabel(TextIssue issue)
    {
        if (IsTerminalPunctuationInsert(issue) || issue.Length == 0)
        {
            return FormatChange(issue);
        }

        if (issue.Category == IssueCategory.Punctuation
            && issue.Length == 1
            && issue.Replacement is { Length: 2 } rep
            && rep.StartsWith(issue.Original, StringComparison.Ordinal)
            && IsPunctuationChar(rep[^1]))
        {
            return FormatChange(issue with
            {
                Length = 0,
                Original = string.Empty,
                Replacement = rep[^1].ToString()
            });
        }

        return issue.Replacement ?? "Исправить";
    }

    /// <summary>
    /// Normalizes punctuation issues that incorrectly replace the last letter with "letter+mark"
    /// into a pure zero-length insertion after the letter.
    /// </summary>
    public static TextIssue NormalizeForApply(TextIssue issue)
    {
        if (issue.Category == IssueCategory.Punctuation
            && issue.Length == 1
            && !string.IsNullOrEmpty(issue.Original)
            && issue.Replacement is { Length: 2 } rep
            && rep.StartsWith(issue.Original, StringComparison.Ordinal)
            && IsPunctuationChar(rep[^1]))
        {
            return issue with
            {
                Start = issue.Start + issue.Length,
                Length = 0,
                Original = string.Empty,
                Replacement = rep[^1].ToString(),
                CanApplyAutomatically = true
            };
        }

        return issue;
    }

    public static bool IsTerminalPunctuationInsert(TextIssue issue)
        => issue.Length == 0
           && !string.IsNullOrEmpty(issue.Replacement)
           && (issue.Category == IssueCategory.Punctuation
               || issue.RuleId.Contains("punctuation", StringComparison.OrdinalIgnoreCase)
               || issue.RuleId.Contains("terminal", StringComparison.OrdinalIgnoreCase));

    private static bool IsPunctuationChar(char c)
        => c is '.' or ',' or '!' or '?' or ':' or ';' or '…';
}
