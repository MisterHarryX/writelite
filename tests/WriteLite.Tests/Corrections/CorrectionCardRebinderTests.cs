using WriteLite.Models;
using WriteLite.Services;

namespace WriteLite.Tests.Corrections;

/// <summary>
/// Following an open card across an edit the user made in another application — §4.
/// </summary>
[TestClass]
public sealed class CorrectionCardRebinderTests
{
    [TestMethod]
    public void AnEditAfterTheSpanLeavesItWhereItWas()
    {
        var mapped = CorrectionCardRebinder.MapSpan("роботает хорошо", "роботает хорошо!!", 0, 8);

        Assert.AreEqual((0, 8), mapped);
    }

    [TestMethod]
    public void AnEditBeforeTheSpanShiftsIt()
    {
        var mapped = CorrectionCardRebinder.MapSpan("он роботает", "а он роботает", 3, 8);

        Assert.AreEqual((5, 8), mapped);
    }

    [TestMethod]
    public void AnEditThatDeletesTextBeforeTheSpanShiftsItBack()
    {
        var mapped = CorrectionCardRebinder.MapSpan("а он роботает", "он роботает", 5, 8);

        Assert.AreEqual((3, 8), mapped);
    }

    [TestMethod]
    public void AnEditInsideTheSpanLosesIt()
    {
        // The characters the card was about are not there any more. There is nothing to
        // follow it to, and guessing is what put a wrong card back on screen.
        Assert.IsNull(CorrectionCardRebinder.MapSpan("роботает", "робатает", 0, 8));
        Assert.IsNull(CorrectionCardRebinder.MapSpan("роботает", "робот", 0, 8));
    }

    [TestMethod]
    public void RebindReturnsTheFindingTheNewAnalysisProduced()
    {
        var before = "он роботает";
        var open = Spelling(3, "роботает", "работает");
        var after = "а он роботает";

        // The new pass reports the same word at its new offset, with a different candidate.
        var fresh = Spelling(5, "роботает", "работал");

        var rebound = CorrectionCardRebinder.Rebind(before, open, after, [fresh]);

        Assert.IsNotNull(rebound);
        Assert.AreEqual("работал", rebound.Replacement, "the card must show what is true of the text now");
        Assert.AreEqual(5, rebound.Start);
    }

    [TestMethod]
    public void RebindClosesTheCardWhenTheNewAnalysisNoLongerReportsTheWord()
    {
        var rebound = CorrectionCardRebinder.Rebind(
            "он роботает",
            Spelling(3, "роботает", "работает"),
            "а он роботает",
            []);

        Assert.IsNull(rebound, "the finding is gone, so the card goes with it");
    }

    [TestMethod]
    public void RebindClosesTheCardWhenTheUserEditedTheWordItself()
    {
        var rebound = CorrectionCardRebinder.Rebind(
            "он роботает",
            Spelling(3, "роботает", "работает"),
            "он работает",
            []);

        Assert.IsNull(rebound);
    }

    [TestMethod]
    public void RebindRefusesAFindingOfADifferentKindOverTheSameCharacters()
    {
        var punctuation = Spelling(5, "роботает", "работает") with
        {
            Category = IssueCategory.Punctuation,
        };

        var rebound = CorrectionCardRebinder.Rebind(
            "он роботает",
            Spelling(3, "роботает", "работает"),
            "а он роботает",
            [punctuation]);

        Assert.IsNull(rebound, "a punctuation finding is not the spelling card the user opened");
    }

    private static TextIssue Spelling(int start, string original, string replacement) => new(
        start, original.Length, original, replacement,
        "Орфография", "Слово не найдено в русском орфографическом словаре.",
        IssueCategory.Orthography, IssueSeverity.Warning,
        CanApplyAutomatically: false, RuleId: "ru.spelling.typo");
}
