using System.Diagnostics;
using WriteLite.Language.Russian;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Spelling;

/// <summary>
/// Guards the binary form index: its contents, its perfect-hash addressing, and
/// the latency budget it has to fit inside a keystroke-driven UI.
/// </summary>
/// <remarks>
/// The metadata array is addressed purely by the id the graph traversal derives
/// from per-arc subtree counts. If that arithmetic drifts, every word silently
/// gets another word's part of speech and frequency — no crash, just quietly
/// worse suggestions. <see cref="Metadata_IsAddressedByTheCorrectWordId"/> is
/// the tripwire for that.
/// </remarks>
/// <remarks>
/// Not parallelised. These assertions measure process-wide facts — managed heap
/// growth and wall-clock per lookup — which are meaningless while other tests are
/// allocating and competing for cores in the same process. Run alone they are
/// stable; run alongside the rest of the suite they fail on load rather than on a
/// real regression, which is the worst kind of test.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class RussianFormIndexTests
{
    private static RussianFormIndex? _index;

    [ClassInitialize]
    public static void Load(TestContext _) => _index = RussianFormIndex.Load();

    [ClassCleanup]
    public static void Unload() => _index?.Dispose();

    private static RussianFormIndex Index
    {
        get
        {
            Assert.IsNotNull(_index, "ru-forms.wldawg was not deployed next to the test binaries");
            return _index;
        }
    }

    [TestMethod]
    public void Index_CoversAtLeastOneMillionForms()
    {
        Assert.IsTrue(
            Index.WordCount >= 1_000_000,
            $"expected at least 1M Russian surface forms, found {Index.WordCount}");
        Assert.IsTrue(Index.HasMetadata, "metadata sidecar missing");
    }

    [TestMethod]
    [DataRow("привет")]
    [DataRow("здравствуйте")]
    [DataRow("программирование")]
    [DataRow("хотела")]
    [DataRow("хотели")]
    [DataRow("пришёл")]
    [DataRow("пришел")]        // ё folds to е
    [DataRow("Москва")]        // capitalisation folds
    [DataRow("несоответствия")]
    [DataRow("рассчитывающийся")]
    public void Contains_KnownForms(string word)
        => Assert.IsTrue(Index.Contains(word), $"{word} should be a known Russian form");

    [TestMethod]
    [DataRow("превет")]
    [DataRow("зделал")]
    [DataRow("хочю")]
    [DataRow("програма")]
    [DataRow("асдфгыв")]
    public void Contains_RejectsMisspellings(string word)
        => Assert.IsFalse(Index.Contains(word), $"{word} must not be treated as a real word");

    [TestMethod]
    public void Metadata_IsAddressedByTheCorrectWordId()
    {
        // The lemma stored against a form is the one property that cannot be
        // right by accident if the id arithmetic is wrong.
        foreach (var (form, lemma) in new[]
                 {
                     ("столами", "стол"),
                     ("книгами", "книга"),
                     ("написанный", "написать"),
                     ("быстрее", "быстрый"),
                     ("интереснее", "интересный"),
                 })
        {
            var info = Index.GetInfo(form);
            Assert.AreEqual(lemma, info.Lemma, $"wrong lemma for {form}");
        }
    }

    [TestMethod]
    public void FindWithin_ReturnsTheIntendedWord()
    {
        foreach (var (typo, expected) in new[]
                 {
                     ("превет", "привет"),
                     ("програма", "программа"),
                     ("севодня", "сегодня"),
                     ("првиет", "привет"),
                     ("интиресный", "интересный"),
                 })
        {
            var matches = Index.FindWithin(typo, 2, 128).Select(m => m.Word).ToArray();
            CollectionAssert.Contains(matches, expected, $"{typo} -> {expected} not among {matches.Length} matches");
        }
    }

    [TestMethod]
    public void FindWithin_HonoursTheDistanceBudget()
    {
        foreach (var match in Index.FindWithin("привет", 1, 64))
        {
            Assert.IsTrue(match.Distance <= 1, $"{match.Word} reported at distance {match.Distance}");
        }
    }

    [TestMethod]
    public void Lookup_StaysWellUnderAKeystroke()
    {
        var words = new[] { "привет", "программа", "сегодня", "пожалуйста", "интересный", "асдфгыв" };
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20_000; i++)
        {
            Index.Contains(words[i % words.Length]);
        }

        sw.Stop();
        var microseconds = sw.Elapsed.TotalMilliseconds * 1000 / 20_000;
        Assert.IsTrue(microseconds < 50, $"exact lookup took {microseconds:F1} us/word");
    }

    [TestMethod]
    public void CandidateSearch_FitsTheTypingBudget()
    {
        var generator = new RussianCandidateGenerator(Index);
        var typos = new[] { "превет", "програма", "севодня", "интиресный", "пажалуйста", "зделать" };

        // Warm the page cache so the measurement reflects steady state, which is
        // what a user typing continuously actually experiences.
        foreach (var typo in typos) generator.Rank(typo);

        var sw = Stopwatch.StartNew();
        foreach (var typo in typos) generator.Rank(typo);
        sw.Stop();

        var perWord = sw.Elapsed.TotalMilliseconds / typos.Length;
        Assert.IsTrue(perWord < 60, $"candidate generation took {perWord:F1} ms/word");
    }
}

