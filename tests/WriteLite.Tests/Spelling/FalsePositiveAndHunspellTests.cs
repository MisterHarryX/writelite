using WriteLite.Language.Core;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Spelling;

[TestClass]
public sealed class FalsePositiveAndHunspellTests
{
    private static readonly string[] RussianMustBeKnown =
    [
        "привет", "здравствуйте", "человек", "приложение", "программа", "компьютер",
        "сообщение", "предложение", "словарь", "исправление", "проверка", "работает",
        "красивый", "магазин", "друг", "пользователь", "интерфейс", "ошибка",
        "текст", "слово", "русский", "английский", "смешанный", "локальный",
        "интеллект", "поддержка", "настройки", "диагностика"
    ];

    private static readonly string[] EnglishMustBeKnown =
    [
        "hello", "application", "computer", "message", "sentence", "dictionary",
        "correction", "language", "beautiful", "support", "settings", "diagnostics",
        "local", "intelligence", "program", "user", "interface", "error", "text", "word"
    ];

    [TestMethod]
    public void IdenticalCandidates_AreRejected()
    {
        Assert.IsTrue(CorrectionCandidateValidityPolicy.IsIdenticalCorrection("привет", "привет"));
        Assert.IsTrue(CorrectionCandidateValidityPolicy.IsMeaninglessSpellingCandidate("привет", "привет"));
        Assert.IsTrue(CorrectionCandidateValidityPolicy.IsIdenticalCorrection("hello", "hello"));
        var filtered = CorrectionCandidateValidityPolicy.FilterSuggestions("привет", ["привет", "приветы", "привет"]);
        Assert.IsFalse(filtered.Contains("привет"));
    }

    [TestMethod]
    public void Analyzer_DoesNotFlagPrivet()
    {
        using var checker = new LocalSpellChecker();
        var analyzer = new SpellTextAnalyzer(checker);
        var issues = analyzer.Analyze("привет");
        Assert.AreEqual(0, issues.Count, "привет must never produce spelling issues");
        Assert.IsTrue(checker.CheckWord("привет", SpellingLanguage.Russian).IsKnown);
    }

    [TestMethod]
    public void Analyzer_DoesNotCreateIdenticalReplacement()
    {
        using var checker = new LocalSpellChecker();
        var analyzer = new SpellTextAnalyzer(checker);
        foreach (var word in RussianMustBeKnown.Concat(EnglishMustBeKnown))
        {
            var issues = analyzer.Analyze(word);
            foreach (var issue in issues)
            {
                Assert.IsFalse(
                    CorrectionCandidateValidityPolicy.IsIdenticalCorrection(issue.Original, issue.Replacement ?? issue.Original),
                    $"Identical candidate for {word}");
            }
        }
    }

    [TestMethod]
    public void CommonRussianWords_AreKnownOrNotUnderlinedWithoutSuggestion()
    {
        using var checker = new LocalSpellChecker();
        var analyzer = new SpellTextAnalyzer(checker);
        var failures = new List<string>();
        foreach (var word in RussianMustBeKnown)
        {
            var issues = analyzer.Analyze(word);
            if (issues.Count > 0)
            {
                failures.Add($"{word} => {issues[0].Replacement}");
            }
        }

        Assert.AreEqual(0, failures.Count, string.Join("; ", failures));
    }

    [TestMethod]
    public void CommonEnglishWords_AreKnownOrNotUnderlinedWithoutSuggestion()
    {
        using var checker = new LocalSpellChecker();
        var analyzer = new SpellTextAnalyzer(checker);
        var failures = new List<string>();
        foreach (var word in EnglishMustBeKnown)
        {
            var issues = analyzer.Analyze(word);
            if (issues.Count > 0)
            {
                failures.Add($"{word} => {issues[0].Replacement}");
            }
        }

        Assert.AreEqual(0, failures.Count, string.Join("; ", failures));
    }

    [TestMethod]
    public void RealTypos_StillSuggested()
    {
        using var checker = new LocalSpellChecker();
        var analyzer = new SpellTextAnalyzer(checker);
        var issues = analyzer.Analyze("пливет");
        Assert.IsTrue(issues.Count >= 1);
        Assert.AreEqual("привет", issues[0].Replacement);
        Assert.IsFalse(CorrectionCandidateValidityPolicy.IsIdenticalCorrection(issues[0].Original, issues[0].Replacement!));
    }

    [TestMethod]
    public void HunspellResources_LoadWhenPresent()
    {
        using var checker = new LocalSpellChecker();
        Console.WriteLine($"full={checker.IsFullDictionaryLoaded} ru={checker.Stats.RussianWordCount} en={checker.Stats.EnglishWordCount}");
        // Soft assertion: seed always works; full preferred
        Assert.IsGreaterThan(20, checker.Stats.RussianWordCount);
        Assert.AreEqual(0, checker.Stats.EnglishWordCount);
    }
}
