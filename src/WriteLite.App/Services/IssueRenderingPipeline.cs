using WriteLite.Language.Core;
using WriteLite.Models;

namespace WriteLite.Services;

/// <summary>Rejects issues whose exact UTF-16 range is stale or protected.</summary>
public sealed class IssueProtectedRangeFilter
{
    public IReadOnlyList<TextIssue> Filter(string text, IEnumerable<TextIssue> issues)
    {
        var protectedSpans = ProtectedTextSpans.Find(text);
        return issues.Where(issue => IsCurrentExactRange(text, issue)
                                     && !ProtectedTextSpans.Overlaps(issue.Start, issue.Length, protectedSpans))
            .ToList();
    }

    public static bool IsCurrentExactRange(string text, TextIssue issue)
    {
        if (!TextCorrectionService.IsRangeValid(text, issue.Start, issue.Length)) return false;
        if (SplitsSurrogatePair(text, issue.Start) || SplitsSurrogatePair(text, issue.Start + issue.Length)) return false;
        return issue.Length == 0
            ? issue.Original.Length == 0
            : string.Equals(text.Substring(issue.Start, issue.Length), issue.Original, StringComparison.Ordinal);
    }

    private static bool SplitsSurrogatePair(string text, int index) =>
        index > 0 && index < text.Length && char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]);
}

/// <summary>Category-specific confidence threshold for inline rendering.</summary>
public sealed class IssueConfidencePolicy
{
    public double OrthographyThreshold { get; init; } = 0.72;
    public double PunctuationThreshold { get; init; } = 0.70;
    public double GrammarThreshold { get; init; } = 0.76;
    public double LexicalThreshold { get; init; } = 0.82;
    public double StyleThreshold { get; init; } = 0.84;

    public bool Allows(TextIssue issue)
    {
        var threshold = issue.Category switch
        {
            IssueCategory.Orthography => OrthographyThreshold,
            IssueCategory.Punctuation => PunctuationThreshold,
            IssueCategory.Grammar => GrammarThreshold,
            IssueCategory.Style => StyleThreshold,
            IssueCategory.Readability => LexicalThreshold,
            _ => 1.0
        };
        return !double.IsNaN(issue.Confidence) && Math.Clamp(issue.Confidence, 0, 1) >= threshold;
    }
}

/// <summary>Collapses exact and semantic duplicates before geometry creation.</summary>
public sealed class IssueDeduplicationService
{
    public IReadOnlyList<TextIssue> Deduplicate(IEnumerable<TextIssue> issues) => issues
        .GroupBy(IssueKey.Create)
        .Select(group => group.OrderByDescending(Score).ThenBy(i => i.RuleId, StringComparer.Ordinal).First())
        .OrderBy(i => i.Start)
        .ThenBy(i => i.Length)
        .ToList();

    private static double Score(TextIssue issue) =>
        SourcePriority(issue) * 100 + CategoryPriority(issue.Category) * 10 + issue.Confidence
        + (issue.CanApplyAutomatically ? 1 : 0);

    internal static int SourcePriority(TextIssue issue) => issue.RuleId switch
    {
        var id when id.StartsWith("WL-AI-", StringComparison.OrdinalIgnoreCase) => 1,
        var id when id.StartsWith("WL-", StringComparison.OrdinalIgnoreCase) => 2,
        _ => 3
    };

    internal static int CategoryPriority(IssueCategory category) => category switch
    {
        IssueCategory.Orthography => 6,
        IssueCategory.Punctuation => 5,
        IssueCategory.Grammar => 4,
        IssueCategory.Style => 2,
        IssueCategory.Readability => 1,
        _ => 0
    };

    private readonly record struct IssueKey(int Start, int Length, string Original, string Replacement)
    {
        public static IssueKey Create(TextIssue issue) => new(
            issue.Start,
            issue.Length,
            issue.Original,
            issue.Replacement ?? string.Empty);
    }
}

/// <summary>Keeps the highest-priority issue for overlapping inline ranges.</summary>
public sealed class IssueOverlapResolver
{
    public IReadOnlyList<TextIssue> Resolve(IEnumerable<TextIssue> issues)
    {
        var accepted = new List<TextIssue>();
        foreach (var candidate in issues.OrderByDescending(Priority).ThenBy(i => i.Start).ThenByDescending(i => i.Length))
        {
            if (accepted.Any(existing => Overlaps(existing, candidate))) continue;
            accepted.Add(candidate);
        }

        return accepted.OrderBy(i => i.Start).ThenBy(i => i.Length).ToList();
    }

    private static double Priority(TextIssue issue) =>
        IssueDeduplicationService.SourcePriority(issue) * 100
        + IssueDeduplicationService.CategoryPriority(issue.Category) * 10
        + issue.Confidence;

    private static bool Overlaps(TextIssue left, TextIssue right)
    {
        if (left.Length == 0 || right.Length == 0)
            return left.Start == right.Start;
        return left.Start < right.Start + right.Length && right.Start < left.Start + left.Length;
    }
}

/// <summary>Single strict path used before issue rendering.</summary>
public sealed class IssueRenderingPipeline
{
    private readonly IssueProtectedRangeFilter _rangeFilter = new();
    private readonly IssueConfidencePolicy _confidence;
    private readonly IssueDeduplicationService _deduplication = new();
    private readonly IssueOverlapResolver _overlap = new();

    public IssueRenderingPipeline(IssueConfidencePolicy? confidence = null) =>
        _confidence = confidence ?? new IssueConfidencePolicy();

    public IReadOnlyList<TextIssue> Filter(string text, IEnumerable<TextIssue> candidates)
    {
        var exact = _rangeFilter.Filter(text, candidates)
            .Where(_confidence.Allows)
            .Where(issue =>
                issue.Replacement is null
                || (!CorrectionCandidateValidityPolicy.IsIdenticalCorrection(issue.Original, issue.Replacement)
                    && CorrectionCandidateValidityPolicy.WouldChangeText(
                        text, issue.Start, issue.Length, issue.Replacement)));
        return _overlap.Resolve(_deduplication.Deduplicate(exact));
    }

    /// <summary>
    /// Inline decorations require an exact actionable replacement. Automatic
    /// safety is not required because a single click only opens a confirmation card.
    /// </summary>
    public static bool IsInlineCorrectionCandidate(TextIssue issue) =>
        !string.IsNullOrWhiteSpace(issue.Replacement)
        && !CorrectionCandidateValidityPolicy.IsIdenticalCorrection(issue.Original, issue.Replacement);
}

public sealed class IssueGeometryValidator
{
    public IReadOnlyList<System.Windows.Rect> Filter(
        IReadOnlyList<System.Windows.Rect> rectangles,
        System.Windows.Rect? fieldBounds = null) =>
        InlineIssueGeometryBuilder.FilterPreciseRectangles(rectangles, fieldBounds);
}