/// <summary>
/// Behavioural guarantees for Russian correction that the old Hunspell-only
/// path could not make.
/// </summary>
[TestClass]
public sealed class RussianCandidateGeneratorTests
{
    private static RussianFormIndex? _index;
    private static RussianCandidateGenerator? _generator;

    [ClassInitialize]
    public static void Load(TestContext _)
    {
        _index = RussianFormIndex.Load();
        if (_index is not null) _generator = new RussianCandidateGenerator(_index);
    }

    [ClassCleanup]
    public static void Unload() => _index?.Dispose();

    private static RussianCandidateGenerator Generator
    {
        get
        {
            Assert.IsNotNull(_generator, "form index not deployed");
            return _generator;
        }
    }

    [TestMethod]
    [DataRow("превет", "привет")]
    [DataRow("севодня", "сегодня")]
    [DataRow("програма", "программа")]
    [DataRow("првиет", "привет")]
    [DataRow("интиресный", "интересный")]
    [DataRow("пажалуйста", "пожалуйста")]
    [DataRow("зделать", "сделать")]
    [DataRow("руский", "русский")]
    [DataRow("дила", "дела")]
    [DataRow("сдесь", "здесь")]
    public void TopCandidate_IsTheIntendedWord(string typo, string expected)
    {
        var ranked = Generator.Rank(typo);
        Assert.IsTrue(ranked.Count > 0, $"no candidates for {typo}");
        Assert.AreEqual(expected, ranked[0].Word, $"top candidate for {typo} was {ranked[0].Word}");
    }

    [TestMethod]
    public void WrongLayout_IsRecognisedRatherThanSpellCorrected()
    {
        var ranked = Generator.Rank("ghbdtn");
        Assert.IsTrue(ranked.Count > 0, "no candidate for a wrong-layout word");
        Assert.AreEqual("привет", ranked[0].Word);
        Assert.AreEqual(RussianCandidateOrigin.KeyboardLayout, ranked[0].Origin);
        Assert.IsTrue(
            RussianCorrectionConfidence.QualifiesForAutomaticReplacement(ranked),
            "a layout slip is unambiguous and should be safe to fix automatically");
    }

    [TestMethod]
    public void LatinLookalikes_AreNormalisedToCyrillic()
    {
        // "привет" with Latin p, o and e — visually identical, lexically absent.
        var ranked = Generator.Rank("привeт");
        Assert.IsTrue(ranked.Count > 0, "no candidate for a mixed-script word");
        Assert.AreEqual("привет", ranked[0].Word);
    }

    [TestMethod]
    public void MergedWords_AreSplitRatherThanRespelled()
    {
        var ranked = Generator.Rank("потомучто");
        Assert.IsTrue(ranked.Any(c => c.Word == "потому что"), "expected a word-split candidate");
    }

    [TestMethod]
    public void TwoEditGuesses_AreOfferedButNeverAppliedSilently()
    {
        // A two-edit candidate is a guess. It belongs in the suggestion list,
        // never in the user's text without them asking.
        foreach (var typo in new[] { "прафисионал", "здраствуйтe", "экстримальнй" })
        {
            var ranked = Generator.Rank(typo);
            if (ranked.Count == 0) continue;
            if (ranked[0].Distance < 2) continue;
            Assert.IsFalse(
                RussianCorrectionConfidence.QualifiesForAutomaticReplacement(ranked),
                $"{typo} -> {ranked[0].Word} was a two-edit guess yet qualified for silent replacement");
        }
    }

    [TestMethod]
    public void KnownWords_AreNeverHandedToTheCorrector()
    {
        // Real words must not acquire suggestions just because a closer-looking
        // word exists; that is how a spellchecker starts "fixing" correct text.
        using var checker = new LocalSpellChecker();
        foreach (var word in new[] { "кампания", "компания", "поделка", "подделка", "туш", "тушь" })
        {
            var result = checker.CheckWord(word, SpellingLanguage.Russian);
            Assert.IsTrue(result.IsKnown, $"{word} is a real Russian word and was flagged");
        }
    }

