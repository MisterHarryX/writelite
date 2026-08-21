using WriteLite.Models;

namespace LiveTest;

/// <summary>
/// The §52 scorecard: what was expected, what was found, what was missed, and — the row that
/// can fail the release on its own — what was changed dangerously.
/// </summary>
/// <remarks>
/// <para>Scoring is deliberately lenient about <em>how</em> a finding is expressed and strict
/// about <em>what</em> it claims. An expected issue counts as detected when some finding
/// overlaps its span; it counts as <b>fixed</b> only when the finding also offers one of the
/// accepted replacements. That separation is what distinguishes "WriteLite noticed but could
/// not help" from "WriteLite did not notice", which are different product problems with
/// different fixes.</para>
///
/// <para>Style is scored on detection only. §35: acceptable alternatives are non-unique and
/// exact rewrite equality is the wrong metric for a suggestion the user is free to ignore.</para>
///
/// <para>False positives are counted on the <c>clean</c> category only. On a paragraph that is
/// deliberately full of errors, a finding outside the expected spans is very often a real
/// error the gold labels did not enumerate, and counting it against the engine would punish
/// it for being better than the annotation.</para>
/// </remarks>
internal sealed class ScoreCard
{
    private readonly List<ItemResult> _results = [];

    public bool HasDangerousChanges => _results.Any(r => r.Dangerous.Count > 0);

    public void Add(Item item, IReadOnlyList<TextIssue> findings)
    {
        var result = new ItemResult { Item = item };

        foreach (var expected in item.Expected)
        {
            var start = item.Text.IndexOf(expected.Original, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Malformed.Add($"gold span not present in text: «{expected.Original}»");
                continue;
            }

            var end = start + expected.Original.Length;
            var overlapping = findings
                .Where(f => f.Start < end && start < f.Start + Math.Max(f.Length, 1))
                .ToList();

            if (overlapping.Count == 0)
            {
                result.Missed.Add(expected);
                continue;
            }

            var isStyle = expected.Category.Equals("style", StringComparison.OrdinalIgnoreCase);
            var fixedProperly = isStyle
                || overlapping.Any(f => Accepts(item.Text, f, expected));

            if (fixedProperly) result.Fixed.Add((expected, overlapping[0]));
            else result.DetectedNotFixed.Add((expected, overlapping[0]));
        }

        // §38: a forbidden rewrite is a release blocker wherever it appears.
        foreach (var forbidden in item.Forbidden)
        {
            foreach (var finding in findings)
            {
                if (string.IsNullOrEmpty(finding.Replacement)) continue;
                if (!finding.Original.Contains(forbidden.Original, StringComparison.OrdinalIgnoreCase)) continue;
                if (!finding.Replacement.Contains(forbidden.Replacement, StringComparison.OrdinalIgnoreCase)) continue;
                result.Dangerous.Add((forbidden, finding));
            }
        }

        result.All = findings;
        _results.Add(result);
    }

    /// <summary>Whether applying this finding yields one of the accepted texts.</summary>
    private static bool Accepts(string text, TextIssue finding, Expected expected)
    {
        if (string.IsNullOrEmpty(finding.Replacement) && finding.Length > 0) return false;

        var applied = text[..finding.Start] + (finding.Replacement ?? string.Empty)
                      + text[(finding.Start + finding.Length)..];

        return expected.Replacements.Any(accepted =>
            applied.Contains(accepted, StringComparison.Ordinal));
    }

