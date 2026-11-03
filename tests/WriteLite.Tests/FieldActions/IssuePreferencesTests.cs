using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Rules;
using WriteLite.Services.Settings;

namespace WriteLite.Tests.FieldActions;

[TestClass]
[DoNotParallelize]
public sealed class IssuePreferencesTests
{
    [TestCleanup]
    public void Reset() => IssuePreferences.Apply(new WriteLiteAppSettings());

    private static TextIssue Issue(string ruleId, IssueCategory category = IssueCategory.Punctuation,
        LinguisticIssueCategory linguistic = LinguisticIssueCategory.PunctuationRecommendation) =>
        new(0, 0, string.Empty, ".", "t", "e", category, IssueSeverity.Suggestion,
            RuleId: ruleId, LinguisticCategory: linguistic);

    [TestMethod]
    public void DisabledTerminalMarkKindHidesOnlyThatRule()
    {
        IssuePreferences.Apply(new WriteLiteAppSettings { DisabledIssueKinds = ["terminal-mark"] });

        Assert.IsFalse(IssuePreferences.Allows(Issue("ru.punctuation.terminal.insert")));
        Assert.IsTrue(IssuePreferences.Allows(Issue("ru.punctuation.intro-word-comma")));
        CollectionAssert.Contains(IssuePreferences.DisabledEngineRules.ToList(), "PUNCTUATION_PARAGRAPH_END");
    }

    [TestMethod]
    public void EveryKindMatchesAtLeastItsOwnPrimaryRule()
    {
        foreach (var kind in IssueKindCatalog.All)
        {
            var first = kind.RulePrefixes[0].TrimEnd('*');
            if (first.EndsWith('-') || first.EndsWith('.')) first += "x";
            Assert.AreSame(kind, IssueKindCatalog.KindOf(Issue(first)) is { } k && k.Id == kind.Id ? kind : null,
                $"kind {kind.Id} does not match {first}");
        }
    }

    [TestMethod]
    public void CategorySwitchStillApplies()
    {
        IssuePreferences.Apply(new WriteLiteAppSettings { EnablePunctuation = false });
        Assert.IsFalse(IssuePreferences.Allows(Issue("ru.punctuation.intro-word-comma")));
        Assert.IsTrue(IssuePreferences.Allows(Issue("ru.spelling.typo", IssueCategory.Orthography, LinguisticIssueCategory.Typo)));
    }

    [TestMethod]
    public void FastLaneCompositeRespectsPreferences()
    {
        IssuePreferences.Apply(new WriteLiteAppSettings { DisabledIssueKinds = ["sentence-capital"] });
        var analyzer = new CompositeTextAnalyzer(new FixedAnalyzer(
            Issue("ru.orthography.sentence-capital", IssueCategory.Orthography, LinguisticIssueCategory.EndingError),
            Issue("ru.spelling.typo", IssueCategory.Orthography, LinguisticIssueCategory.Typo)));
        var result = analyzer.Analyze("привет мир");
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("ru.spelling.typo", result[0].RuleId);
    }

    private sealed class FixedAnalyzer(params TextIssue[] issues) : ITextAnalyzer
    {
        public IReadOnlyList<TextIssue> Analyze(string text) => issues;
        public Task<IReadOnlyList<TextIssue>> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TextIssue>>(issues);
    }
}
