namespace WriteLite.Language.Punctuation;

/// <summary>
/// Stable Russian punctuation IDs exposed to language-pack diagnostics.
/// The canonical metadata and implementations live in resources/rules/ru/*.json.
/// </summary>
public static class PunctuationRuleCatalog
{
    public sealed record RuleMeta(string RuleId, string Language, bool SafeToApply);

    public static IReadOnlyList<RuleMeta> All { get; } =
    [
        new("ru.spacing.repeated-space", "ru", true),
        new("ru.punctuation.space-before-mark", "ru", true),
        new("ru.punctuation.space-after-mark", "ru", true),
        new("ru.punctuation.repeated-mark", "ru", false),
        new("ru.typography.ellipsis", "ru", false),
        new("ru.typography.double-hyphen", "ru", false),
        new("ru.punctuation.open-paren-space", "ru", true),
        new("ru.punctuation.close-paren-space", "ru", true),
        new("ru.punctuation.open-quote-space", "ru", true),
        new("ru.punctuation.close-quote-space", "ru", true),
        new("ru.punctuation.abbreviation-td", "ru", true),
        new("ru.punctuation.abbreviation-te", "ru", true),
        new("ru.punctuation.terminal.insert", "ru", false),
        new("ru.punctuation.comma-before-chtoby", "ru", true),
        new("ru.punctuation.intro-comma.info", "ru", false),
        new("ru.punctuation.subordinate-comma", "ru", false),
        new("ru.punctuation.subordinate-comma.info", "ru", false),

        // Phase 5. Both carry a replacement and both auto-apply, which the older comma
        // heuristics deliberately do not: their conditions are closed lists rather than
        // guesses about sentence structure. See docs/language-engine-phase5-report.md §5.
        new("ru.punctuation.intro-word-comma", "ru", true),
        new("ru.punctuation.adversative-comma", "ru", true)
    ];

    public static int CountRu => All.Count;
    [Obsolete("WriteLite ships a Russian-only punctuation pack.")]
    public static int CountEn => 0;
}