    public void Print()
    {
        var expectedTotal = _results.Sum(r => r.Item.Expected.Count);
        var fixedTotal = _results.Sum(r => r.Fixed.Count);
        var detectedTotal = fixedTotal + _results.Sum(r => r.DetectedNotFixed.Count);
        var missedTotal = _results.Sum(r => r.Missed.Count);
        var dangerousTotal = _results.Sum(r => r.Dangerous.Count);

        foreach (var r in _results)
        {
            Console.WriteLine($"── {r.Item.Id}  [{r.Item.Category}]");
            Console.WriteLine($"   expected {r.Item.Expected.Count}  fixed {r.Fixed.Count}  "
                + $"detected-only {r.DetectedNotFixed.Count}  missed {r.Missed.Count}  "
                + $"findings {r.All.Count}");

            foreach (var (expected, finding) in r.Fixed)
            {
                Console.WriteLine($"   [FIXED ] «{Short(expected.Original)}» → «{Short(finding.Replacement ?? "")}»  {finding.RuleId}");
            }

            foreach (var (expected, finding) in r.DetectedNotFixed)
            {
                Console.WriteLine($"   [SEEN  ] «{Short(expected.Original)}» flagged by {finding.RuleId} "
                    + $"but offered «{Short(finding.Replacement ?? "(nothing)")}»");
            }

            foreach (var expected in r.Missed)
            {
                Console.WriteLine($"   [MISS  ] «{Short(expected.Original)}» → «{Short(expected.Replacements.FirstOrDefault() ?? "")}»  ({expected.Category})");
            }

            foreach (var (forbidden, finding) in r.Dangerous)
            {
                Console.WriteLine($"   [DANGER] «{finding.Original}» → «{finding.Replacement}»  {finding.RuleId} — {forbidden.Reason}");
            }

            foreach (var note in r.Malformed) Console.WriteLine($"   [GOLD? ] {note}");
            Console.WriteLine();
        }

        Console.WriteLine("═══ scorecard ═══");
        Console.WriteLine($"expected issues        : {expectedTotal}");
        Console.WriteLine($"detected               : {detectedTotal}  ({Rate(detectedTotal, expectedTotal)})");
        Console.WriteLine($"correctly fixed        : {fixedTotal}  ({Rate(fixedTotal, expectedTotal)})");
        Console.WriteLine($"detected but no fix    : {detectedTotal - fixedTotal}");
        Console.WriteLine($"missed                 : {missedTotal}  ({Rate(missedTotal, expectedTotal)})");
        Console.WriteLine($"dangerous changes      : {dangerousTotal}{(dangerousTotal > 0 ? "   ← RELEASE BLOCKER" : "")}");
        Console.WriteLine();

        Console.WriteLine("by category:");
        var byCategory = _results
            .SelectMany(r => r.Item.Expected.Select(e => (e, r)))
            .GroupBy(x => x.e.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in byCategory)
        {
            var total = group.Count();
            var got = group.Count(x => x.r.Fixed.Any(f => ReferenceEquals(f.Expected, x.e)));
            var seen = group.Count(x => x.r.DetectedNotFixed.Any(f => ReferenceEquals(f.Expected, x.e)));
            Console.WriteLine($"  {group.Key,-14} fixed {got,3}/{total,-3}  seen-only {seen,3}  missed {total - got - seen,3}");
        }

        Console.WriteLine();
        Console.WriteLine("clean-prose false positives:");
        foreach (var r in _results.Where(r => r.Item.Category.Equals("clean", StringComparison.OrdinalIgnoreCase)))
        {
            var words = System.Text.RegularExpressions.Regex.Matches(r.Item.Text, @"\p{L}+").Count;
            Console.WriteLine($"  {r.Item.Id,-24} {r.All.Count} findings on {words} words");
            foreach (var f in r.All)
            {
                Console.WriteLine($"      {f.RuleId,-42} «{Short(f.Original)}» → «{Short(f.Replacement ?? "")}»");
            }
        }
    }

    private static string Rate(int part, int whole) => whole == 0 ? "n/a" : $"{(double)part / whole:P1}";

    private static string Short(string value)
    {
        var normalized = value.Replace("\n", "⏎").Replace("\r", "");
        return normalized.Length <= 46 ? normalized : normalized[..46] + "…";
    }

    public object ToJson() => new
    {
        ExpectedIssues = _results.Sum(r => r.Item.Expected.Count),
        Fixed = _results.Sum(r => r.Fixed.Count),
        DetectedNotFixed = _results.Sum(r => r.DetectedNotFixed.Count),
        Missed = _results.Sum(r => r.Missed.Count),
        Dangerous = _results.Sum(r => r.Dangerous.Count),
        ByCategory = _results
            .SelectMany(r => r.Item.Expected.Select(e => (e, r)))
            .GroupBy(x => x.e.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Total = g.Count(),
                    Fixed = g.Count(x => x.r.Fixed.Any(f => ReferenceEquals(f.Expected, x.e))),
                    Seen = g.Count(x => x.r.DetectedNotFixed.Any(f => ReferenceEquals(f.Expected, x.e))),
                }),
        Items = _results.Select(r => new
        {
            r.Item.Id,
            r.Item.Category,
            Findings = r.All.Count,
            Fixed = r.Fixed.Select(f => new { f.Expected.Original, f.Finding.Replacement, f.Finding.RuleId }),
            Missed = r.Missed.Select(m => new { m.Original, m.Category, Want = m.Replacements.FirstOrDefault() }),
            Dangerous = r.Dangerous.Select(d => new { d.Finding.Original, d.Finding.Replacement, d.Finding.RuleId }),
        }),
    };

    private sealed class ItemResult
    {
        public required Item Item { get; init; }
        public IReadOnlyList<TextIssue> All { get; set; } = [];
        public List<(Expected Expected, TextIssue Finding)> Fixed { get; } = [];
        public List<(Expected Expected, TextIssue Finding)> DetectedNotFixed { get; } = [];
        public List<Expected> Missed { get; } = [];
        public List<(Forbidden Forbidden, TextIssue Finding)> Dangerous { get; } = [];
        public List<string> Malformed { get; } = [];
    }
}
