using WriteLite.Models;

namespace WriteLite.Services.Ai;

/// <summary>
/// Applies a set of issues to text, and checks that a diff round-trips.
/// </summary>
/// <remarks>
/// <para><b>The invariant §28 asks for.</b> When the model returns a corrected sentence, the
/// pipeline turns it into a list of spans and then shows those spans to the user. If applying
/// every span to the original does not reproduce the corrected sentence, the span list is not
/// a description of the model's answer — it is a mangling of it, and the user is being offered
/// edits nobody produced. <c>«что то» → «, что»</c> is what that looks like from the outside.
/// </para>
///
/// <para>Checking it is one string comparison, so it is checked rather than assumed. A diff
/// that fails the check is discarded whole: partially trusting a span list whose provenance is
/// in doubt is how a corrupted edit reaches the text.</para>
///
/// <para>Overlapping issues are not a failure. Independent analyzers legitimately find
/// different things in the same place, and the merge decides between them; what this type
/// answers is whether one <em>diff</em> reconstructs one <em>rewrite</em>, so it reports an
/// overlap as "cannot verify" and the caller treats that the same as a mismatch.</para>
/// </remarks>
public static class IssueApplication
{
    /// <summary>Why a span list could not be applied.</summary>
    public enum ApplyStatus
    {
        Applied,
        OutOfRange,
        OriginalMismatch,
        Overlapping,
    }

    public readonly record struct ApplyResult(ApplyStatus Status, string Text)
    {
        public bool Ok => Status == ApplyStatus.Applied;
    }

    /// <summary>
    /// <paramref name="original"/> with every issue's replacement substituted.
    /// </summary>
    /// <remarks>
    /// Right to left, so that applying one edit does not move the offsets of the edits that
    /// have not been applied yet. Every span is checked against the text it claims to replace
    /// before anything is written, because an issue whose <see cref="TextIssue.Original"/> no
    /// longer matches the document is stale and applying it would corrupt an unrelated word.
    /// </remarks>
    public static ApplyResult Apply(string original, IReadOnlyList<TextIssue> issues)
    {
        if (string.IsNullOrEmpty(original) || issues.Count == 0)
        {
            return new ApplyResult(ApplyStatus.Applied, original ?? string.Empty);
        }

        var ordered = issues
            .Where(i => i.Replacement is not null)
            .OrderBy(i => i.Start)
            .ThenBy(i => i.Length)
            .ToList();

        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Start < ordered[i - 1].Start + ordered[i - 1].Length)
            {
                return new ApplyResult(ApplyStatus.Overlapping, original);
            }
        }

        foreach (var issue in ordered)
        {
            if (issue.Start < 0 || issue.Length < 0 || issue.Start + issue.Length > original.Length)
            {
                return new ApplyResult(ApplyStatus.OutOfRange, original);
            }

            if (!string.Equals(
                    original.Substring(issue.Start, issue.Length),
                    issue.Original,
                    StringComparison.Ordinal))
            {
                return new ApplyResult(ApplyStatus.OriginalMismatch, original);
            }
        }

        var builder = new System.Text.StringBuilder(original);
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var issue = ordered[i];
            builder.Remove(issue.Start, issue.Length);
            builder.Insert(issue.Start, issue.Replacement!);
        }

        return new ApplyResult(ApplyStatus.Applied, builder.ToString());
    }

    /// <summary>
    /// True when applying <paramref name="issues"/> to <paramref name="original"/> reproduces
    /// <paramref name="corrected"/> exactly.
    /// </summary>
    public static bool Reconstructs(string original, string corrected, IReadOnlyList<TextIssue> issues)
    {
        var result = Apply(original, issues);
        return result.Ok && string.Equals(result.Text, corrected, StringComparison.Ordinal);
    }
}
