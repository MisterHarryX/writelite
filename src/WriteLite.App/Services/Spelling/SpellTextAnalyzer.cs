using System.Text.RegularExpressions;
using WriteLite.Language.Core;
using WriteLite.Models;

namespace WriteLite.Services.Spelling;

public sealed partial class SpellTextAnalyzer(ISpellChecker spellChecker) : ITextAnalyzer
{
    private static readonly UserDictionaryService DefaultUserDictionary = UserDictionaryService.LoadDefault();

    /// <summary>
    /// When false (default), unknown words without strong suggestions are not underlined.
    /// </summary>
    public bool FlagUnknownWordsWithoutSuggestions { get; set; }

    public IReadOnlyList<TextIssue> Analyze(string text)
    {
        return AnalyzeCore(text, (word, language, cancellationToken) =>
            spellChecker.CheckWord(word, language, cancellationToken), CancellationToken.None);
    }

    public Task<IReadOnlyList<TextIssue>> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(AnalyzeCore(text, (word, language, token) =>
            spellChecker.CheckWord(word, language, token), cancellationToken));
    }

    private IReadOnlyList<TextIssue> AnalyzeCore(
        string text,
        Func<string, SpellingLanguage, CancellationToken, SpellCheckResult> checkWord,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var protectedSpans = ProtectedTextSpans.Find(text);
        var issues = new List<TextIssue>();

        foreach (Match match in WordRegex().Matches(text))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ProtectedTextSpans.Overlaps(match.Index, match.Length, protectedSpans)
                || ProtectedTextSpans.IsTechnicalToken(match.Value)
                || ShouldSkipToken(match.Value))
            {
                continue;
            }

            var language = DetectLanguage(match.Value);
            if (language is null) continue;
            if (DefaultUserDictionary.Contains(match.Value)
                || DefaultUserDictionary.Contains(CorrectionCandidateValidityPolicy.FoldSpelling(match.Value)))
            {
                continue;
            }

            var result = checkWord(match.Value, language.Value, cancellationToken);
            if (result.IsKnown) continue;

            var suggestions = CorrectionCandidateValidityPolicy.FilterSuggestions(
                match.Value,
                result.Suggestions,
                max: 5);

            if (suggestions.Count == 0)
            {
                // Missing from lexical pack / dictionary alone is NOT an orthography error.
                if (!FlagUnknownWordsWithoutSuggestions) continue;

                issues.Add(new TextIssue(
                    match.Index,
                    match.Length,
                    match.Value,
                    null,
                    "Возможно, неизвестное слово",
                    "Слово не найдено в орфографическом словаре. Это не доказывает ошибку. Можно добавить его в пользовательский словарь.",
                    IssueCategory.Orthography,
                    IssueSeverity.Suggestion,
                    CanApplyAutomatically: false,
                    RuleId: "ru.spelling.unknown-soft",
                    LinguisticCategory: LinguisticIssueCategory.UnknownWord,
                    Confidence: 0.2));
                continue;
            }

            var best = suggestions[0];
            var isKeyboardLayout = match.Value.All(IsEnglishLetter) && best.Any(IsRussianLetter);
            if (CorrectionCandidateValidityPolicy.IsIdenticalCorrection(match.Value, best))
            {
                continue;
            }

            if (!CorrectionCandidateValidityPolicy.WouldChangeText(text, match.Index, match.Length, best))
            {
                continue;
            }

            issues.Add(new TextIssue(
                match.Index,
                match.Length,
                match.Value,
                best,
                isKeyboardLayout ? "Возможно, выбрана латинская раскладка" : "Возможная орфографическая ошибка",
                isKeyboardLayout
                    ? "Латинские клавиши образуют известное русское слово. Проверьте вариант перед применением."
                    : "Слово не найдено в русском орфографическом словаре. Проверьте предложенный вариант.",
                IssueCategory.Orthography,
                IssueSeverity.Warning,
                CanApplyAutomatically: false,
                RuleId: isKeyboardLayout ? "ru.spelling.keyboard-layout" : "ru.spelling.typo",
                LinguisticCategory: LinguisticIssueCategory.Typo,
                Confidence: isKeyboardLayout ? 0.8 : 0.75));
        }

        return issues;
    }

    public static bool IsRussianLetter(char value)
        => (value >= 'а' && value <= 'я') || (value >= 'А' && value <= 'Я') || value is 'ё' or 'Ё';

    public static bool IsEnglishLetter(char value)
        => (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z');

    private static SpellingLanguage? DetectLanguage(string value)
    {
        var hasRussian = false;
        var hasEnglish = false;
        foreach (var character in value)
        {
            hasRussian |= IsRussianLetter(character);
            hasEnglish |= IsEnglishLetter(character);
        }

        return (hasRussian, hasEnglish) switch
        {
            (true, false) => SpellingLanguage.Russian,
            // Latin is ignored as a language. It is routed through the Russian
            // checker only for the bounded Latin-keyboard -> Cyrillic heuristic.
            (false, true) => SpellingLanguage.Russian,
            _ => null
        };
    }

    private static bool ShouldSkipToken(string value)
    {
        if (value.Length <= 1) return true;
        if (value.Any(char.IsDigit)) return true;
        if (value.Contains('_')) return true;
        if (value.Any(character => character is '/' or '\\' or '@' or ':')) return true;
        return false;
    }

    [GeneratedRegex(@"[\p{L}_][\p{L}\p{N}_]*", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
