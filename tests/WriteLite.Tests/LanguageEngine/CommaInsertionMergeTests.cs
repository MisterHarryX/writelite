using WriteLite.Models;
using WriteLite.Services.LanguageEngine;

namespace WriteLite.Tests.LanguageEngine;

/// <summary>
/// One card per comma, whichever layers proposed it.
/// </summary>
/// <remarks>
/// Found by benchmark rather than by design. Adding «а» to the adversative comma rule made
/// WriteLite propose a comma LanguageTool had already proposed, and the merger kept both
/// because it compared spans: the rule reports a zero-length insertion after the preceding
/// word, the engine reports both words around the boundary rewritten with the comma between
/// them. Two shapes, one edit, two underlines on the user's screen.
/// </remarks>
[TestClass]
public sealed class CommaInsertionMergeTests
{
    private const string Sentence = "Мы поехали на дачу а они остались дома.";

    /// <summary>The built-in rule's shape: insert a comma after «дачу».</summary>
    private static TextIssue RuleFinding() => new(
        18, 0, string.Empty, ",",
        "Запятая перед противительным союзом",
        "Перед союзом «а» в середине предложения ставится запятая.",
        IssueCategory.Punctuation,
        IssueSeverity.Warning,
        CanApplyAutomatically: true,
        RuleId: "ru.punctuation.adversative-comma",
        LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation,
        Confidence: 0.86);

    /// <summary>The engine's shape: rewrite «дачу а» as «дачу, а».</summary>
    private static TextIssue EngineFinding() => new(
        14, 6, "дачу а", "дачу, а",
        "Пунктуация",
        "Возможно, здесь нужна запятая.",
        IssueCategory.Punctuation,
        IssueSeverity.Warning,
        CanApplyAutomatically: true,
        RuleId: "WL-COMMA_BEFORE_A",
        LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation,
        Confidence: 0.7);

    [TestMethod]
    public void SameCommaFromTwoLayers_ProducesOneFinding()
    {
        var merged = new WriteLiteIssueMerger().Merge([RuleFinding()], null, [EngineFinding()]);

        Assert.HasCount(1, merged.Issues);
        Assert.AreEqual(1, merged.DuplicateCount);
    }

    [TestMethod]
    public void TheSurvivingFinding_KeepsTheWiderSpanAndTheSpecificReason()
    {
        var merged = new WriteLiteIssueMerger().Merge([RuleFinding()], null, [EngineFinding()]);
        var issue = merged.Issues[0];

        // The span the user reads is the one carrying context, not a bare inserted comma.
        Assert.AreEqual(14, issue.Start);
        Assert.AreEqual(6, issue.Length);
        Assert.AreEqual("дачу, а", issue.Replacement);

        // The reason is WriteLite's own, not the engine's generic message.
        Assert.AreEqual("ru.punctuation.adversative-comma", issue.RuleId);
        StringAssert.Contains(issue.Explanation, "«а»");
    }

    [TestMethod]
    public void ApplyingTheMergedFinding_ProducesTheCorrectSentence()
    {
        var merged = new WriteLiteIssueMerger().Merge([RuleFinding()], null, [EngineFinding()]);
        var issue = merged.Issues[0];

        var applied = Sentence[..issue.Start] + issue.Replacement + Sentence[(issue.Start + issue.Length)..];

        Assert.AreEqual("Мы поехали на дачу, а они остались дома.", applied);
    }

    [TestMethod]
    public void CommasAtDifferentPositions_AreNotMerged()
    {
        var first = RuleFinding();
        var second = RuleFinding() with { Start = 25 };

        var merged = new WriteLiteIssueMerger().Merge([first, second]);

        Assert.HasCount(2, merged.Issues);
    }

    [TestMethod]
    public void AnEditThatIsNotAPureCommaInsertion_IsNotReduced()
    {
        // The reduction is defined as "same text, one comma more". An edit that changes
        // anything else about the span is a different claim and must survive on its own,
        // even when it overlaps and even when it happens to contain a comma.
        var comma = RuleFinding();
        var rewrite = EngineFinding() with
        {
            Replacement = "даче, а",
            RuleId = "WL-CASE",
            Category = IssueCategory.Grammar,
        };

        var merged = new WriteLiteIssueMerger().Merge([comma], null, [rewrite]);

        Assert.HasCount(2, merged.Issues);
    }

    [TestMethod]
    public void AGrammarFindingAtTheSameOffset_IsNotSwallowed()
    {
        // The reduction is defined for punctuation only: a grammar finding that happens to
        // add a comma-shaped character is a different claim about the text.
        var comma = RuleFinding();
        var grammar = RuleFinding() with { Category = IssueCategory.Grammar, RuleId = "ru.grammar.something" };

        var merged = new WriteLiteIssueMerger().Merge([comma], null, [grammar]);

        Assert.HasCount(2, merged.Issues);
    }
}
