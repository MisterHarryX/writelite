using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Spelling;

/// <summary>
/// Guards the curated correction table in <see cref="LocalSpellChecker"/>.
///
/// The table exists because Hunspell ranks candidates by edit distance and its
/// own frequency data, neither of which is tuned for Russian typing errors.
/// A user typed "Как твои дила" and the app offered "дали" — one edit away, a
/// real word, and completely wrong. A wrong correction is worse than none, so
/// each word below is one where the intended form is unambiguous and the
/// generic ranker was observed to pick badly.
/// </summary>
[TestClass]
public sealed class StableCorrectionsTests
{
    private static LocalSpellChecker CreateChecker() => new(SeedSpellDictionary.Load());

    private static string? FirstSuggestion(LocalSpellChecker checker, string word)
    {
        var result = checker.CheckWord(word, SpellingLanguage.Russian);
        return result.Suggestions.Count > 0 ? result.Suggestions[0] : null;
    }

    [TestMethod]
    public void Dila_IsCorrectedToDela_NotDali()
    {
        using var checker = CreateChecker();

        var suggestion = FirstSuggestion(checker, "дила");

        Assert.AreEqual("дела", suggestion, "\"дила\" must resolve to \"дела\"; \"дали\" is a different word.");
    }

    [DataTestMethod]
    [DataRow("вроди", "вроде")]
    [DataRow("сдесь", "здесь")]
    [DataRow("зделать", "сделать")]
    [DataRow("извените", "извините")]
    [DataRow("граматика", "грамматика")]
    [DataRow("колличество", "количество")]
    [DataRow("професия", "профессия")]
    [DataRow("следущий", "следующий")]
    [DataRow("будующий", "будущий")]
    [DataRow("агенство", "агентство")]
    [DataRow("обьяснить", "объяснить")]
    [DataRow("малышы", "малыши")]
    [DataRow("расчитать", "рассчитать")]
    [DataRow("экстримальный", "экстремальный")]
    public void CuratedTypo_ResolvesToTheIntendedWord(string typo, string expected)
    {
        using var checker = CreateChecker();

        Assert.AreEqual(expected, FirstSuggestion(checker, typo));
    }

    [DataTestMethod]
    [DataRow("врятли", "вряд ли")]
    [DataRow("потомучто", "потому что")]
    [DataRow("какбудто", "как будто")]
    [DataRow("вобщем", "в общем")]
    public void RunTogetherPhrase_IsSplit(string typo, string expected)
    {
        using var checker = CreateChecker();

        Assert.AreEqual(expected, FirstSuggestion(checker, typo));
    }

    [TestMethod]
    public void CuratedTable_NeverMapsAWordToItself()
    {
        using var checker = CreateChecker();

        // A self-mapping entry would be filtered out downstream as an identical
        // correction, leaving the word silently unfixed while looking handled.
        // Three such entries slipped into the table once; this catches the next.
        string[] curated =
        [
            "дила", "сдесь", "зделать", "извените", "граматика", "колличество",
            "професия", "следущий", "будующий", "агенство", "обьяснить",
            "малышы", "расчитать", "экстримальный", "врятли", "потомучто",
            "какбудто", "вобщем", "координально", "пришол", "жызнь",
            "машына", "симпотичный", "чуствовать", "учавствовать", "руский",
            "програма", "подьезд", "вкурсе",
        ];

        foreach (var word in curated)
        {
            var suggestion = FirstSuggestion(checker, word);
            Assert.IsNotNull(suggestion, $"\"{word}\" produced no suggestion at all.");
            Assert.AreNotEqual(
                word,
                suggestion,
                StringComparer.OrdinalIgnoreCase,
                $"\"{word}\" was corrected to itself.");
        }
    }

    [TestMethod]
    public void CorrectWords_AreLeftAlone()
    {
        using var checker = CreateChecker();

        // The counterpart risk: an over-eager table flagging correct Russian.
        foreach (var word in new[] { "дела", "дали", "здесь", "сделать", "количество", "прийти" })
        {
            var result = checker.CheckWord(word, SpellingLanguage.Russian);
            Assert.IsTrue(result.IsKnown, $"\"{word}\" is correct Russian and must not be flagged.");
        }
    }
}
