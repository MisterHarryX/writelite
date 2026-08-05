using System.Windows;
using System.Windows.Media;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Views;
using Size = System.Windows.Size;

namespace WriteLite.Tests;

[TestClass]
public sealed class SystemLayerTests
{
    [TestMethod]
    public void OverlayUsesSelectiveNcHitTest()
    {
        Assert.AreEqual(1, InlineErrorOverlayWindow.ResolveNcHitTest(true));
        Assert.AreEqual(-1, InlineErrorOverlayWindow.ResolveNcHitTest(false));
    }

    [TestMethod]
    public void ExactErrorHitAreaDoesNotCoverNeighbouringWord()
    {
        var hit = InlineErrorOverlayWindow.CreateHitArea(new Rect(100, 100, 30, 16), insertion: false);

        Assert.IsTrue(hit.Contains(new Point(115, 101)));
        Assert.IsTrue(hit.Contains(new Point(115, 110)));
        Assert.IsFalse(hit.Contains(new Point(145, 110)));
    }

    [TestMethod]
    public void InsertionMarkerHasClickableHitArea()
    {
        var hit = InlineErrorOverlayWindow.CreateHitArea(new Rect(100, 100, 5, 16), insertion: true);

        Assert.IsTrue(hit.Contains(new Point(101, 108)));
        Assert.IsFalse(hit.Contains(new Point(120, 108)));
    }

    [TestMethod]
    public void TargetChangeIncrementsGenerationAndClearsOldTarget()
    {
        var manager = new ActiveTextTargetManager();

        Assert.IsTrue(manager.UpdateIdentityForTest(Target(1, 10)));
        var firstGeneration = manager.GenerationId;

        Assert.IsFalse(manager.UpdateIdentityForTest(Target(1, 10)));
        Assert.AreEqual(firstGeneration, manager.GenerationId);

        Assert.IsTrue(manager.UpdateIdentityForTest(Target(2, 20)));
        Assert.IsGreaterThan(firstGeneration, manager.GenerationId);
        Assert.IsNull(manager.CurrentTarget);
    }

    [TestMethod]
    public void SwitchingBetweenTwentyFieldsDoesNotKeepOldTarget()
    {
        var manager = new ActiveTextTargetManager();

        for (var i = 0; i < 20; i++)
        {
            Assert.IsTrue(manager.UpdateIdentityForTest(Target(i + 1, i + 100)));
            Assert.IsNull(manager.CurrentTarget);
        }

        Assert.AreEqual(20, manager.GenerationId);
    }

    [TestMethod]
    public void RightPlacementIsPreferredWhenSpaceExists()
    {
        var result = OverlayPlacementService.PlaceIndicator(
            new Rect(100, 100, 300, 100),
            new Rect(0, 0, 1000, 800),
            new Size(78, 38));

        Assert.AreEqual(OverlayPlacement.Right, result.Placement);
    }

    [TestMethod]
    public void LeftPlacementIsUsedWhenRightSideHasNoSpace()
    {
        var result = OverlayPlacementService.PlaceIndicator(
            new Rect(900, 100, 90, 100),
            new Rect(0, 0, 1000, 800),
            new Size(78, 38));

        Assert.AreEqual(OverlayPlacement.Left, result.Placement);
    }

    [TestMethod]
    public void PanelStaysInsideWorkArea()
    {
        var result = OverlayPlacementService.PlacePanel(
            new Rect(900, 700, 80, 40),
            new Rect(0, 0, 1000, 800),
            new Size(410, 520));

        var panel = new Rect(result.Location, result.Size);
        Assert.IsTrue(new Rect(0, 0, 1000, 800).Contains(panel));
    }

    [TestMethod]
    public void DpiCoordinatesAreConvertedToWpfUnits()
    {
        var scaled = OverlayPlacementService.Scale(new Rect(200, 100, 400, 200), 2, 2);

        Assert.AreEqual(new Rect(100, 50, 200, 100), scaled);
    }

    [TestMethod]
    public void InlinePresentationUsesDistinctRequiredUnderlineColors()
    {
        Assert.AreEqual(IssueUnderlineTheme.Orthography, InlineIssuePresentation.ColorFor(IssueCategory.Orthography));
        Assert.AreEqual(IssueUnderlineTheme.Punctuation, InlineIssuePresentation.ColorFor(IssueCategory.Punctuation));
        Assert.AreEqual(IssueUnderlineTheme.Grammar, InlineIssuePresentation.ColorFor(IssueCategory.Grammar));
        Assert.AreEqual(IssueUnderlineTheme.Style, InlineIssuePresentation.ColorFor(IssueCategory.Style));
        Assert.AreEqual(IssueUnderlineTheme.DefaultThickness, InlineIssuePresentation.UnderlineThickness);
        Assert.AreNotEqual(
            InlineIssuePresentation.ColorFor(IssueCategory.Orthography),
            InlineIssuePresentation.ColorFor(IssueCategory.Grammar));
    }

