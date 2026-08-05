using WriteLite.Language.Core;
using WriteLite.Models;

namespace WriteLite.Services;

public static class TextCorrectionService
{
    public static bool CanApply(TextIssue issue, string currentText, bool supportsDirectWrite)
    {
        if (!supportsDirectWrite
            || !issue.CanApplyAutomatically
            || issue.Replacement is null)
        {
            return false;
        }

        if (CorrectionCandidateValidityPolicy.IsIdenticalCorrection(issue.Original, issue.Replacement)
            || CorrectionCandidateValidityPolicy.IsMeaninglessSpellingCandidate(issue.Original, issue.Replacement))
        {
            return false;
        }

        if (!IsRangeValid(currentText, issue.Start, issue.Length))
        {
            return false;
        }

        // Length == 0 is a pure insert: Original must be empty.
        if (issue.Length == 0)
        {
            return issue.Original.Length == 0 && issue.Replacement.Length > 0
                && !CorrectionCandidateValidityPolicy.IsIdenticalCorrection(string.Empty, issue.Replacement);
        }

        // Stale Start/Length protection: original fragment must still match.
        return string.Equals(
            currentText.Substring(issue.Start, issue.Length),
            issue.Original,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Validates a user-selected correction. Unlike batch correction, this does not
    /// reject a concrete choice merely because the analyser marked it conservative.
    /// The target, UTF-16 range, original fragment and replacement are still required.
    /// </summary>
    public static bool CanApplyManual(TextIssue issue, string currentText, bool supportsDirectWrite)
    {
        if (!supportsDirectWrite || string.IsNullOrEmpty(issue.Replacement)
            || !IsRangeValid(currentText, issue.Start, issue.Length))
        {
            return false;
        }

        if (CorrectionCandidateValidityPolicy.IsIdenticalCorrection(issue.Original, issue.Replacement)
            || !CorrectionCandidateValidityPolicy.WouldChangeText(
                currentText, issue.Start, issue.Length, issue.Replacement))
        {
            return false;
        }

        return issue.Length == 0
            ? issue.Original.Length == 0
            : string.Equals(currentText.Substring(issue.Start, issue.Length), issue.Original, StringComparison.Ordinal);
    }

    public static bool TryApplyManual(string currentText, TextIssue issue, bool supportsDirectWrite, out string newText, out int caretIndex)
    {
        newText = currentText;
        caretIndex = -1;
        if (!CanApplyManual(issue, currentText, supportsDirectWrite)) return false;

        newText = currentText.Remove(issue.Start, issue.Length).Insert(issue.Start, issue.Replacement!);
        caretIndex = issue.Start + issue.Replacement!.Length;
        return true;
    }

    public static bool IsRangeValid(string text, int start, int length)
    {
        return start >= 0 && length >= 0 && start <= text.Length && start + length <= text.Length;
    }

    public static int CountApplicable(IEnumerable<TextIssue> issues, string currentText, bool supportsDirectWrite)
    {
        return issues.Count(issue => CanApply(issue, currentText, supportsDirectWrite));
    }

    public static bool TryApplySingle(
        string currentText,
        TextIssue issue,
        bool supportsDirectWrite,
        out string newText)
        => TryApplySingle(currentText, issue, supportsDirectWrite, out newText, out _);

    public static bool TryApplySingle(
        string currentText,
        TextIssue issue,
        bool supportsDirectWrite,
        out string newText,
        out int caretIndex)
    {
        newText = currentText;
        caretIndex = -1;
        if (!CanApply(issue, currentText, supportsDirectWrite))
        {
            return false;
        }

        newText = currentText.Remove(issue.Start, issue.Length).Insert(issue.Start, issue.Replacement!);
        caretIndex = issue.Start + issue.Replacement!.Length;
        return true;
    }

    /// <summary>
    /// Selects non-overlapping applicable issues from the end of the string.
    /// Zero-length inserts: only one per Start position.
    /// </summary>
    public static IReadOnlyList<TextIssue> SelectApplicableAll(
        IEnumerable<TextIssue> issues,
        string currentText,
        bool supportsDirectWrite)
    {
        var result = new List<TextIssue>();
        // nextStart is the exclusive right boundary of the still-free prefix.
        var nextStart = currentText.Length + 1;

        foreach (var issue in issues
                     .OrderByDescending(i => i.Start)
                     .ThenByDescending(i => i.Length))
        {
            if (!CanApply(issue, currentText, supportsDirectWrite))
            {
                continue;
            }

            // Must lie entirely to the left of nextStart and not start at/after it.
            if (issue.Start >= nextStart)
            {
                continue;
            }

            if (issue.Start + issue.Length > nextStart)
            {
                continue;
            }

            result.Add(issue);
            nextStart = issue.Start;
        }

        return result;
    }

    public static CorrectionComposeResult ComposeApplyAll(
        string currentText,
        IEnumerable<TextIssue> issues,
        bool supportsDirectWrite)
    {
        var issueList = issues as IReadOnlyList<TextIssue> ?? issues.ToArray();
        var applicable = SelectApplicableAll(issueList, currentText, supportsDirectWrite);
        if (applicable.Count == 0)
        {
            return new CorrectionComposeResult(
                CanApply: false,
                NewText: currentText,
                AppliedCount: 0,
                SkippedCount: issueList.Count,
                RemainingRecommendations: issueList.Count,
                AppliedIssues: []);
        }

        // Apply from end so earlier offsets stay valid.
        var newText = currentText;
        foreach (var issue in applicable.OrderByDescending(i => i.Start).ThenByDescending(i => i.Length))
        {
            // Re-validate against the evolving string for non-zero lengths at original indices
            // (still valid because we apply strictly from the end without overlapping).
            if (issue.Length == 0)
            {
                if (issue.Start > newText.Length)
                {
                    continue;
                }

                newText = newText.Insert(issue.Start, issue.Replacement!);
            }
            else
            {
                if (!IsRangeValid(newText, issue.Start, issue.Length)
                    || !string.Equals(newText.Substring(issue.Start, issue.Length), issue.Original, StringComparison.Ordinal))
                {
                    continue;
                }

                newText = newText.Remove(issue.Start, issue.Length).Insert(issue.Start, issue.Replacement!);
            }
        }

        var remaining = issueList.Count - applicable.Count;
        var skipped = issueList.Count(issue =>
            issue.CanApplyAutomatically &&
            issue.Replacement is not null) - applicable.Count;

        return new CorrectionComposeResult(
            CanApply: true,
            NewText: newText,
            AppliedCount: applicable.Count,
            SkippedCount: Math.Max(0, skipped),
            RemainingRecommendations: remaining,
            AppliedIssues: applicable);
    }

    public static bool TryApplyAll(
        string currentText,
        IEnumerable<TextIssue> issues,
        bool supportsDirectWrite,
        out string newText)
    {
        var composed = ComposeApplyAll(currentText, issues, supportsDirectWrite);
        newText = composed.NewText;
        return composed.CanApply;
    }

    /// <summary>
    /// Preferred caret after applying a batch: end of the left-most applied replacement.
    /// </summary>
    public static int EstimateCaretAfterApplyAll(IReadOnlyList<TextIssue> appliedIssues)
    {
        if (appliedIssues.Count == 0)
        {
            return -1;
        }

        var first = appliedIssues.OrderBy(i => i.Start).First();
        return first.Start + (first.Replacement?.Length ?? 0);
    }
}
