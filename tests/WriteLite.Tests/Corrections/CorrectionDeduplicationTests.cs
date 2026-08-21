using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Rules;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Corrections;

/// <summary>
/// §16: one textual error is one card, and applying it does not leave a copy behind.
/// </summary>
[TestClass]
public sealed class CorrectionDeduplicationTests
{
    [TestMethod]
    public void TheRulePackAndTheDictionaryAgreeing_ProduceOneCard()
    {
        // «вообщем» is caught twice on purpose: by the curated correction table and by the
        // rule pack. Both propose exactly the same edit, and the user must see it once.
        var spellChecker = new LocalSpellChecker();
        var analyzer = new CompositeTextAnalyzer(
            new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), spellChecker.RussianFormIndex),
            new SpellTextAnalyzer(spellChecker) { ContextualRefinementEnabled = false });

        var raw = analyzer.Analyze("вообщем всё понятно");
        Assert.IsGreaterThan(1, raw.Count(IsAboutTheFirstWord), "expected the duplicate this test is about");

        var merged = new WriteLiteIssueMerger().Merge(raw).Issues;

        Assert.AreEqual(1, merged.Count(IsAboutTheFirstWord));
        var card = merged.Single(IsAboutTheFirstWord);
        Assert.AreEqual("вообщем", card.Original);
        Assert.AreEqual("в общем", card.Replacement);

        static bool IsAboutTheFirstWord(TextIssue issue) => issue.Start == 0 && issue.Length == 7;
    }

    [TestMethod]
    public void ApplyingACorrection_RemovesItAndKeepsTheRest()
    {
        // The user applies the second of two findings; the first must survive with its span
        // intact and the applied one must not come back.
        const string text = "Сечас программа роботает";
        IReadOnlyList<TextIssue> issues =
        [
            Spelling(0, "Сечас", "Сейчас"),
            Spelling(16, "роботает", "работает"),
        ];

        var rebased = IssueSetRebase.AfterReplacement(issues, start: 16, length: 8, replacementLength: 8);

        Assert.AreEqual(1, rebased.Count);
        Assert.AreEqual("Сечас", rebased[0].Original);

        var applied = text.Remove(16, 8).Insert(16, "работает");
        Assert.AreEqual("Сечас программа работает", applied);
        CollectionAssert.AreEquivalent(
            rebased.ToArray(), IssueSetRebase.ValidAgainst(rebased, applied).ToArray());
    }

    [TestMethod]
    public void AStaleFindingNeverSurvivesToTheCard()
    {
        // §19: the user fixed the word by hand while the popup was open.
        IReadOnlyList<TextIssue> issues = [Spelling(0, "роботает", "работает")];

        Assert.AreEqual(1, IssueSetRebase.ValidAgainst(issues, "роботает сегодня").Count);
        Assert.AreEqual(0, IssueSetRebase.ValidAgainst(issues, "работает сегодня").Count);
        Assert.AreEqual(0, IssueSetRebase.ValidAgainst(issues, "нет").Count);
    }

    [TestMethod]
    public void TwoAnalyzersOfferingDifferentAnswers_StayTwoCards()
    {
        // Deduplication must not become "one card per span": genuinely different corrections
        // for the same word are different offers and the user chooses.
        IReadOnlyList<TextIssue> issues =
        [
            Spelling(0, "превет", "привет") with { RuleId = "ru.spelling.typo" },
            Spelling(0, "превет", "прервёт") with { RuleId = "ru.rules.alternative" },
        ];

        var merged = new WriteLiteIssueMerger().Merge(issues).Issues;
        Assert.AreEqual(2, merged.Count);
    }

    private static TextIssue Spelling(int start, string original, string replacement) => new(
        start, original.Length, original, replacement,
        "Орфография", "Проверьте предложенный вариант.",
        IssueCategory.Orthography, IssueSeverity.Warning,
        CanApplyAutomatically: false, RuleId: "ru.spelling.typo");
}
