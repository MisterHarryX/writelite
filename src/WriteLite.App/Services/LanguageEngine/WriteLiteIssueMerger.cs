using System.Diagnostics;
using WriteLite.Models;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Merges issue lists from built-in WriteLite analyzers and mapped engine results.
/// Does not mutate inputs. Safe for Apply All via non-overlapping CanApplyAutomatically flags.
/// </summary>
public sealed class WriteLiteIssueMerger
{
    public const int DefaultMaxIssues = 200;

    public WriteLiteIssueMergeResult Merge(
        IEnumerable<TextIssue>? primaryWriteLiteIssues,
        IEnumerable<TextIssue>? secondaryWriteLiteIssues = null,
        IEnumerable<TextIssue>? engineMappedIssues = null,
        int maxIssues = DefaultMaxIssues)
    {
        var sw = Stopwatch.StartNew();

        var primary = (primaryWriteLiteIssues ?? []).ToList();
        var secondary = (secondaryWriteLiteIssues ?? []).ToList();
        var engine = (engineMappedIssues ?? []).ToList();

        // Tag source priority via RuleId prefix heuristics (WL- from engine factory vs built-in ids).
        var bag = new List<ScoredIssue>();
        foreach (var issue in primary)
        {
            bag.Add(Score(issue, sourceRank: 0));
        }

        foreach (var issue in secondary)
        {
            bag.Add(Score(issue, sourceRank: 1));
        }

        foreach (var issue in engine)
        {
            bag.Add(Score(issue, sourceRank: 2));
        }

        // Deterministic order before dedup.
        bag = bag
            .OrderBy(s => s.Issue.Start)
            .ThenBy(s => s.Issue.Length)
            .ThenBy(s => s.SourceRank)
            .ThenBy(s => s.Issue.RuleId, StringComparer.Ordinal)
            .ThenBy(s => s.Issue.Title, StringComparer.Ordinal)
            .ToList();

        var accepted = new List<ScoredIssue>();
        var duplicateCount = 0;
        var conflictCount = 0;

        foreach (var candidate in bag)
        {
            var drop = false;
            for (var i = 0; i < accepted.Count; i++)
            {
                var existing = accepted[i];
                if (IsSameCommaInsertion(existing.Issue, candidate.Issue))
                {
                    accepted[i] = MergeCommaInsertions(existing, candidate);
                    duplicateCount++;
                    drop = true;
                    break;
                }

                if (IsExactOrSemanticDuplicate(existing.Issue, candidate.Issue))
                {
                    accepted[i] = MergeDuplicateMetadata(existing, candidate);

                    duplicateCount++;
                    drop = true;
                    break;
                }

                if (RangesOverlap(existing.Issue, candidate.Issue))
                {
                    // Different useful same-range categories: keep both if no replacement conflict.
                    if (existing.Issue.Start == candidate.Issue.Start
                        && existing.Issue.Length == candidate.Issue.Length
                        && existing.Issue.Category != candidate.Issue.Category
                        && !SameReplacement(existing.Issue, candidate.Issue))
                    {
                        // Keep both — Apply All will skip overlapping via SelectApplicableAll.
                        continue;
                    }

                    // Overlap with conflicting apply: keep better, demote loser for Apply All.
                    if (Prefer(candidate, existing) > 0)
                    {
                        accepted[i] = DemoteForApplyAll(existing);
                        // candidate stays for insert below
                        conflictCount++;
                    }
                    else if (Prefer(candidate, existing) < 0)
                    {
                        candidate.Issue = DemoteForApplyAll(candidate).Issue;
                        conflictCount++;
                    }
                    else
                    {
                        // Equal preference: demote the later one (candidate).
                        candidate.Issue = DemoteForApplyAll(candidate).Issue;
                        conflictCount++;
                    }
                }
            }

            if (!drop)
            {
                accepted.Add(candidate);
            }
        }

        // Re-sort after possible replacements in list.
        var merged = accepted
            .Select(s => s.Issue)
            .OrderBy(i => i.Start)
            .ThenByDescending(i => i.Severity)
            .ThenBy(i => i.RuleId, StringComparer.Ordinal)
            .Take(Math.Max(1, maxIssues))
            .ToList();

        // Ensure Apply All set is non-overlapping: demote lower-priority overlapping autos.
        EnforceNonOverlappingAutoApply(merged);

        sw.Stop();
        var safeCount = merged.Count(i => i.CanApplyAutomatically && !string.IsNullOrEmpty(i.Replacement));
        CompatibilityLogger.Technical(
            "language-engine-merge-completed",
            $"mappedCount={merged.Count} duplicateCount={duplicateCount} conflictCount={conflictCount} safeCount={safeCount} durationMs={sw.ElapsedMilliseconds}");

        return new WriteLiteIssueMergeResult(
            merged,
            duplicateCount,
            conflictCount,
            safeCount,
            sw.Elapsed);
    }

