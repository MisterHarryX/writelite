using WriteLite.Models;

namespace WriteLite.Services.Spelling;

/// <summary>
/// English spelling for English sentences, on the bundled en_US Hunspell dictionary.
/// </summary>
/// <remarks>
/// <para>The fast lane's half of English support: underlines appear as soon as the word is typed,
/// before the language engine (which checks English grammar and has a context-aware speller of
/// its own) answers. Where both find the same word, the card keeps the engine's suggestion — see
/// <c>IssueDeduplicationService.OneSpellingCardPerWord</c>.</para>
///
/// <para>Two things are deliberately not flagged: capitalised words inside a sentence (names,
/// products — «Dmitry», «Figma») and words in capitals («NASA»). A Russian word typed on the
/// English layout («ghbdtn») is still offered as a layout fix, because the Russian checker is
/// consulted first for every word the English dictionary does not know.</para>
/// </remarks>
public static class EnglishSpelling
{
    private static readonly Lazy<HunspellSpellingLexicon> Lexicon =
        new(() => HunspellSpellingLexicon.LoadEnglish(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Settings switch («Проверять английский»).</summary>
    public static bool Enabled { get; set; } = true;

    public static bool IsKnown(string word)
    {
        var lexicon = Lexicon.Value;
        if (!lexicon.IsReady) return true;
        return lexicon.ContainsExact(word) || lexicon.ContainsExact(word.ToLowerInvariant());
    }

    public static TextIssue? TryCreateIssue(
        string text,
        int start,
        string word,
        Func<string, SpellingLanguage, CancellationToken, SpellCheckResult> russianCheck,
        CancellationToken cancellationToken)
    {
        if (word.Length < 2) return null;
        if (word.All(char.IsUpper)) return null;
        // Capitalised words are names more often than typos («Dmitry», «Figma»); the language
        // engine still checks them with its own, context-aware speller.
        if (char.IsUpper(word[0])) return null;
        if (IsKnown(word)) return null;

        // «ghbdtn» in an English sentence is still Russian on the wrong layout.
        var russian = russianCheck(word, SpellingLanguage.Russian, cancellationToken);
        var layout = russian.Suggestions.FirstOrDefault(s => s.Any(SpellTextAnalyzer.IsRussianLetter));
        if (layout is not null)
        {
            return new TextIssue(
                start, word.Length, word, layout,
                "Возможно, выбрана не та раскладка",
                "Латинские клавиши образуют известное русское слово. Проверьте вариант перед применением.",
                IssueCategory.Orthography, IssueSeverity.Warning,
                CanApplyAutomatically: false,
                RuleId: "ru.spelling.keyboard-layout",
                LinguisticCategory: LinguisticIssueCategory.Typo,
                Confidence: 0.8);
        }

        // Hunspell also proposes splits («me-sage», «mes age»); a typo inside one word is fixed
        // inside one word.
        var suggestions = Lexicon.Value.Suggest(word, 6)
            .Where(s => !s.Contains(' ') && !s.Contains('-'))
            .Take(3)
            .ToList();
        if (suggestions.Count == 0) return null;
        var best = char.IsUpper(word[0]) && suggestions[0].Length > 0
            ? char.ToUpperInvariant(suggestions[0][0]) + suggestions[0][1..]
            : suggestions[0];
        if (string.Equals(best, word, StringComparison.Ordinal)) return null;

        var others = suggestions.Skip(1).Where(s => !string.Equals(s, best, StringComparison.OrdinalIgnoreCase)).ToList();
        var explanation = $"Слова «{word}» нет в английском словаре. Скорее всего, имелось в виду «{best}»."
                          + (others.Count > 0 ? $" Другие варианты: {string.Join(", ", others.Select(o => $"«{o}»"))}." : string.Empty);
        return new TextIssue(
            start, word.Length, word, best,
            "Опечатка в английском",
            explanation,
            IssueCategory.Orthography, IssueSeverity.Warning,
            CanApplyAutomatically: false,
            RuleId: "en.spelling.typo",
            LinguisticCategory: LinguisticIssueCategory.Typo,
            Confidence: 0.8);
    }

    private static bool IsSentenceStart(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c is '"' or '«' or '(' or '\'') continue;
            return c is '.' or '!' or '?' or '…' or '\n';
        }

        return true;
    }
}
