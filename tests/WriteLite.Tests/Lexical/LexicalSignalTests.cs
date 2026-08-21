using WriteLite.Language.Russian;
using WriteLite.Services.Lexical;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Lexical;

/// <summary>
/// The dictionary as active correction knowledge, rather than a yes/no word check.
/// </summary>
/// <remarks>
/// Phase 2 measured what happens when a general Russian dictionary is allowed to judge
/// informal Russian: enabling LanguageTool dropped slang preservation from 0.957 to 0.717
/// and profanity preservation from 0.885 to 0.385. The register metadata that would have
/// prevented that was already indexed for all 3.09 M forms and simply unused.
///
/// These tests pin the product rules that follow from it: slang is not an error, profanity
/// is never sanitised, names and terminology survive, and a user's own word is untouchable.
/// </remarks>
[TestClass]
public sealed class LexicalSignalTests
{
    private static LocalSpellChecker? _checker;
    private static LexicalSignalService? _signals;

    [ClassInitialize]
    public static void Load(TestContext _)
    {
        _checker = new LocalSpellChecker();
        _signals = LexicalSignalService.TryCreate(_checker);
    }

    [ClassCleanup]
    public static void Unload() => _checker?.Dispose();

    private static LexicalSignalService Signals
    {
        get
        {
            if (_signals is null) Assert.Inconclusive("Russian form index not deployed next to the test binaries");
            return _signals!;
        }
    }

    [TestMethod]
    public void KnownOrdinaryWord_HasLemmaAndPartOfSpeech()
    {
        var signals = Signals.For("компания");

        Assert.IsTrue(signals.IsKnown);
        Assert.IsFalse(string.IsNullOrEmpty(signals.Lemma), "an indexed form should carry its lemma");
        Assert.AreNotEqual(RussianPartOfSpeech.Unknown, signals.PartOfSpeech);
        Assert.IsFalse(signals.HasProtectedRegister, "an ordinary noun carries no special register");
    }

    [TestMethod]
    public void UnknownWord_ReportsNothingRatherThanGuessing()
    {
        var signals = Signals.For("зюквафыр");

        Assert.IsFalse(signals.IsKnown);
        Assert.IsFalse(signals.HasProtectedRegister);
        Assert.IsNull(signals.RegisterLabel);
    }

    /// <summary>
    /// Modern internet and gaming vocabulary must be recognised, not treated as typos.
    /// These are the words that collapsed to 0.717 preservation once LanguageTool was on.
    /// </summary>
    [TestMethod]
    [DataRow("имба")]
    [DataRow("кринж")]
    [DataRow("рофл")]
    [DataRow("баг")]
    public void ModernVocabulary_IsRecognisedAsAWord(string word)
    {
        var signals = Signals.For(word);

        Assert.IsTrue(
            signals.IsKnown,
            $"'{word}' is in the shipped modern-vocabulary pack and must not read as unknown");
    }

    /// <summary>
    /// The gap this used to record is closed.
    /// </summary>
    /// <remarks>
    /// Until Phase 7.5 «юзать» was absent from the vocabulary pack and from OpenCorpora, and
    /// a named test held that fact so it could not be forgotten. It was not a theoretical
    /// gap: the spelling layer offered «юзать» → «юань» in live use, alongside «нерфить» →
    /// «неофит». The pack now carries the word and the index was rebuilt, so the assertion is
    /// inverted — the words are known, and the substitutions they caused are pinned in
    /// <c>SemanticSafetyRegressionTests</c>.
    /// </remarks>
    [TestMethod]
    [DataRow("юзать")]
    [DataRow("нерфить")]
    [DataRow("бафф")]
    [DataRow("тильт")]
    [DataRow("дефолтный")]
    public void FormerCoverageGaps_AreNowInTheVocabularyPack(string word)
    {
        Assert.IsTrue(
            Signals.For(word).IsKnown,
            $"'{word}' regressed out of the shipped modern-vocabulary pack");
    }

    /// <summary>
    /// Whatever else happens, a recognised word carrying a protected register must have a
    /// reason we can show the user rather than a silent suppression.
    /// </summary>
    [TestMethod]
    public void ProtectedRegister_CarriesAUserFacingReason()
    {
        var candidates = new[] { "имба", "рофл", "кринж", "юзать" };
        var labelled = candidates
            .Select(Signals.For)
            .Where(s => s.HasProtectedRegister)
            .ToList();

        if (labelled.Count == 0)
        {
            Assert.Inconclusive("no register flags on the sampled modern vocabulary in this pack build");
            return;
        }

        foreach (var signals in labelled)
        {
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(signals.RegisterLabel),
                "a suppressed correction must be explainable, not silent");
        }
    }

    [TestMethod]
    public void UserDictionaryWord_IsAlwaysKnownAndAlwaysLabelled()
    {
        var dictionary = UserDictionaryService.CreateEmpty();
        dictionary.Add("Зюквафыр");

        var service = new LexicalSignalService(_checker?.RussianFormIndex, dictionary);
        var signals = service.For("Зюквафыр");

        Assert.IsTrue(signals.IsKnown, "a user's own word must never read as unknown");
        Assert.IsTrue(signals.IsUserWord);
        Assert.AreEqual("из пользовательского словаря", signals.RegisterLabel);
    }

    [TestMethod]
    public void FrequencyRank_IsAvailableForCommonWords()
    {
        var common = Signals.For("что");
        var rare = Signals.For("экзегеза");

        if (!common.IsKnown)
        {
            Assert.Inconclusive("frequency data not deployed");
            return;
        }

        Assert.IsTrue(common.HasFrequencyEvidence, "a very common word should carry a frequency rank");
        if (rare.IsKnown && rare.FrequencyRank > 0)
        {
            Assert.IsLessThan(
                rare.FrequencyRank,
                common.FrequencyRank,
                "rank 1 is the most frequent word, so a common word must rank lower than a rare one");
        }
    }

    /// <summary>
    /// The lookup is on the analysis path, so it has to be cheap. A DAFSA walk plus a
    /// fixed-offset read into a memory-mapped array should be microseconds.
    /// </summary>
    [TestMethod]
    public void Lookup_IsFastEnoughForThePerTokenPath()
    {
        var words = new[] { "компания", "имба", "который", "зюквафыр", "программирование" };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 2000; i++)
        {
            foreach (var word in words)
            {
                _ = Signals.For(word);
            }
        }

        sw.Stop();
        var perLookupMicroseconds = sw.Elapsed.TotalMilliseconds * 1000 / (2000 * words.Length);

        Assert.IsLessThan(
            50,
            perLookupMicroseconds,
            $"lexical signal lookup cost {perLookupMicroseconds:F1} µs per word; at that price it "
            + "cannot sit on the per-token analysis path");
    }
}