    /// <summary>
    /// Issues safe for current Apply All (non-overlapping, CanApplyAutomatically).
    /// Mirrors <see cref="TextCorrectionService.SelectApplicableAll"/> ordering from end.
    /// </summary>
    public static IReadOnlyList<TextIssue> SelectSafeForApplyAll(IEnumerable<TextIssue> issues)
    {
        var list = issues.ToList();
        var result = new List<TextIssue>();
        var nextStart = int.MaxValue;

        foreach (var issue in list.OrderByDescending(i => i.Start).ThenByDescending(i => i.Length))
        {
            if (!issue.CanApplyAutomatically || string.IsNullOrEmpty(issue.Replacement))
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

    private static void EnforceNonOverlappingAutoApply(List<TextIssue> merged)
    {
        var autos = merged
            .Select((issue, index) => (issue, index))
            .Where(t => t.issue.CanApplyAutomatically && !string.IsNullOrEmpty(t.issue.Replacement))
            .OrderBy(t => t.issue.Start)
            .ThenBy(t => t.issue.Length)
            .ToList();

        for (var i = 0; i < autos.Count; i++)
        {
            for (var j = i + 1; j < autos.Count; j++)
            {
                var a = autos[i].issue;
                var b = autos[j].issue;
                if (!RangesOverlap(a, b))
                {
                    continue;
                }

                // Demote lower source priority / less specific.
                var demoteIndex = PreferRaw(a, b, sourceA: 0, sourceB: 0) >= 0
                    ? autos[j].index
                    : autos[i].index;
                var victim = merged[demoteIndex];
                merged[demoteIndex] = victim with { CanApplyAutomatically = false, Severity = IssueSeverity.Suggestion };
            }
        }
    }

    private static ScoredIssue Score(TextIssue issue, int sourceRank)
    {
        // Built-in rules without WL- prefix rank higher (sourceRank 0/1).
        // Engine WL-* ids are sourceRank 2.
        var isEngine = issue.RuleId.StartsWith("WL-", StringComparison.Ordinal);
        var effectiveSource = isEngine ? Math.Max(sourceRank, 2) : sourceRank;
        return new ScoredIssue(issue, effectiveSource);
    }

    private static ScoredIssue DemoteForApplyAll(ScoredIssue scored)
    {
        var issue = scored.Issue with
        {
            CanApplyAutomatically = false,
            Severity = IssueSeverity.Suggestion
        };
        return new ScoredIssue(issue, scored.SourceRank);
    }

    /// <summary>
    /// The absolute offset at which a punctuation finding inserts a comma, or null when it is
    /// not a pure comma insertion.
    /// </summary>
    /// <remarks>
    /// Two layers proposing the same comma describe it with different spans. The rules layer
    /// reports a zero-length insertion after the preceding word; LanguageTool reports the two
    /// words around the boundary and rewrites them with the comma in the middle. Both are
    /// legitimate shapes for the same edit, and comparing them by span — which is all
    /// <see cref="IsExactOrSemanticDuplicate"/> can do — finds no duplicate at all. The user
    /// then gets two cards for one comma.
    ///
    /// Reducing each to the position it inserts the comma at makes them comparable regardless
    /// of how much context the span carries.
    /// </remarks>
    private static int? CommaInsertionPosition(TextIssue issue)
    {
        if (issue.Category != IssueCategory.Punctuation) return null;

        var original = issue.Original ?? string.Empty;
        var replacement = issue.Replacement;
        if (replacement is null || replacement.Length != original.Length + 1) return null;

        var i = 0;
        while (i < original.Length && original[i] == replacement[i]) i++;
        if (replacement[i] != ',') return null;
        if (!string.Equals(original[i..], replacement[(i + 1)..], StringComparison.Ordinal)) return null;

        return issue.Start + i;
    }

    private static bool IsSameCommaInsertion(TextIssue a, TextIssue b)
    {
        var position = CommaInsertionPosition(a);
        return position is not null && position == CommaInsertionPosition(b);
    }

    /// <summary>
    /// One card for one comma: the span that shows the most context, with the most specific
    /// explanation available from either layer.
    /// </summary>
    /// <remarks>
    /// The wider span wins because it is what the user reads — «дачу а» → «дачу, а» shows the
    /// boundary, while a bare inserted comma renders as «[вставка]». The explanation is taken
    /// from the built-in rule when one is involved, because those carry a curated reason
    /// naming the construction, and the engine's mapped message is generic. §13 asks for the
    /// former; keeping the engine's span costs nothing and keeps the exact-span shape the
    /// benchmark scores.
    /// </remarks>
    private static ScoredIssue MergeCommaInsertions(ScoredIssue first, ScoredIssue second)
    {
        var (wider, narrower) = first.Issue.Length >= second.Issue.Length
            ? (first, second)
            : (second, first);

        var builtIn = !narrower.Issue.RuleId.StartsWith("WL-", StringComparison.Ordinal)
                      && wider.Issue.RuleId.StartsWith("WL-", StringComparison.Ordinal)
            ? narrower.Issue
            : null;

        if (builtIn is null) return wider;

        return new ScoredIssue(
            wider.Issue with
            {
                Title = builtIn.Title,
                Explanation = builtIn.Explanation,
                RuleId = builtIn.RuleId,
                LinguisticCategory = builtIn.LinguisticCategory,
                Confidence = Math.Max(wider.Issue.Confidence, builtIn.Confidence),
                CanApplyAutomatically = wider.Issue.CanApplyAutomatically && builtIn.CanApplyAutomatically,
            },
            Math.Min(wider.SourceRank, narrower.SourceRank));
    }

    private static ScoredIssue MergeDuplicateMetadata(ScoredIssue first, ScoredIssue second)
    {
        var winner = Prefer(second, first) > 0 ? second : first;
        var other = ReferenceEquals(winner, first) ? second : first;
        var strongestExplanation = other.Issue.Explanation.Length > winner.Issue.Explanation.Length
            ? other.Issue.Explanation
            : winner.Issue.Explanation;

        return new ScoredIssue(
            winner.Issue with
            {
                Explanation = strongestExplanation,
                Confidence = Math.Max(first.Issue.Confidence, second.Issue.Confidence),
                CanApplyAutomatically = first.Issue.CanApplyAutomatically || second.Issue.CanApplyAutomatically,
            },
            Math.Min(first.SourceRank, second.SourceRank));
    }

    private static bool IsExactOrSemanticDuplicate(TextIssue a, TextIssue b)
    {
        if (a.Start == b.Start && a.Length == b.Length
            && string.Equals(a.Original, b.Original, StringComparison.Ordinal)
            && SameReplacement(a, b)
            && a.Category == b.Category)
        {
            return true;
        }

        // A range alone is not an issue identity. Two analyzers may legitimately offer
        // different corrections for the same fragment; those must remain separate cards.
        if (a.Start == b.Start && a.Length == b.Length
            && string.Equals(a.Original, b.Original, StringComparison.Ordinal)
            && SameReplacement(a, b)
            && CategoriesClose(a.Category, b.Category))
        {
            return true;
        }

        // Same range + same replacement, close categories.
        if (a.Start == b.Start && a.Length == b.Length && SameReplacement(a, b)
            && CategoriesClose(a.Category, b.Category))
        {
            return true;
        }

        // Near-identical overlapping orthography (e.g. spell + builtin typo).
        if (a.Category == IssueCategory.Orthography && b.Category == IssueCategory.Orthography
            && RangesOverlap(a, b)
            && string.Equals(a.Original.Trim(), b.Original.Trim(), StringComparison.OrdinalIgnoreCase)
            && SameReplacement(a, b))
        {
            return true;
        }

        return false;
    }

    private static bool SameReplacement(TextIssue a, TextIssue b)
        => string.Equals(a.Replacement ?? string.Empty, b.Replacement ?? string.Empty, StringComparison.Ordinal);

    private static bool CategoriesClose(IssueCategory a, IssueCategory b)
        => a == b
           || (a is IssueCategory.Style or IssueCategory.Readability
               && b is IssueCategory.Style or IssueCategory.Readability);

    private static bool RangesOverlap(TextIssue a, TextIssue b)
    {
        var aEnd = a.Start + a.Length;
        var bEnd = b.Start + b.Length;
        return a.Start < bEnd && b.Start < aEnd;
    }

    /// <summary>Positive if a is better than b.</summary>
    private static int Prefer(ScoredIssue a, ScoredIssue b)
        => PreferRaw(a.Issue, b.Issue, a.SourceRank, b.SourceRank);

    private static int PreferRaw(TextIssue a, TextIssue b, int sourceA, int sourceB)
    {
        // Lower sourceRank = higher priority (built-in WriteLite first).
        if (sourceA != sourceB)
        {
            return sourceB.CompareTo(sourceA);
        }

        // Prefer auto-safe.
        var safeA = a.CanApplyAutomatically && !string.IsNullOrEmpty(a.Replacement) ? 1 : 0;
        var safeB = b.CanApplyAutomatically && !string.IsNullOrEmpty(b.Replacement) ? 1 : 0;
        if (safeA != safeB)
        {
            return safeA.CompareTo(safeB);
        }

        // Prefer more specific categories (not Readability/Other).
        var specA = Specificity(a.Category);
        var specB = Specificity(b.Category);
        if (specA != specB)
        {
            return specA.CompareTo(specB);
        }

        // Prefer single clear replacement (already reflected in CanApplyAutomatically).
        var repA = string.IsNullOrEmpty(a.Replacement) ? 0 : 1;
        var repB = string.IsNullOrEmpty(b.Replacement) ? 0 : 1;
        if (repA != repB)
        {
            return repA.CompareTo(repB);
        }

        // Prefer longer non-empty explanation (clarity).
        return a.Explanation.Length.CompareTo(b.Explanation.Length);
    }

    private static int Specificity(IssueCategory c) => c switch
    {
        IssueCategory.Orthography => 5,
        IssueCategory.Punctuation => 5,
        IssueCategory.Grammar => 4,
        IssueCategory.Style => 3,
        IssueCategory.Readability => 2,
        _ => 1
    };

    private sealed class ScoredIssue
    {
        public ScoredIssue(TextIssue issue, int sourceRank)
        {
            Issue = issue;
            SourceRank = sourceRank;
        }

        public TextIssue Issue { get; set; }
        public int SourceRank { get; }
    }
}

public sealed record WriteLiteIssueMergeResult(
    IReadOnlyList<TextIssue> Issues,
    int DuplicateCount,
    int ConflictCount,
    int SafeCount,
    TimeSpan Duration);
