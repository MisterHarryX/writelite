using WriteLite.Language.Core;
using WriteLite.Language.Russian;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Spelling;

/// <summary>
/// Keyboard-layout recovery in both directions, and the hard negatives that keep it from
/// becoming a machine for rewriting ordinary words.
/// </summary>
/// <remarks>
/// Phase 4 found the Latin-target direction missing: the converter mapped ЙЦУКЕН→QWERTY and
/// then resolved the result against the Russian lexicon only, so «пшерги» came back as
/// «перги» rather than "github". The negatives matter at least as much as the positives —
/// a layout path that fires on real Russian words costs more than the one it fixes, and
/// mixed Russian/English text is the documented WriteLite use case (§10).
/// </remarks>
[TestClass]
public sealed class KeyboardLayoutRecoveryTests
{
    private static RussianFormIndexLexicon? _lexicon;

    [ClassInitialize]
    public static void Load(TestContext _) => _lexicon = RussianFormIndexLexicon.TryLoad();

    [ClassCleanup]
    public static void Unload() => _lexicon?.Dispose();

    // ── The mapping itself ──────────────────────────────────────────────────────

    [TestMethod]
    public void LayoutConversion_WorksInBothDirections()
    {
        Assert.AreEqual("привет", RussianKeyboard.LatinToRussianLayout("ghbdtn"));
        Assert.AreEqual("hello", RussianKeyboard.RussianToLatinLayout("руддщ"));
        Assert.AreEqual("github", RussianKeyboard.RussianToLatinLayout("пшерги"));
        Assert.AreEqual("google", RussianKeyboard.RussianToLatinLayout("пщщпду"));
        Assert.AreEqual("python", RussianKeyboard.RussianToLatinLayout("знерщт"));
    }

    [TestMethod]
    public void CyrillicTextDetection_RejectsAnythingThatIsNotPureCyrillic()
    {
        Assert.IsTrue(RussianKeyboard.IsCyrillicText("пшерги"));
        Assert.IsFalse(RussianKeyboard.IsCyrillicText("github"));
        Assert.IsFalse(RussianKeyboard.IsCyrillicText("файл2"));
        Assert.IsFalse(RussianKeyboard.IsCyrillicText("привет-мир"));
        Assert.IsFalse(RussianKeyboard.IsCyrillicText(""));
    }

    // ── The English lexicon behind the Latin direction ──────────────────────────

    [TestMethod]
    public void EnglishLexicon_IsDeployedAndCarriesBothOrdinaryAndTechnicalWords()
    {
        var english = new EnglishLayoutLexicon();

        Assert.IsGreaterThan(50_000, english.WordCount, "the generated English word list is not deployed");
        Assert.IsTrue(english.Contains("error"));
        Assert.IsTrue(english.Contains("start"));
        Assert.IsTrue(english.Contains("yes"));
        Assert.IsTrue(english.Contains("github"));
        Assert.IsTrue(english.IsTechnicalTerm("github"));
        Assert.IsTrue(english.IsTechnicalTerm("npm"));
        Assert.IsFalse(english.IsTechnicalTerm("arteriogram"));
        Assert.IsFalse(english.Contains("пшерги"));

        // Function words are members but carry no technical bonus: they are short, and a
        // short Cyrillic string collides with a short English word too easily.
        Assert.IsTrue(english.Contains("the"));
        Assert.IsTrue(english.Contains("yes"));
        Assert.IsFalse(english.IsTechnicalTerm("the"));
        Assert.IsFalse(english.IsTechnicalTerm("yes"));
    }

    // ── Latin target: the Phase 4 failures ──────────────────────────────────────

    [TestMethod]
    [DataRow("пшерги", "github")]
    [DataRow("пщщпду", "google")]
    [DataRow("знерщт", "python")]
    [DataRow("уккщк", "error")]
    [DataRow("ыефке", "start")]
    [DataRow("руддщ", "hello")]
    public void CyrillicKeystrokes_RecoverTheirEnglishWord(string typed, string expected)
    {
        var best = Best(typed);
        Assert.AreEqual(expected, best, $"'{typed}' should recover '{expected}'");
    }

    [TestMethod]
    public void RecoveredEnglishWord_KeepsTheTypedCapitalisation()
        => Assert.AreEqual("Github", Best("Пшерги"));

    // ── Russian target: what already worked, and must keep working ──────────────

    [TestMethod]
    [DataRow("ghbdtn", "привет")]
    [DataRow("crjhj", "скоро")]
    [DataRow("ltkf", "дела")]
    [DataRow("ujhjl", "город")]
    public void LatinKeystrokes_StillRecoverTheirRussianWord(string typed, string expected)
        => Assert.AreEqual(expected, Best(typed));

    // ── Hard negatives: ordinary Russian must survive ───────────────────────────

