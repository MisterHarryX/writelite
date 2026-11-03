using WriteLite.Models;

namespace WriteLite.Services.Rules;

/// <summary>One rule as the reader-facing reference presents it.</summary>
public sealed record RuleReferenceEntry(
    string RuleId,
    string Section,
    string Title,
    string Summary,
    string Explanation,
    string? BadExample,
    string? GoodExample,
    IReadOnlyList<(string Bad, string Good)> Examples,
    string Source);

/// <summary>A titled group of rules in reading order.</summary>
public sealed record RuleReferenceSection(string Title, IReadOnlyList<RuleReferenceEntry> Entries);

/// <summary>
/// The rules reference: what each check means, why, with examples and a source.
/// </summary>
/// <remarks>
/// <para>Built from the shipped rule pack rather than written separately, so the reference
/// and the checker cannot drift: every finding names a rule id, and the same id is the key
/// here. A correction card asks <see cref="Find"/> for the rule behind its finding and shows
/// the short form; the reference page shows every entry in full.</para>
///
/// <para>Entries whose rule is a soft hint rather than a correction — «Возможна граница
/// придаточной части», «Перегруженное предложение» — are kept: a reader who was shown the
/// hint should be able to read what it means.</para>
/// </remarks>
public sealed class RuleReference
{
    private static readonly Lazy<RuleReference> Shipped = new(() => FromCatalog(RuleCatalog.LoadDefault()));

    private readonly Dictionary<string, RuleReferenceEntry> _byId;

    private RuleReference(IReadOnlyList<RuleReferenceSection> sections)
    {
        Sections = sections;
        _byId = sections
            .SelectMany(s => s.Entries)
            .GroupBy(e => e.RuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The reference built from the rule pack the application ships with.</summary>
    public static RuleReference Default => Shipped.Value;

    public IReadOnlyList<RuleReferenceSection> Sections { get; }

    public int Count => _byId.Count;

    /// <summary>The rule behind a finding, or null when the finding has no reference entry.</summary>
    public RuleReferenceEntry? Find(string? ruleId)
        => !string.IsNullOrWhiteSpace(ruleId) && _byId.TryGetValue(ruleId, out var entry) ? entry : null;

    /// <summary>Entries whose title, summary, explanation or examples contain <paramref name="query"/>.</summary>
    public IReadOnlyList<RuleReferenceSection> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Sections;

        var needle = query.Trim();
        bool Matches(RuleReferenceEntry e) =>
            Contains(e.Title, needle) || Contains(e.Summary, needle) || Contains(e.Explanation, needle)
            || e.Examples.Any(x => Contains(x.Bad, needle) || Contains(x.Good, needle));

        return Sections
            .Select(s => new RuleReferenceSection(s.Title, s.Entries.Where(Matches).ToArray()))
            .Where(s => s.Entries.Count > 0)
            .ToArray();

        static bool Contains(string text, string part)
            => text.Contains(part, StringComparison.CurrentCultureIgnoreCase)
               || text.Replace('ё', 'е').Contains(part.Replace('ё', 'е'), StringComparison.CurrentCultureIgnoreCase);
    }

    public static RuleReference FromCatalog(RuleCatalog catalog)
    {
        var sections = catalog.Rules
            .Where(r => !string.IsNullOrWhiteSpace(r.Title))
            .Select(ToEntry)
            .GroupBy(e => e.Section)
            .OrderBy(g => SectionOrder.IndexOf(g.Key) is var i && i >= 0 ? i : int.MaxValue)
            .Select(g => new RuleReferenceSection(
                g.Key,
                g.OrderBy(e => e.Title, StringComparer.CurrentCulture).ToArray()))
            .ToArray();
        return new RuleReference(sections);
    }

    private static RuleReferenceEntry ToEntry(RuleDefinition rule)
    {
        var examples = rule.BadExamples
            .Select((bad, i) => (Bad: bad, Good: i < rule.GoodExamples.Count ? rule.GoodExamples[i] : string.Empty))
            .Where(x => !string.IsNullOrWhiteSpace(x.Bad) && !string.IsNullOrWhiteSpace(x.Good))
            .ToArray();

        return new RuleReferenceEntry(
            rule.RuleId,
            SectionOf(rule),
            rule.Title.Trim(),
            rule.ShortMessage.Trim(),
            string.IsNullOrWhiteSpace(rule.DetailedExplanation) ? rule.ShortMessage.Trim() : rule.DetailedExplanation.Trim(),
            examples.Length > 0 ? examples[0].Bad : null,
            examples.Length > 0 ? examples[0].Good : null,
            examples,
            rule.Source.Trim());
    }

    private const string Spelling = "Орфография";
    private const string Punctuation = "Пунктуация";
    private const string Grammar = "Грамматика";
    private const string Typography = "Пробелы и типографика";
    private const string Style = "Стиль";
    private const string Words = "Трудные слова";

    /// <summary>
    /// Rules a reader learns from first, then the per-word list. The word list is a hundred
    /// entries of the «агенство → агентство» kind; placed first it buried the twenty rules
    /// that explain how Russian works.
    /// </summary>
    private static readonly List<string> SectionOrder = [Grammar, Punctuation, Spelling, Typography, Style, Words];

    /// <summary>
    /// The section a rule is read under, from its id first: a spacing rule is filed as
    /// punctuation by the checker, but a reader looks for it under typography.
    /// </summary>
    private static string SectionOf(RuleDefinition rule)
    {
        var id = rule.RuleId;
        if (id.StartsWith("ru.spacing.", StringComparison.Ordinal)
            || id.StartsWith("ru.typography.", StringComparison.Ordinal)
            || (id.StartsWith("ru.punctuation.", StringComparison.Ordinal) && id.Contains("space", StringComparison.Ordinal)))
        {
            return Typography;
        }

        if (id.StartsWith("ru.style.", StringComparison.Ordinal) || id.StartsWith("ru.readability.", StringComparison.Ordinal))
            return Style;
        if (id.StartsWith("ru.punctuation.", StringComparison.Ordinal)) return Punctuation;

        // One misspelt word and its correct form: «агенство» — ошибочное написание…
        if (id.StartsWith("ru.spelling.", StringComparison.Ordinal) && rule.Title.TrimStart().StartsWith('«'))
            return Words;

        if (id.StartsWith("ru.grammar.", StringComparison.Ordinal)) return Grammar;
        if (id.StartsWith("ru.spelling.", StringComparison.Ordinal) || id.StartsWith("ru.orthography.", StringComparison.Ordinal))
            return Spelling;

        return rule.IssueCategory switch
        {
            IssueCategory.Punctuation => Punctuation,
            IssueCategory.Grammar => Grammar,
            IssueCategory.Style or IssueCategory.Readability => Style,
            _ => Spelling,
        };
    }
}
