using WriteLite.Models;

namespace WriteLite.Services.Ai;

public sealed class AnalysisResultValidator
{
    /// <summary>Reject replacements that remove more than this fraction of the original span.</summary>
    public double MaxDeletionRatio { get; set; } = 0.55;

    /// <summary>Reject full-text rewrite that shrinks overall text more than this fraction.</summary>
    public double MaxOverallShrinkRatio { get; set; } = 0.35;

    public bool IsRangeValid(string text, int start, int length)
        => start >= 0 && length >= 0 && start <= text.Length && start + length <= text.Length;

    public bool OriginalMatches(string text, int start, int length, string? original)
    {
        if (!IsRangeValid(text, start, length))
        {
            return false;
        }

        if (length == 0)
        {
            return original is not null && original.Length == 0;
        }

        if (string.IsNullOrEmpty(original))
        {
            return false;
        }

        var slice = text.Substring(start, length);
        return string.Equals(slice, original, StringComparison.Ordinal);
    }

    public bool IsReplacementSane(string original, string? replacement)
    {
        if (replacement is null)
        {
            return false;
        }

        // Empty replacement only allowed for pure whitespace/punctuation cleanup of short spans.
        if (replacement.Length == 0)
        {
            return original.Length > 0 && original.Length <= 3 && original.All(ch => char.IsWhiteSpace(ch) || char.IsPunctuation(ch));
        }

        if (original.Length == 0)
        {
            return replacement.Length <= 32;
        }

        var shrink = 1.0 - (double)replacement.Length / original.Length;
        if (shrink > MaxDeletionRatio)
        {
            return false;
        }

        return true;
    }

    public bool IsCorrectedTextSane(string original, string? corrected)
    {
        if (string.IsNullOrWhiteSpace(corrected))
        {
            return false;
        }

        // Reject AI/report-style wrappers and injected Markdown.
        var sanitized = TextOutputSanitizer.SanitizeCorrection(original, corrected);
        if (sanitized is null)
        {
            return false;
        }

        corrected = sanitized;

        if (original.Length == 0)
        {
            return corrected.Length <= 64;
        }

        var shrink = 1.0 - (double)corrected.Length / original.Length;
        if (shrink > MaxOverallShrinkRatio)
        {
            return false;
        }

        var lettersOrig = original.Count(char.IsLetter);
        var lettersNew = corrected.Count(char.IsLetter);
        if (lettersOrig >= 10 && lettersNew < lettersOrig * 0.4)
        {
            return false;
        }

        return true;
    }

    /// <summary>Sanitize + validate corrected full text; null if unusable.</summary>
    public string? NormalizeCorrectedText(string original, string? corrected)
    {
        if (!IsCorrectedTextSane(original, corrected))
        {
            return null;
        }

        return TextOutputSanitizer.SanitizeCorrection(original, corrected);
    }

    public IReadOnlyList<TextIssue> FilterValidIssues(string currentText, IEnumerable<TextIssue> issues)
    {
        var list = new List<TextIssue>();
        foreach (var issue in issues)
        {
            if (!IsRangeValid(currentText, issue.Start, issue.Length))
            {
                continue;
            }

            if (issue.Length == 0)
            {
                if (issue.Original.Length != 0)
                {
                    continue;
                }
            }
            else if (!string.Equals(
                         currentText.Substring(issue.Start, issue.Length),
                         issue.Original,
                         StringComparison.Ordinal))
            {
                continue;
            }

            var replacement = issue.Replacement;
            if (replacement is not null)
            {
                replacement = TextOutputSanitizer.SanitizeReplacement(issue.Original, replacement);
                if (replacement is null)
                {
                    continue;
                }

                if (!IsReplacementSane(issue.Original, replacement))
                {
                    continue;
                }
            }

            if (string.Equals(issue.Original, replacement, StringComparison.Ordinal))
            {
                continue;
            }

            var safe = issue.CanApplyAutomatically;
            if (safe && TextOutputSanitizer.MessageContradictsSafeApply(issue.Explanation, safe))
            {
                safe = false;
            }

            if (safe && replacement is null)
            {
                safe = false;
            }

            list.Add(issue with
            {
                Replacement = replacement,
                CanApplyAutomatically = safe
            });
        }

        return list;
    }
}