    [TestMethod]
    [DataRow("привет")]
    [DataRow("работа")]
    [DataRow("сегодня")]
    [DataRow("который")]
    [DataRow("говорит")]
    [DataRow("книга")]
    [DataRow("время")]
    [DataRow("человек")]
    [DataRow("система")]
    [DataRow("вопрос")]
    public void CorrectRussianWords_AreNeverOfferedAnEnglishRewrite(string word)
    {
        var suggestions = Suggestions(word);
        Assert.IsFalse(
            suggestions.Any(s => s.Any(char.IsAsciiLetter)),
            $"'{word}' was offered a Latin rewrite: {string.Join(", ", suggestions)}");
    }

    [TestMethod]
    [DataRow("превет")]
    [DataRow("здраствуйте")]
    [DataRow("севодня")]
    [DataRow("граматика")]
    [DataRow("интиресно")]
    public void OrdinaryRussianTypos_AreCorrectedInRussian_NotIntoEnglish(string typo)
    {
        var best = Best(typo);
        Assert.IsNotNull(best, $"'{typo}' produced no suggestion at all");
        Assert.IsFalse(
            best.Any(char.IsAsciiLetter),
            $"'{typo}' was 'corrected' into Latin text '{best}'");
    }

    [TestMethod]
    public void TwoLetterCyrillic_IsNeverRecoveredUnlessItIsTechnicalVocabulary()
    {
        // Two-character strings map onto real English words far too often by accident.
        foreach (var word in new[] { "на", "не", "по", "за", "то", "он", "мы" })
        {
            Assert.IsFalse(
                Suggestions(word).Any(s => s.Any(char.IsAsciiLetter)),
                $"'{word}' was offered a Latin rewrite");
        }
    }

    // ── Mixed-language text is the documented use case (§10) ────────────────────

    [TestMethod]
    [DataRow("middleware")]
    [DataRow("pipeline")]
    [DataRow("GitHub")]
    [DataRow("npm")]
    [DataRow("build")]
    [DataRow("Qwen")]
    [DataRow("The")]
    [DataRow("Verge")]
    public void EnglishTermsInRussianProse_AreLeftAlone(string term)
    {
        var checker = new LocalSpellChecker();
        try
        {
            var result = checker.CheckWord(term, SpellingLanguage.Russian);
            Assert.IsTrue(
                result.IsKnown || result.Suggestions.Count == 0,
                $"'{term}' was flagged with {string.Join(", ", result.Suggestions)}");
        }
        finally
        {
            checker.Dispose();
        }
    }

    // ── Ranking evidence is used, not just membership (§11) ─────────────────────

    [TestMethod]
    public void TechnicalVocabularyOutranksAnOrdinaryDictionaryHit()
    {
        var technical = RussianCandidateScorer.Score(
            "пшерги", "github", 0, RussianCandidateOrigin.KeyboardLayoutLatin, default, technicalTerm: true);
        var ordinary = RussianCandidateScorer.Score(
            "пшерги", "github", 0, RussianCandidateOrigin.KeyboardLayoutLatin, default, technicalTerm: false);

        Assert.IsGreaterThan(ordinary, technical);
    }

    [TestMethod]
    public void ShortLatinRecoveryIsTrustedLessThanALongOne()
    {
        var longWord = RussianCandidateScorer.Score(
            "знерщт", "python", 0, RussianCandidateOrigin.KeyboardLayoutLatin, default);
        var shortWord = RussianCandidateScorer.Score(
            "нуы", "yes", 0, RussianCandidateOrigin.KeyboardLayoutLatin, default);

        Assert.IsGreaterThan(shortWord, longWord);
    }

    [TestMethod]
    public void ARealRussianWordOutranksAnyLatinRecovery()
    {
        // The production comparison, with the frequency and register evidence the index
        // actually carries: a known, frequent Russian form must beat a layout guess.
        if (_lexicon is null)
        {
            Assert.Inconclusive("Russian form index is not deployed.");
            return;
        }

        var ranked = _lexicon.RankCandidates("превет", 5);
        Assert.IsNotEmpty(ranked);
        Assert.AreEqual("привет", ranked[0].Word);
        Assert.AreEqual(RussianCandidateOrigin.EditDistance, ranked[0].Origin);
    }

    [TestMethod]
    public void WithoutAnEnglishLexicon_LatinTargetRecoveryIsSimplyAbsent()
    {
        var index = RussianFormIndex.Load();
        if (index is null)
        {
            Assert.Inconclusive("Russian form index is not deployed.");
            return;
        }

        using (index)
        {
            var generator = new RussianCandidateGenerator(index);
            var ranked = generator.Rank("пшерги");
            Assert.IsFalse(
                ranked.Any(c => c.Origin == RussianCandidateOrigin.KeyboardLayoutLatin),
                "a null lexicon must disable the branch, not crash it");
        }
    }

    private static string? Best(string word) => Suggestions(word).FirstOrDefault();

    private static IReadOnlyList<string> Suggestions(string word)
    {
        if (_lexicon is null)
        {
            Assert.Inconclusive("Russian form index is not deployed.");
            return [];
        }

        return _lexicon.Suggest(word, 5);
    }
}