    [TestMethod]
    public void StaticOrReadOnlyTextIsNotAnEditableTarget()
    {
        Assert.IsFalse(EditableTextTargetPolicy.IsEditableTextTarget(false, true, false, true, true, true, false, false));
        Assert.IsFalse(EditableTextTargetPolicy.IsEditableTextTarget(false, true, true, true, true, false, false, false));
        Assert.IsFalse(EditableTextTargetPolicy.IsEditableTextTarget(false, true, true, true, true, true, false, false));
        Assert.IsFalse(EditableTextTargetPolicy.IsEditableTextTarget(false, true, true, true, true, true, false, true));
        Assert.IsTrue(EditableTextTargetPolicy.IsEditableTextTarget(true, true, true, true, true, true, false, false));
    }

    [TestMethod]
    public void CorrectionPopupIsAnchoredAndKeptInsideWorkArea()
    {
        var workArea = new Rect(0, 0, 1000, 800);
        var result = CorrectionPopupWindow.CalculatePlacement(new Rect(900, 700, 40, 20), workArea, new Size(286, 180));
        Assert.IsTrue(workArea.Contains(new Rect(result.Location, result.Size)));
        Assert.AreNotEqual(new Point(0, 0), result.Location);
    }

    [TestMethod]
    public void CorrectionPopupPrefersTheAreaBelowTheClickedError()
    {
        var result = CorrectionPopupWindow.CalculatePlacement(
            new Rect(220, 150, 44, 18),
            new Rect(0, 0, 1000, 800),
        new Size(332, 184));

        Assert.AreEqual(OverlayPlacement.Below, result.Placement);
        Assert.AreEqual(176d, result.Location.Y);
    }

    [TestMethod]
    public void ZeroLengthIssueHasSmallInsertionMarker()
    {
        var marker = InlineIssueGeometryBuilder.CreateInsertionMarker(new Rect(100, 100, 20, 18));
        Assert.IsLessThanOrEqualTo(6d, marker.Width);
        Assert.AreEqual(98d, marker.Left);
    }

    [TestMethod]
    public void ApplyAllUsesRangesFromEnd()
    {
        var issues = new[]
        {
            Issue(0, 3, "aaa", "x"),
            Issue(4, 3, "bbb", "y")
        };

        Assert.IsTrue(TextCorrectionService.TryApplyAll("aaa bbb", issues, true, out var result));
        Assert.AreEqual("x y", result);
    }

    [TestMethod]
    public void OverlappingCorrectionsAreNotAppliedTwice()
    {
        var issues = new[]
        {
            Issue(0, 5, "abcde", "X"),
            Issue(2, 3, "cde", "Y")
        };

        Assert.IsTrue(TextCorrectionService.TryApplyAll("abcde", issues, true, out var result));
        Assert.AreEqual("abY", result);
    }

    [TestMethod]
    public void StaleCorrectionIsRejectedWhenTextChanged()
    {
        var issue = Issue(0, 6, "пливет", "привет");

        Assert.IsFalse(TextCorrectionService.TryApplySingle("привет", issue, true, out var result));
        Assert.AreEqual("привет", result);
    }

    [TestMethod]
    public void InformationalPunctuationHintCannotBeApplied()
    {
        var issue = new TextIssue(
            5,
            4,
            "если",
            null,
            "Info",
            "No safe replacement.",
            IssueCategory.Punctuation,
            IssueSeverity.Suggestion,
            CanApplyAutomatically: false);

        Assert.IsFalse(TextCorrectionService.CanApply(issue, "тест если", true));
    }

    [TestMethod]
    public void UserSelectedVariantCanApplyWhenAutomaticSafetyIsFalse()
    {
        var issue = new TextIssue(0, 3, "bad", "good", "Spelling", "Fix", IssueCategory.Orthography,
            IssueSeverity.Warning, CanApplyAutomatically: false);

        Assert.IsFalse(TextCorrectionService.CanApply(issue, "bad", true));
        Assert.IsTrue(TextCorrectionService.TryApplyManual("bad", issue, true, out var result, out _));
        Assert.AreEqual("good", result);
    }

    [TestMethod]
    public void GeometryBuilderKeepsSeparateExactIssueRanges()
    {
        var issues = new[] { Issue(0, 3, "one", "1"), Issue(4, 3, "two", "2") };
        var geometry = InlineIssueGeometryBuilder.Build(issues,
            new IReadOnlyList<Rect>[] { [new Rect(10, 10, 20, 12)], [new Rect(35, 10, 20, 12)] });

        Assert.AreEqual(2, geometry.Count);
        Assert.AreEqual(10d, geometry[0].Rectangles[0].Left);
        Assert.AreEqual(35d, geometry[1].Rectangles[0].Left);
    }

    [TestMethod]
    public void DoubleClickGateRejectsSecondWrite()
    {
        var gate = new AsyncOperationGate();

        Assert.IsTrue(gate.TryEnter());
        Assert.IsFalse(gate.TryEnter());
    }

    private static ActiveTextTargetIdentity Target(int processId, int handle)
    {
        return new ActiveTextTargetIdentity(
            processId,
            handle,
            processId + "." + handle,
            "ControlType.Edit",
            "editor-" + handle,
            "test");
    }

    private static TextIssue Issue(int start, int length, string original, string replacement)
    {
        return new TextIssue(
            start,
            length,
            original,
            replacement,
            "Issue",
            "Explanation",
            IssueCategory.Orthography,
            IssueSeverity.Error);
    }
}
