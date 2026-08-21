using WriteLite.Models;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Spelling;

/// <summary>
/// Proves the contextual reranker is actually reachable from the analysis pipeline.
/// </summary>
/// <remarks>
/// It was not, until 2026-08-12. The model was trained, evaluated, quantised, shipped
/// next to the binaries and covered by its own unit tests, but nothing in the product
/// ever called <see cref="ContextualCorrectionRefiner"/> — the only references were
/// from its tests. End-to-end measurement put real-word detection recall at 0.013,
/// which is what a disconnected component looks like from the outside.
///
/// ContextualRerankerTests covers the model's behaviour. These cover the wiring, which
/// is the part that was broken, and which unit tests of the component could never catch.
/// </remarks>
[TestClass]
public sealed class ContextualSpellIntegrationTests
{
    private static LocalSpellChecker? _checker;

    [ClassInitialize]
    public static void Load(TestContext _) => _checker = new LocalSpellChecker();

    [ClassCleanup]
    public static void Unload() => _checker?.Dispose();

    private static SpellTextAnalyzer Analyzer(bool contextual)
    {
        if (_checker is null) Assert.Inconclusive("spell checker unavailable");
        return new SpellTextAnalyzer(_checker!) { ContextualRefinementEnabled = contextual };
    }

    /// <summary>"будит" is a real verb form, so no dictionary can object to it — but it
    /// cannot follow "должен". Only the contextual pass can see this.</summary>
    private const string RealWordError = "Он должен будит прийти на встречу завтра.";

    [TestMethod]
    public void RealWordError_IsReachableThroughTheAnalyzer()
    {
        var analyzer = Analyzer(contextual: true);
        if (!analyzer.ContextualRefinementEnabled)
        {
            Assert.Inconclusive("reranker not deployed next to the test binaries");
            return;
        }

        var issues = analyzer.Analyze(RealWordError);

        Assert.IsTrue(
            issues.Any(i => i.Original == "будит" && i.Replacement == "будет"),
            "the analyzer did not surface the real-word error the refiner detects; the "
            + "contextual pass is not wired into SpellTextAnalyzer. Found: "
            + string.Join(", ", issues.Select(i => $"{i.Original}->{i.Replacement}")));
    }

    [TestMethod]
    public void RealWordError_IsInvisibleToTheLexicalPassAlone()
    {
        // The control for the test above: without the contextual pass this sentence
        // produces nothing, which is why the disconnection went unnoticed for so long.
        var issues = Analyzer(contextual: false).Analyze(RealWordError);

        Assert.IsFalse(
            issues.Any(i => i.Original == "будит"),
            "the lexical pass is not supposed to be able to see this");
    }

    [TestMethod]
    public void RealWordFindings_AreNeverAutomaticallyApplicable()
    {
        var analyzer = Analyzer(contextual: true);
        if (!analyzer.ContextualRefinementEnabled)
        {
            Assert.Inconclusive("reranker not deployed next to the test binaries");
            return;
        }

        foreach (var issue in analyzer.Analyze(RealWordError).Where(i => i.RuleId == "ru.context.real-word"))
        {
            Assert.IsFalse(
                issue.CanApplyAutomatically,
                "a neural acceptability score must never silently rewrite a correctly "
                + "spelled word the user chose");
            Assert.AreEqual(IssueSeverity.Suggestion, issue.Severity);
            Assert.IsFalse(string.IsNullOrWhiteSpace(issue.Explanation), "every correction needs a reason");
        }
    }

    [TestMethod]
    public void CorrectSentences_GainNoIssuesFromTheContextualPass()
    {
        var analyzer = Analyzer(contextual: true);
        if (!analyzer.ContextualRefinementEnabled)
        {
            Assert.Inconclusive("reranker not deployed next to the test binaries");
            return;
        }

        // Wiring a contextual layer in is only an improvement if it stays quiet on text
        // that is already correct. False positives on correct writing are the failure
        // this product can least afford.
        foreach (var sentence in new[]
                 {
                     "Наша компания открыла новый офис в центре города.",
                     "Предвыборная кампания продлится до конца месяца.",
                     "Он должен прийти на встречу завтра утром.",
                     "Разработчик отправил изменения в репозиторий вчера вечером.",
                     "Мне нужно надеть тёплую куртку перед выходом.",
                 })
        {
            var contextualIssues = analyzer.Analyze(sentence)
                .Where(i => i.RuleId == "ru.context.real-word")
                .ToList();

            Assert.IsEmpty(
                contextualIssues,
                $"contextual pass flagged correct text \"{sentence}\": "
                + string.Join(", ", contextualIssues.Select(i => $"{i.Original}->{i.Replacement}")));
        }
    }

    [TestMethod]
    public void ContextualPass_CanBeTurnedOffForTheTypingPath()
    {
        // App.OnStartup and EditorPage both switch this off for their as-you-type
        // analyzers, so an ONNX session never lands on the keystroke path.
        var analyzer = Analyzer(contextual: true);
        analyzer.ContextualRefinementEnabled = false;
        Assert.IsFalse(analyzer.ContextualRefinementEnabled);

        // And back on, because the orchestrated path needs it.
        analyzer.ContextualRefinementEnabled = true;
        Assert.AreEqual(ContextualCorrectionRefiner.TryLoad() is not null, analyzer.ContextualRefinementEnabled);
    }
}
