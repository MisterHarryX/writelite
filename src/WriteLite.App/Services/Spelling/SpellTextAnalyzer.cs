using System.Text.RegularExpressions;
using WriteLite.Language.Core;
using WriteLite.Models;
using WriteLite.Services.LanguageEngine;

namespace WriteLite.Services.Spelling;

public sealed partial class SpellTextAnalyzer(ISpellChecker spellChecker) : ITextAnalyzer
{
    private static readonly UserDictionaryService DefaultUserDictionary = UserDictionaryService.LoadDefault();

    /// <summary>
    /// The shared contextual pass. Null when no reranker is deployed, in which case
    /// every path below degrades to the lexical behaviour it had before.
    /// </summary>
    /// <remarks>
    /// Loaded once per process and shared: the ONNX graph is 29 MB and stateless, so a
    /// second copy would cost memory and buy nothing.
    /// </remarks>
    private static readonly Lazy<ContextualCorrectionRefiner?> SharedRefiner =
        new(() => ContextualCorrectionRefiner.TryLoad(), LazyThreadSafetyMode.ExecutionAndPublication);

    private ContextualCorrectionRefiner? _refiner = SharedRefiner.Value;

    /// <summary>
    /// When false (default), unknown words without strong suggestions are not underlined.
    /// </summary>
    public bool FlagUnknownWordsWithoutSuggestions { get; set; }

    /// <summary>
    /// The context-aware pass over each sentence. On by default when a reranker is
    /// deployed; set false to measure the lexical layer in isolation.
    /// </summary>
    public bool ContextualRefinementEnabled
    {
        get => _refiner is not null;
        set => _refiner = value ? SharedRefiner.Value : null;
    }

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

            // The checker has already judged which of these split the token and which of
            // those are real words; re-running that gate here without its lexicon would
            // silently drop «потомучто» → «потому что».
            var suggestions = CorrectionCandidateValidityPolicy.FilterSuggestions(
                match.Value,
                result.Suggestions,
                max: 5,
                splitsAlreadyJudged: true);

            // The lexicon ranks by edit distance and frequency; only the sentence can
            // say which candidate belongs in it. Returns the input untouched unless the
            // model separates the top two clearly.
            //
            // Not consulted when the candidates are not all in one script. The reranker is a
            // Russian acceptability model: asked to choose between «пощаду» and "google" it
            // prefers the Russian string every time, for the same reason it would prefer it
            // over any English word — which silently undid Latin-target layout recovery on
            // every sentence. Where the scripts differ the question is which language was
            // meant, and that is the layout evidence's to answer, not this model's.
            if (_refiner is not null && suggestions.Count > 1 && !MixesScripts(suggestions))
            {
                suggestions = _refiner.Rerank(text, match.Index, match.Length, suggestions, cancellationToken);
            }

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

