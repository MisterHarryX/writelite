using WriteLite.AI.Contracts;

namespace WriteLite.AI.Local;

/// <summary>
/// Validates model/engine outputs before they reach the UI pipeline.
/// Never trusts offsets or rewrites blindly.
/// </summary>
public sealed class AiResultValidator
{
    public double MaxOverallExpandRatio { get; set; } = 1.5;
    public double MaxOverallShrinkRatio { get; set; } = 0.55;
    public double MaxSingleIssueExpand { get; set; } = 4.0;

    public AiTextAnalysisResult Validate(
        string originalText,
        AiTextAnalysisResult candidate)
    {
        if (candidate.SchemaVersion != AiSchema.CurrentVersion && candidate.SchemaVersion != 0)
        {
            return Fail(originalText, candidate, AiErrorCodes.SchemaMismatch, "Несовместимая версия схемы ответа.");
        }

        var corrected = candidate.CorrectedText ?? originalText;
        if (!IsRewriteSane(originalText, corrected))
        {
            return Fail(originalText, candidate, AiErrorCodes.ExcessiveRewrite, "Модель слишком сильно изменила текст.");
        }

        // Protect URLs/emails/paths/numbers presence.
        if (!PreservesProtectedTokens(originalText, corrected))
        {
            return Fail(originalText, candidate, AiErrorCodes.ExcessiveRewrite, "Исправлены защищённые фрагменты (URL/email/путь/число).");
        }

        var issues = new List<AiTextIssue>();
        var occupied = new List<(int Start, int End)>();

        // Prefer higher-confidence, shorter spans first so large bad hunks do not occupy the range.
        foreach (var issue in candidate.Issues
                     .OrderBy(i => i.Start)
                     .ThenByDescending(i => i.Confidence)
                     .ThenBy(i => i.Length))
        {
            if (issue.Start < 0 || issue.Length < 0 || issue.Start + issue.Length > originalText.Length)
            {
                continue;
            }

            var slice = originalText.Substring(issue.Start, issue.Length);
            if (!string.Equals(slice, issue.Original, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(issue.Original, issue.Replacement, StringComparison.Ordinal))
            {
                continue;
            }

            if (issue.Original.Length > 0
                && issue.Replacement.Length > issue.Original.Length * MaxSingleIssueExpand
                && issue.Replacement.Length > 32)
            {
                continue;
            }

            // Reject issue-level rewrites that destroy most of the span content.
            if (issue.Original.Length >= 4)
            {
                var oLetters = issue.Original.Count(char.IsLetter);
                var rLetters = issue.Replacement.Count(char.IsLetter);
                if (oLetters >= 4 && rLetters < oLetters * 0.4)
                {
                    continue;
                }

                if (issue.Replacement.Length < issue.Original.Length * 0.35
                    && issue.Original.Length >= 8)
                {
                    continue;
                }
            }

            var end = issue.Start + issue.Length;
            if (occupied.Any(o => issue.Start < o.End && end > o.Start))
            {
                continue;
            }

            var conf = Math.Clamp(issue.Confidence, 0, 1);
            issues.Add(issue with
            {
                Confidence = conf,
                LowConfidence = conf < 0.7 || issue.LowConfidence,
                SafeToApply = issue.SafeToApply && conf >= 0.75 && issue.Length <= 64
            });
            occupied.Add((issue.Start, end));
        }

        // Deterministic corrected text from validated issues when possible.
        var rebuilt = issues.Count > 0
            ? TextDiffBuilder.ApplyIssuesRightToLeft(originalText, issues)
            : corrected;

        if (!IsRewriteSane(originalText, rebuilt) || !PreservesProtectedTokens(originalText, rebuilt))
        {
            rebuilt = originalText;
            issues = [];
        }

        var uncertain = candidate.Uncertain || issues.Any(i => i.LowConfidence);
        return candidate with
        {
            CorrectedText = rebuilt,
            Issues = issues,
            Uncertain = uncertain,
            ErrorCode = AiErrorCodes.Ok,
            Warning = uncertain
                ? (candidate.Warning ?? "Модель не уверена в части исправлений.")
                : candidate.Warning
        };
    }

    public bool IsRewriteSane(string original, string corrected)
    {
        if (corrected is null)
        {
            return false;
        }

        if (original.Length == 0)
        {
            return corrected.Length <= 64;
        }

        if (corrected.Length > original.Length * MaxOverallExpandRatio + 32)
        {
            return false;
        }

        if (corrected.Length < original.Length * MaxOverallShrinkRatio)
        {
            return false;
        }

        var lettersOrig = original.Count(char.IsLetter);
        var lettersNew = corrected.Count(char.IsLetter);
        if (lettersOrig >= 10 && lettersNew < lettersOrig * 0.4)
        {
            return false;
        }

        // Reject markdown/report wrappers.
        if (corrected.Contains("```", StringComparison.Ordinal)
            || corrected.Contains("**Испра", StringComparison.OrdinalIgnoreCase)
            || corrected.StartsWith("Sure,", StringComparison.OrdinalIgnoreCase)
            || corrected.StartsWith("Here's", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public bool PreservesProtectedTokens(string original, string corrected)
    {
        var spans = ProtectedSpanDetector.FindProtectedSpans(original);
        foreach (var (start, length) in spans)
        {
            var token = original.Substring(start, length);
            // Numbers may be reformatted rarely — require exact presence for URLs/emails/paths.
            if (token.Contains('@') || token.Contains("://", StringComparison.Ordinal) || token.Contains('\\') || token.Contains('/'))
            {
                if (!corrected.Contains(token, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            else if (token.Any(char.IsDigit) && token.Length >= 1)
            {
                // Require digit sequences preserved.
                if (!corrected.Contains(token, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            else if (token.Contains("WriteLite", StringComparison.Ordinal))
            {
                if (!corrected.Contains(token, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            else if (token.Any(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
                     && !corrected.Contains(token, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static AiTextAnalysisResult Fail(
        string original,
        AiTextAnalysisResult candidate,
        string code,
        string warning)
        => candidate with
        {
            CorrectedText = original,
            Issues = [],
            Uncertain = true,
            ErrorCode = code,
            Warning = warning
        };
}
