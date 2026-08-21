using System.Diagnostics;
using WriteLite.AI.Local;
using WriteLite.Language.Russian;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Ai;

/// <summary>
/// Behaviour of the contextual layer: the part of correction that needs the
/// sentence, not just the word.
/// </summary>
[TestClass]
public sealed class ContextualRerankerTests
{
    private static ContextualCorrectionRefiner? _refiner;

    [ClassInitialize]
    public static void Load(TestContext _) => _refiner = ContextualCorrectionRefiner.TryLoad();

    [ClassCleanup]
    public static void Unload() => _refiner?.Dispose();

    private static ContextualCorrectionRefiner Refiner
    {
        get
        {
            if (_refiner is null) Assert.Inconclusive("reranker not deployed next to the test binaries");
            return _refiner!;
        }
    }

    [TestMethod]
    public void ConfusionSets_AreLoaded()
    {
        Assert.IsTrue(Refiner.ConfusionSetCount >= 250,
            $"only {Refiner.ConfusionSetCount} confusion sets loaded");
    }

    [TestMethod]
    public void Reranker_PrefersTheContextuallyCorrectMember()
    {
        var reranker = RussianContextualReranker.TryLoad();
        if (reranker is null) { Assert.Inconclusive("reranker not deployed"); return; }
        using var _ = reranker;

        // Each pair is the same sentence with one word swapped. Both readings
        // are made of real Russian words; only meaning separates them.
        foreach (var (correct, wrong) in new[]
                 {
                     ("Наша компания открыла новый офис в центре города.",
                      "Наша кампания открыла новый офис в центре города."),
                     ("Мне нужно надеть тёплую куртку перед выходом.",
                      "Мне нужно одеть тёплую куртку перед выходом."),
                     ("Он должен будет прийти на встречу завтра.",
                      "Он должен будит прийти на встречу завтра."),
                 })
        {
            var correctScore = reranker.ScoreSentence(correct);
            var wrongScore = reranker.ScoreSentence(wrong);
            Assert.IsTrue(correctScore > wrongScore,
                $"model preferred the wrong reading: {wrongScore:F3} >= {correctScore:F3} for \"{wrong}\"");
        }
    }

    [TestMethod]
    public void GrammaticallyImpossibleRealWords_AreFound()
    {
        // "будит" is a perfectly good verb form, so the lexicon has nothing to
        // say about it — but it cannot follow "должен", and the model is very
        // sure about that (measured gain +0.83 against a 0.35 gate).
        var findings = Refiner.FindRealWordErrors("Он должен будит прийти на встречу завтра.");
        Assert.IsTrue(findings.Any(f => f.Original == "будит" && f.Replacement == "будет"),
            "expected будит -> будет, got: "
            + string.Join(", ", findings.Select(f => $"{f.Original}->{f.Replacement}")));
    }

    [TestMethod]
    public void SemanticallyConfusablePairs_AreRankedRightButNotAsserted()
    {
        // компания/кампания differ by meaning, not grammar. The model leans the
        // correct way in both directions but only by ~0.02, far below the gate:
        // deciding which one a sentence is *about* needs world knowledge a 29M
        // encoder does not have. Documented here so the next person measures
        // instead of assuming the detector covers this class.
        var reranker = RussianContextualReranker.TryLoad();
        if (reranker is null) { Assert.Inconclusive("reranker not deployed"); return; }
        using var _ = reranker;

        const string business = "Наша компания открыла новый офис в центре города.";
        var swapped = business.Replace("компания", "кампания");
        Assert.IsTrue(reranker.ScoreSentence(business) > reranker.ScoreSentence(swapped),
            "the model should at least lean towards the correct member");

        Assert.AreEqual(0, Refiner.FindRealWordErrors(business).Count,
            "a correct sentence must never be flagged");
    }

    [TestMethod]
    public void CorrectSentences_AreLeftAlone()
    {
        // The margin exists for these. A contextual layer that "improves"
        // correct writing is worse than no contextual layer at all.
        foreach (var sentence in new[]
                 {
                     "Наша компания открыла новый офис в центре города.",
                     "Предвыборная кампания продлится до конца месяца.",
                     "Мне нужно надеть тёплую куртку перед выходом.",
                     "Он должен прийти на встречу завтра утром.",
                     "Разработчик отправил изменения в репозиторий вчера вечером.",
                 })
        {
            var findings = Refiner.FindRealWordErrors(sentence);
            Assert.AreEqual(0, findings.Count,
                $"false correction in \"{sentence}\": "
                + string.Join(", ", findings.Select(f => $"{f.Original}->{f.Replacement}")));
        }
    }

    [TestMethod]
    public void Scoring_FitsTheInteractiveBudget()
    {
        var reranker = RussianContextualReranker.TryLoad();
        if (reranker is null) { Assert.Inconclusive("reranker not deployed"); return; }
        using var _ = reranker;

        const string sentence = "Я хочу купить новый ноутбук для работы.";
        string[] candidates = ["хочу", "хожу", "хочу же", "хотя"];

        reranker.Rerank(sentence, 2, 4, candidates);   // warm up

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) reranker.Rerank(sentence, 2, 4, candidates);
        sw.Stop();

        var perCall = sw.Elapsed.TotalMilliseconds / 10;
        Assert.IsTrue(perCall < 120, $"reranking four candidates took {perCall:F1} ms");
    }

    [TestMethod]
    public void MissingModel_DegradesToLexicalRanking()
    {
        // Offline-first means optional: a deployment without the model must not
        // throw, it must simply stop being context-aware.
        Assert.IsNull(RussianContextualReranker.TryLoad(Path.Combine(Path.GetTempPath(), "writelite-no-such-model")));
        Assert.IsNull(ContextualCorrectionRefiner.TryLoad(Path.Combine(Path.GetTempPath(), "writelite-no-such-model")));
    }
}
