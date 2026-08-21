using WriteLite.AI.Local;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests;

[TestClass]
public sealed class IssueRenderingPipelineTests
{
    [TestMethod]
    public void CommonRussianTypo_RemainsAnExactInlineOrthographyIssue()
    {
        const string text = "Привет как тваи дела";
        var spelling = new SpellTextAnalyzer(new LocalSpellChecker()).Analyze(text);
        var filtered = new IssueRenderingPipeline().Filter(text, spelling);

        var issue = filtered.Single(candidate => candidate.Original == "тваи");
        Assert.AreEqual(text.IndexOf("тваи", StringComparison.Ordinal), issue.Start);
        Assert.AreEqual(4, issue.Length);
        Assert.AreEqual(IssueCategory.Orthography, issue.Category);
    }
    [TestMethod]
    public void ProtectedPath_IsNeverRenderedOrApplied()
    {
        const string text = @"Не изменяйте E:\WriteLite.Web.";
        var start = text.IndexOf(@"E:\WriteLite.Web", StringComparison.Ordinal);
        var issue = Issue(start, 12, @"E:\WriteLite", @"E:\WriteLite ", IssueCategory.Orthography, "spell");

        var filtered = new IssueRenderingPipeline().Filter(text, [issue]);

        Assert.IsEmpty(filtered);
        Assert.IsFalse(TextCorrectionService.TryApplyAll(text, filtered, true, out var changed));
        Assert.AreEqual(text, changed);
    }

    [TestMethod]
    public void ProtectedTokens_AreRestoredToTheirOwnPlaceholders()
    {
        const string text = @"test@example.com и E:\WriteLite.Web и 1.2.5";
        var masked = ProtectedSpanDetector.MaskProtected(text, out var map);
        var restored = ProtectedSpanDetector.UnmaskProtected(masked, map);
        Assert.AreEqual(text, restored);
    }

    [TestMethod]
    public void DuplicateRuleAndAiIssue_KeepDeterministicRule()
    {
        const string text = "холю";
        var deterministic = Issue(0, 4, text, "хочу", IssueCategory.Orthography, "spell", 1);
        var ai = Issue(0, 4, text, "хочу", IssueCategory.Orthography, "WL-AI-SPELLING", .96);

        var filtered = new IssueRenderingPipeline().Filter(text, [ai, deterministic]);

        Assert.HasCount(1, filtered);
        Assert.AreEqual("spell", filtered[0].RuleId);
    }

    [TestMethod]
    public void OverlappingDistinctIssues_RemainVisible()
    {
        const string text = "нужный Вещев";
        var spelling = Issue(7, 5, "Вещев", "вещей", IssueCategory.Orthography, "spell", .95);
        var style = Issue(0, text.Length, text, "нужных вещей", IssueCategory.Style, "WL-AI-STYLE", .95);

        var filtered = new IssueRenderingPipeline().Filter(text, [style, spelling]);

        Assert.HasCount(2, filtered);
        CollectionAssert.AreEquivalent(new[] { spelling, style }, filtered.ToArray());
    }

    [TestMethod]
    public void LowConfidenceGrammar_IsNotRendered()
    {
        const string text = "он могут";
        var issue = Issue(0, text.Length, text, "он может", IssueCategory.Grammar, "WL-AI-GRAMMAR", .55);
        Assert.IsEmpty(new IssueRenderingPipeline().Filter(text, [issue]));
    }

    [TestMethod]
    public void ExactManualSuggestion_IsClickableInline()
    {
        var issue = new TextIssue(
            0, 3, "текст", "замена", "Проверьте стиль", "style",
            IssueCategory.Style, IssueSeverity.Suggestion,
            CanApplyAutomatically: false, RuleId: "style.advisory", Confidence: 1);

        Assert.IsTrue(IssueRenderingPipeline.IsInlineCorrectionCandidate(issue));
    }

    [TestMethod]
    public void Utf16Range_CannotSplitEmojiSurrogatePair()
    {
        const string text = "A😀B";
        var issue = Issue(1, 1, text.Substring(1, 1), "x", IssueCategory.Orthography, "bad", 1);
        Assert.IsEmpty(new IssueRenderingPipeline().Filter(text, [issue]));
    }

    private static TextIssue Issue(
        int start,
        int length,
        string original,
        string replacement,
        IssueCategory category,
        string rule,
        double confidence = 1) =>
        new(start, length, original, replacement, category.ToString(), "test", category,
            IssueSeverity.Warning, true, rule, Confidence: confidence);
}
