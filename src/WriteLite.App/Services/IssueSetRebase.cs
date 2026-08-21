using WriteLite.Models;

namespace WriteLite.Services;

/// <summary>
/// Keeps a published issue set correct across an edit the caller already knows the shape of.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> Applying a correction used to leave the sidebar untouched and
/// wait for the next analysis to rebuild it. On the shipping stack that wait was 24–53 s,
/// during which the card the user had just applied was still on screen, still enabled, and
/// still described a span whose text had changed underneath it. The user reads that as "the
/// correction came back", and clicking it again does nothing, because the original no longer
/// matches.</para>
///
/// <para>An apply is the one edit whose exact shape is known before it happens — offset,
/// removed length, inserted length. That is enough to keep every other finding in the document
/// pointing at the right characters without asking any analyzer anything. Re-analysis then
/// becomes a background correctness check rather than the thing the UI is waiting for.</para>
/// </remarks>
public static class IssueSetRebase
{
    /// <summary>
    /// The issue set as it stands after replacing <paramref name="length"/> characters at
    /// <paramref name="start"/> with <paramref name="replacementLength"/> characters.
    /// </summary>
    /// <remarks>
    /// Three outcomes per issue, in order:
    /// <list type="bullet">
    /// <item>Entirely before the edit — unchanged.</item>
    /// <item>Entirely after it — shifted by the delta.</item>
    /// <item>Overlapping it — dropped. Its span no longer describes the text it was about, and
    /// a finding that has to be guessed at is worse than one the next pass will find again.</item>
    /// </list>
    /// </remarks>
    public static IReadOnlyList<TextIssue> AfterReplacement(
        IReadOnlyList<TextIssue> issues,
        int start,
        int length,
        int replacementLength)
    {
        if (issues is null || issues.Count == 0) return [];

        var delta = replacementLength - length;
        var editEnd = start + length;
        var result = new List<TextIssue>(issues.Count);

        foreach (var issue in issues)
        {
            var issueEnd = issue.Start + issue.Length;

            if (issueEnd <= start)
            {
                result.Add(issue);
                continue;
            }

            if (issue.Start >= editEnd)
            {
                result.Add(issue with { Start = issue.Start + delta });
                continue;
            }

            // Overlaps the edit: dropped deliberately, see remarks.
        }

        return result;
    }

    /// <summary>
    /// Drops every issue whose recorded <see cref="TextIssue.Original"/> is no longer the text
    /// at its span. The last line of defence before a set reaches the UI.
    /// </summary>
    public static IReadOnlyList<TextIssue> ValidAgainst(IReadOnlyList<TextIssue> issues, string text)
    {
        if (issues is null || issues.Count == 0) return [];
        text ??= string.Empty;

        var result = new List<TextIssue>(issues.Count);
        foreach (var issue in issues)
        {
            if (issue.Start < 0 || issue.Length < 0 || issue.Start + issue.Length > text.Length)
            {
                continue;
            }

            if (issue.Length == 0
                || string.Equals(
                    text.Substring(issue.Start, issue.Length), issue.Original, StringComparison.Ordinal))
            {
                result.Add(issue);
            }
        }

        return result;
    }
}
