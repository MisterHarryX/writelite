using WriteLite.Language.Russian;

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
        new("ru.spacing.repeated-space", RussianLanguageProfile.IsoCode, true),
        new("ru.punctuation.space-before-mark", RussianLanguageProfile.IsoCode, true),
        new("ru.punctuation.space-after-mark", RussianLanguageProfile.IsoCode, true),
        new("ru.punctuation.repeated-mark", RussianLanguageProfile.IsoCode, false),
        new("ru.typography.ellipsis", RussianLanguageProfile.IsoCode, false),
        new("ru.typography.double-hyphen", RussianLanguageProfile.IsoCode, false),
        new("ru.punctuation.open-paren-space", RussianLanguageProfile.IsoCode, true),
        new("ru.punctuation.close-paren-space", RussianLanguageProfile.IsoCode, true),
        new("ru.punctuation.open-quote-space", RussianLanguageProfile.IsoCode, true),
        new("ru.punctuation.close-quote-space", RussianLanguageProfile.IsoCode, true),
        new("ru.punctuation.abbreviation-td", RussianLanguageProfile.IsoCode, true),
        new("ru.punctuation.abbreviation-te", RussianLanguageProfile.IsoCode, true),
        new("ru.punctuation.terminal.insert", RussianLanguageProfile.IsoCode, false),
        new("ru.punctuation.comma-before-chtoby", RussianLanguageProfile.IsoCode, true),
        new("ru.punctuation.intro-comma.info", RussianLanguageProfile.IsoCode, false),
        new("ru.punctuation.subordinate-comma", RussianLanguageProfile.IsoCode, false),
        new("ru.punctuation.subordinate-comma.info", RussianLanguageProfile.IsoCode, false),

        // Phase 5. Both carry a replacement and both auto-apply, which the older comma
        // heuristics deliberately do not: their conditions are closed lists rather than
        // guesses about sentence structure. See docs/language-engine-phase5-report.md §5.
        new("ru.punctuation.intro-word-comma", RussianLanguageProfile.IsoCode, true),
        new("ru.punctuation.adversative-comma", RussianLanguageProfile.IsoCode, true)
    ];

    public static int CountRu => All.Count;
    [Obsolete("WriteLite ships a Russian-only punctuation pack.")]
    public static int CountEn => 0;
}