            // Both layout directions. The Latin-target one — Cyrillic keystrokes that were
            // meant to be an English word, «пшерги» for "github" — was unreachable before
            // Phase 5 and produced an unrelated Russian word instead.
            var latinTyped = match.Value.All(IsEnglishLetter) && best.Any(IsRussianLetter);
            var russianTyped = match.Value.All(IsRussianLetter) && best.Any(IsEnglishLetter);
            var isKeyboardLayout = latinTyped || russianTyped;
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
                isKeyboardLayout ? "Возможно, выбрана не та раскладка" : "Возможная орфографическая ошибка",
                latinTyped
                    ? "Латинские клавиши образуют известное русское слово. Проверьте вариант перед применением."
                    : russianTyped
                        ? "Русские клавиши образуют известное английское слово. Проверьте вариант перед применением."
                        : "Слово не найдено в русском орфографическом словаре. Проверьте предложенный вариант.",
                IssueCategory.Orthography,
                IssueSeverity.Warning,
                CanApplyAutomatically: false,
                RuleId: isKeyboardLayout ? "ru.spelling.keyboard-layout" : "ru.spelling.typo",
                LinguisticCategory: LinguisticIssueCategory.Typo,
                Confidence: isKeyboardLayout ? 0.8 : 0.75));
        }

        AddRealWordFindings(text, issues, protectedSpans, cancellationToken);
        return issues;
    }

    /// <summary>
    /// Words that are spelled correctly but do not belong in this sentence —
    /// "кампания"/"компания", "будит"/"будет". Nothing upstream can see these, because
    /// every one of them passes a dictionary lookup.
    /// </summary>
    /// <remarks>
    /// Reported as a suggestion and never auto-applicable, regardless of how confident
    /// the model is. The evidence is a 29 M-parameter acceptability score, not a rule,
    /// and quietly rewriting a word the user spelled correctly is the most damaging
    /// thing this application can do. The refiner's own margin gate has already rejected
    /// everything it was not clearly sure about before we get here.
    /// </remarks>
    private void AddRealWordFindings(
        string text,
        List<TextIssue> issues,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        CancellationToken cancellationToken)
    {
        if (_refiner is null) return;

        List<ContextualCorrectionRefiner.ContextualFinding> findings = [];
        try
        {
            // Sentence at a time, with offsets mapped back.
            //
            // The reranker's encoder has a 64-token window, and this used to be handed the
            // entire analysed text. Past roughly the first forty words the model was being
            // asked about a token that had already been truncated away, so real-word
            // detection silently stopped working for anything longer than a short note —
            // and scored the wrong sentence for everything else. Feeding it one sentence
            // means the candidate is always inside the window, and it makes the cost of a
            // hundred-page document the same per sentence as a one-line one.
            foreach (var (start, length) in SentenceWindowBuilder.Split(text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (length < 2) continue;

                var sentence = text.Substring(start, length);
                foreach (var finding in _refiner.FindRealWordErrors(sentence, cancellationToken))
                {
                    findings.Add(finding with { Start = finding.Start + start });
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A contextual pass is an enhancement. If the model misbehaves the user still
            // gets every lexical result rather than an empty panel.
            CompatibilityLogger.Technical("contextual-realword-failed", $"type={ex.GetType().Name}");
            return;
        }

        foreach (var finding in findings)
        {
            if (ProtectedTextSpans.Overlaps(finding.Start, finding.Length, protectedSpans)) continue;
            if (DefaultUserDictionary.Contains(finding.Original)) continue;

            // The lexical pass owns any span it already flagged: it has harder evidence.
            var end = finding.Start + finding.Length;
            if (issues.Any(i => i.Start < end && finding.Start < i.Start + i.Length)) continue;

            issues.Add(new TextIssue(
                finding.Start,
                finding.Length,
                finding.Original,
                finding.Replacement,
                "Возможно, перепутано слово",
                $"«{finding.Original}» написано без ошибок, но в этом предложении обычно уместно "
                + $"«{finding.Replacement}». Проверьте, какое слово вы имели в виду.",
                IssueCategory.Grammar,
                IssueSeverity.Suggestion,
                CanApplyAutomatically: false,
                RuleId: "ru.context.real-word",
                LinguisticCategory: LinguisticIssueCategory.EndingError,
                Confidence: Math.Clamp(finding.Confidence, 0.0, 1.0)));
        }
    }

    /// <summary>
    /// True when the candidate list spans both scripts, so a Russian-only ranker cannot
    /// meaningfully order it.
    /// </summary>
    private static bool MixesScripts(IReadOnlyList<string> suggestions)
    {
        var russian = false;
        var latin = false;
        foreach (var suggestion in suggestions)
        {
            foreach (var character in suggestion)
            {
                russian |= IsRussianLetter(character);
                latin |= IsEnglishLetter(character);
            }
        }

        return russian && latin;
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
            // A word mixing both scripts used to be dropped as "no language",
            // which silently exempted exactly the tokens most likely to be
            // wrong: "привeт" with a Latin e looks perfect and fails every
            // lookup. The Russian checker recognises the homoglyph pattern and
            // rewrites it, so these have to reach it.
            (true, true) => SpellingLanguage.Russian,
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