    [TestMethod]
    public void ProperNames_DoNotOutrankOrdinaryWords()
    {
        foreach (var typo in new[] { "превет", "севодня", "програма" })
        {
            var top = Generator.Rank(typo).FirstOrDefault();
            Assert.IsNotNull(top);
            Assert.IsFalse(
                top.Info.Flags.HasFlag(RussianFormFlags.ProperName),
                $"{typo} was corrected into the proper name {top.Word}");
        }
    }
}

/// <summary>Keyboard geometry the scorer depends on.</summary>
[TestClass]
public sealed class RussianKeyboardTests
{
    [TestMethod]
    public void Neighbours_MatchThePhysicalLayout()
    {
        Assert.IsTrue(RussianKeyboard.AreNeighbours('п', 'р'));
        Assert.IsTrue(RussianKeyboard.AreNeighbours('о', 'л'));
        Assert.IsTrue(RussianKeyboard.AreNeighbours('к', 'е'));
        Assert.IsFalse(RussianKeyboard.AreNeighbours('й', 'ю'));
        Assert.IsFalse(RussianKeyboard.AreNeighbours('ф', 'ъ'));
    }

    [TestMethod]
    public void Adjacency_IsSymmetric()
    {
        foreach (var key in "йцукенгшщзхъфывапролджэячсмитьбю")
        {
            foreach (var neighbour in RussianKeyboard.Neighbours(key))
            {
                Assert.IsTrue(
                    RussianKeyboard.AreNeighbours(neighbour, key),
                    $"{key}/{neighbour} adjacency is one-way");
            }
        }
    }

    [TestMethod]
    public void LayoutConversion_RoundTrips()
    {
        Assert.AreEqual("привет", RussianKeyboard.LatinToRussianLayout("ghbdtn"));
        Assert.AreEqual("ghbdtn", RussianKeyboard.RussianToLatinLayout("привет"));
        Assert.AreEqual("Привет", RussianKeyboard.LatinToRussianLayout("Ghbdtn"));
    }

    [TestMethod]
    public void MixedScript_IsDetectedAndNormalised()
    {
        const string mixed = "привeт";     // Latin e
        Assert.IsTrue(RussianKeyboard.HasMixedScript(mixed));
        Assert.AreEqual("привет", RussianKeyboard.NormalizeHomoglyphs(mixed));
        Assert.IsNull(RussianKeyboard.NormalizeHomoglyphs("привет"), "pure Cyrillic needs no rewrite");
    }
}

/// <summary>The form index must not make the checker louder on correct text.</summary>
[TestClass]
public sealed class RussianFormIndexFalsePositiveTests
{
    [TestMethod]
    public void MixedScriptWords_ReachTheChecker()
    {
        // These used to be dropped before any lookup: DetectLanguage returned
        // "no language" for a token containing both scripts, which exempted
        // precisely the words most likely to be wrong.
        using var checker = new LocalSpellChecker();
        var analyzer = new SpellTextAnalyzer(checker);

        var issues = analyzer.Analyze("Он написал привeт в чат.");   // Latin e
        Assert.IsTrue(issues.Count > 0, "a homoglyph word produced no issue at all");
        var issue = issues[0];
        Assert.AreEqual("привeт", issue.Original);
        Assert.AreEqual("привет", issue.Replacement);
    }

    [TestMethod]
    public void LatinWordsInsideRussian_StayUntouched()
    {
        // The mixed-script change must not start flagging ordinary borrowed
        // spellings that happen to sit in a Russian sentence.
        using var checker = new LocalSpellChecker();
        var analyzer = new SpellTextAnalyzer(checker);

        foreach (var text in new[]
                 {
                     "Открой GitHub и создай pull request.",
                     "Скачай PDF файл с сайта.",
                     "Использую Windows и Visual Studio каждый день.",
                 })
        {
            var issues = analyzer.Analyze(text);
            Assert.AreEqual(0, issues.Count, $"false positive in: {text}");
        }
    }

    [TestMethod]
    public void ModernAndInformalWords_AreNotFlagged()
    {
        using var checker = new LocalSpellChecker();
        var words = new[]
        {
            "привет", "программа", "сегодня", "пожалуйста", "интересный",
            "компьютер", "приложение", "сообщение", "разработчик", "пользователи",
        };

        foreach (var word in words)
        {
            var result = checker.CheckWord(word, SpellingLanguage.Russian);
            Assert.IsTrue(result.IsKnown, $"{word} was flagged as a spelling error");
        }
    }
}
