namespace WriteLite.AI.Contracts;

/// <summary>Machine-stable issue type identifiers (schema v1).</summary>
public enum AiIssueType
{
    Spelling = 0,
    Typo = 1,
    Grammar = 2,
    Punctuation = 3,
    Capitalization = 4,
    Agreement = 5,
    WordForm = 6,
    WordOrder = 7,
    MissingWord = 8,
    ExtraWord = 9,
    Spacing = 10,
    Style = 11,
    Repetition = 12,
    Other = 14
}

public static class AiIssueTypeCatalog
{
    public static string ToWireId(AiIssueType type) => type switch
    {
        AiIssueType.Spelling => "spelling",
        AiIssueType.Typo => "typo",
        AiIssueType.Grammar => "grammar",
        AiIssueType.Punctuation => "punctuation",
        AiIssueType.Capitalization => "capitalization",
        AiIssueType.Agreement => "agreement",
        AiIssueType.WordForm => "word_form",
        AiIssueType.WordOrder => "word_order",
        AiIssueType.MissingWord => "missing_word",
        AiIssueType.ExtraWord => "extra_word",
        AiIssueType.Spacing => "spacing",
        AiIssueType.Style => "style",
        AiIssueType.Repetition => "repetition",
        _ => "other"
    };

    public static AiIssueType Parse(string? wire)
    {
        return (wire ?? "").Trim().ToLowerInvariant() switch
        {
            "spelling" or "orthography" => AiIssueType.Spelling,
            "typo" => AiIssueType.Typo,
            "grammar" => AiIssueType.Grammar,
            "punctuation" => AiIssueType.Punctuation,
            "capitalization" or "case" => AiIssueType.Capitalization,
            "agreement" => AiIssueType.Agreement,
            "word_form" or "wordform" => AiIssueType.WordForm,
            "word_order" or "wordorder" => AiIssueType.WordOrder,
            "missing_word" or "missing" => AiIssueType.MissingWord,
            "extra_word" or "extra" => AiIssueType.ExtraWord,
            "spacing" or "typography" or "whitespace" => AiIssueType.Spacing,
            "style" or "clarity" => AiIssueType.Style,
            "repetition" or "repeat" => AiIssueType.Repetition,
            _ => AiIssueType.Other
        };
    }

    public static string DisplayNameRu(AiIssueType type) => type switch
    {
        AiIssueType.Spelling => "Орфография",
        AiIssueType.Typo => "Опечатка",
        AiIssueType.Grammar => "Грамматика",
        AiIssueType.Punctuation => "Пунктуация",
        AiIssueType.Capitalization => "Регистр",
        AiIssueType.Agreement => "Согласование",
        AiIssueType.WordForm => "Форма слова",
        AiIssueType.WordOrder => "Порядок слов",
        AiIssueType.MissingWord => "Пропущенное слово",
        AiIssueType.ExtraWord => "Лишнее слово",
        AiIssueType.Spacing => "Пробелы",
        AiIssueType.Style => "Стиль",
        AiIssueType.Repetition => "Повтор",
        _ => "Другое"
    };

    public static string Description(AiIssueType type) => type switch
    {
        AiIssueType.Spelling => "Орфографическая ошибка.",
        AiIssueType.Typo => "Вероятная опечатка.",
        AiIssueType.Grammar => "Ошибка в грамматической конструкции.",
        AiIssueType.Punctuation => "Пропущенный, лишний или неверный знак препинания.",
        AiIssueType.Capitalization => "Ошибка регистра буквы.",
        AiIssueType.Agreement => "Ошибка согласования.",
        AiIssueType.WordForm => "Неверная форма слова.",
        AiIssueType.WordOrder => "Неверный порядок слов.",
        AiIssueType.MissingWord => "Вероятно, пропущено необходимое слово.",
        AiIssueType.ExtraWord => "Вероятно, лишнее слово.",
        AiIssueType.Spacing => "Ошибка в пробелах или типографике.",
        AiIssueType.Style => "Стилистическая рекомендация.",
        AiIssueType.Repetition => "Случайный повтор слова.",
        _ => "Неклассифицированная ошибка."
    };
}
