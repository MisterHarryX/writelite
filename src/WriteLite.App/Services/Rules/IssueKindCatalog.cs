using WriteLite.Models;
using WriteLite.Services.Settings;

namespace WriteLite.Services.Rules;

/// <summary>
/// A kind of finding the user can switch off on its own — «точка в конце предложения»,
/// «повторы слов» — without losing the whole category it belongs to.
/// </summary>
/// <param name="Id">Stable identifier stored in settings.</param>
/// <param name="Group">Section heading on the settings page.</param>
/// <param name="Title">What the switch is called.</param>
/// <param name="Description">One line saying what WriteLite stops marking when it is off.</param>
/// <param name="RulePrefixes">WriteLite rule ids (exact or <c>prefix*</c>) this kind covers.</param>
/// <param name="EngineRules">LanguageTool rule ids switched off at the source when this kind is off.</param>
/// <param name="Linguistic">Linguistic categories this kind also covers, whatever the rule id.</param>
public sealed record IssueKind(
    string Id,
    string Group,
    string Title,
    string Description,
    IReadOnlyList<string> RulePrefixes,
    IReadOnlyList<string> EngineRules,
    IReadOnlyList<LinguisticIssueCategory>? Linguistic = null)
{
    public bool Matches(TextIssue issue)
    {
        foreach (var pattern in RulePrefixes)
        {
            if (pattern.EndsWith('*'))
            {
                if (issue.RuleId.StartsWith(pattern[..^1], StringComparison.Ordinal)) return true;
            }
            else if (string.Equals(issue.RuleId, pattern, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return Linguistic is { Count: > 0 } && Linguistic.Contains(issue.LinguisticCategory);
    }
}

/// <summary>
/// Every kind the settings page offers, in the order it shows them.
/// </summary>
/// <remarks>
/// A kind is a user's idea of "this sort of remark", which is rarely one rule: «пробелы» is a
/// dozen spacing rules from three layers. Mapping by rule-id prefix keeps new rules in an
/// existing family covered without touching this file, and the engine ids are switched off in
/// the request itself because LanguageTool's findings reach WriteLite with hashed ids.
/// </remarks>
public static class IssueKindCatalog
{
    public const string GroupPunctuation = "Пунктуация";
    public const string GroupSpelling = "Орфография и регистр";
    public const string GroupStyle = "Стиль";

    public static IReadOnlyList<IssueKind> All { get; } =
    [
        new("terminal-mark", GroupPunctuation,
            "Точка в конце предложения",
            "Не считать ошибкой, если в конце текста или сообщения нет точки.",
            ["ru.punctuation.terminal*", "en.punctuation.terminal*"],
            ["PUNCTUATION_PARAGRAPH_END"]),

        new("intro-commas", GroupPunctuation,
            "Запятые при вводных словах",
            "«Кстати», «конечно», «к сожалению» и другие вводные слова.",
            ["ru.punctuation.intro-*"],
            []),

        new("vocative-commas", GroupPunctuation,
            "Запятые при обращениях",
            "«Привет, Маша», «Иван, зайди» — запятая перед и после имени.",
            ["ru.punctuation.vocative-comma", "ru.punctuation.greeting-sentence-boundary"],
            []),

        new("clause-commas", GroupPunctuation,
            "Запятые в сложных предложениях",
            "Перед «что», «чтобы», «но», «а», при деепричастных оборотах и однородных членах.",
            [
                "ru.punctuation.subordinate-*", "ru.punctuation.clause-*",
                "ru.punctuation.adversative-comma", "ru.punctuation.consequence-comma",
                "ru.punctuation.comma-before-chtoby", "ru.punctuation.indirect-question",
                "ru.punctuation.paired-conjunction-to", "ru.punctuation.homogeneous-comma",
                "ru.punctuation.gerund-phrase",
            ],
            []),

        new("model-commas", GroupPunctuation,
            "Запятые, предложенные нейросетью",
            "Подсказки обученной модели пунктуации там, где правила молчат.",
            ["ru.punctuation.model-comma"],
            []),

        new("spaces", GroupPunctuation,
            "Пробелы",
            "Двойные пробелы, пробел перед запятой, нет пробела после точки.",
            ["ru.spacing.*", "ru.punctuation.space-*", "ru.punctuation.*-space", "en.spacing.*"],
            ["WHITESPACE_RULE", "COMMA_PARENTHESIS_WHITESPACE"],
            [LinguisticIssueCategory.ExtraSpace, LinguisticIssueCategory.MissingSpace]),

        new("typography", GroupPunctuation,
            "Тире, многоточие и кавычки",
            "Дефис вместо тире, три точки вместо «…», повторённые знаки.",
            ["ru.typography.*", "ru.punctuation.repeated-mark", "en.typography.*"],
            ["DASH_RULE", "EN_QUOTES", "DOUBLE_PUNCTUATION", "RU_DASH_RULE", "MULTIPLICATION_SIGN"]),

        new("sentence-capital", GroupSpelling,
            "Заглавная буква в начале предложения",
            "Не исправлять «привет» на «Привет» в начале сообщения.",
            ["ru.orthography.sentence-capital", "en.orthography.sentence-capital"],
            ["UPPERCASE_SENTENCE_START"]),

        new("yo", GroupSpelling,
            "Буква «ё»",
            "Не предлагать «ё» вместо «е» («еще» → «ещё»).",
            ["personal.style.yo", "ru.spelling.yo*"],
            []),

        new("keyboard-layout", GroupSpelling,
            "Текст в неправильной раскладке",
            "«ghbdtn» → «привет», «руддщ» → «hello».",
            ["ru.spelling.keyboard-layout", "en.spelling.keyboard-layout"],
            []),

        new("unknown-words", GroupSpelling,
            "Незнакомые слова без вариантов",
            "Слова, которых нет в словаре и для которых нет исправления: имена, термины, сленг.",
            ["ru.spelling.unknown-soft", "en.spelling.unknown-soft"],
            []),

        new("repeats", GroupStyle,
            "Повторы слов",
            "Одно и то же слово рядом или в соседних предложениях.",
            ["ru.style.repeated-*", "ru.style.repetition-*", "en.style.repeated-*"],
            ["WORD_REPEAT_RULE", "RU_WORD_REPEAT_RULE", "ENGLISH_WORD_REPEAT_RULE", "ENGLISH_WORD_REPEAT_BEGINNING_RULE"],
            [LinguisticIssueCategory.RepeatedWord]),

        new("long-sentences", GroupStyle,
            "Длинные предложения",
            "Предложение, которое трудно прочитать с первого раза.",
            ["ru.readability.*", "en.readability.*"],
            ["TOO_LONG_SENTENCE"],
            [LinguisticIssueCategory.LongSentence]),

        new("bureaucratic", GroupStyle,
            "Канцелярит и лишние слова",
            "«Осуществлять оплату», «в связи с тем что», «памятный сувенир».",
            ["ru.style.bureaucratic-*", "ru.style.redundant-*", "ru.style.double-*"],
            []),

        new("fillers", GroupStyle,
            "Слова-паразиты",
            "«Как бы», «так сказать», «собственно».",
            ["ru.style.filler-*"],
            []),
    ];

    public static IssueKind? Find(string id) =>
        All.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.Ordinal));

    /// <summary>The kind a finding belongs to, or null when no switch covers it.</summary>
    public static IssueKind? KindOf(TextIssue issue)
    {
        foreach (var kind in All)
        {
            if (kind.Matches(issue)) return kind;
        }

        return null;
    }
}

/// <summary>
/// What the user asked WriteLite not to show, read on every published finding.
/// </summary>
/// <remarks>
/// <para>Static and swapped whole on every settings change, because the findings it judges come
/// from two analysis graphs and four lanes (see the two-analysis-paths note in the
/// architecture docs). Before this existed, the category switches lived only inside the
/// orchestrated analyzer, so the fast lane — the one that answers first, in the editor and in
/// other applications — showed punctuation findings with «Пунктуация» switched off.</para>
///
/// <para>The default allows everything, so tools and tests that never apply settings see the
/// engine unfiltered.</para>
/// </remarks>
public static class IssuePreferences
{
    private sealed record Snapshot(
        bool Orthography,
        bool Punctuation,
        bool Grammar,
        bool Style,
        bool Typography,
        bool Repeats,
        IReadOnlyList<IssueKind> DisabledKinds,
        IReadOnlyList<string> DisabledEngineRules);

    private static volatile Snapshot _current = new(true, true, true, true, true, true, [], []);

    /// <summary>Raised after settings change, so open views can re-filter what they show.</summary>
    public static event Action? Changed;

    public static void Apply(WriteLiteAppSettings settings)
    {
        WriteLite.Services.Spelling.EnglishSpelling.Enabled = settings.EnableEnglish;
        WriteLite.Services.Translation.LanguageSegmenter.EnglishEnabled = settings.EnableEnglish;
        _current = Build(settings);
        Changed?.Invoke();
    }

    /// <summary>The same judgement against a particular settings object (the orchestrated analyzer's own).</summary>
    public static bool Allows(TextIssue issue, WriteLiteAppSettings settings) => Allows(issue, Build(settings));

    private static Snapshot Build(WriteLiteAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var disabled = (settings.DisabledIssueKinds ?? [])
            .Select(IssueKindCatalog.Find)
            .Where(k => k is not null)
            .Select(k => k!)
            .ToList();

        return new Snapshot(
            settings.EnableOrthography,
            settings.EnablePunctuation,
            settings.EnableGrammar,
            settings.EnableStyle,
            settings.EnableTypography,
            settings.EnableRepeats,
            disabled,
            disabled.SelectMany(k => k.EngineRules).Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>LanguageTool rule ids to send as <c>disabledRules</c>.</summary>
    public static IReadOnlyList<string> DisabledEngineRules => _current.DisabledEngineRules;

    public static bool IsKindDisabled(string kindId) =>
        _current.DisabledKinds.Any(k => string.Equals(k.Id, kindId, StringComparison.Ordinal));

    public static bool Allows(TextIssue issue) => Allows(issue, _current);

    private static bool Allows(TextIssue issue, Snapshot s)
    {
        foreach (var kind in s.DisabledKinds)
        {
            if (kind.Matches(issue)) return false;
        }

        if (issue.LinguisticCategory == LinguisticIssueCategory.RepeatedWord)
        {
            return s.Repeats;
        }

        if (issue.Category == IssueCategory.Readability
            || issue.LinguisticCategory is LinguisticIssueCategory.ExtraSpace
                or LinguisticIssueCategory.MissingSpace)
        {
            return s.Typography || s.Style;
        }

        return issue.Category switch
        {
            IssueCategory.Orthography => s.Orthography,
            IssueCategory.Punctuation => s.Punctuation,
            IssueCategory.Grammar => s.Grammar,
            IssueCategory.Style => s.Style,
            _ => true,
        };
    }

    public static IReadOnlyList<TextIssue> Filter(IReadOnlyList<TextIssue> issues)
    {
        if (issues.Count == 0) return issues;
        List<TextIssue>? kept = null;
        for (var i = 0; i < issues.Count; i++)
        {
            if (Allows(issues[i]))
            {
                kept?.Add(issues[i]);
                continue;
            }

            kept ??= new List<TextIssue>(issues.Take(i));
        }

        return kept ?? issues;
    }
}
