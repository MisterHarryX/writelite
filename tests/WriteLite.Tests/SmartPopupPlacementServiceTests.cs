using System.Windows;
using WriteLite.Services;
using Size = System.Windows.Size;

namespace WriteLite.Tests;

[TestClass]
public sealed class SmartPopupPlacementServiceTests
{
    [TestMethod]
    public void IndicatorPrefersRightTopWhenSpaceExists()
    {
        var result = SmartPopupPlacementService.PlaceIndicator(
            new SmartPlacementContext(
                WorkArea: new Rect(0, 0, 1000, 800),
                FieldBounds: new Rect(100, 100, 300, 100)),
            new Size(78, 38));

        Assert.AreEqual(SmartPlacementSlot.RightTop, result.Slot);
        Assert.IsTrue(new Rect(0, 0, 1000, 800).Contains(new Rect(result.Location, result.Size)));
    }

    [TestMethod]
    public void PlacementNeverLeavesWorkArea()
    {
        var result = SmartPopupPlacementService.PlacePanel(
            new SmartPlacementContext(
                WorkArea: new Rect(0, 0, 1000, 800),
                FieldBounds: new Rect(900, 700, 80, 40)),
            new Size(410, 520));

        Assert.IsTrue(new Rect(0, 0, 1000, 800).Contains(new Rect(result.Location, result.Size)));
    }

    [TestMethod]
    public void AnchoredPopupPrefersBelowRange()
    {
        var result = SmartPopupPlacementService.PlaceAnchoredPopup(
            new SmartPlacementContext(
                WorkArea: new Rect(0, 0, 1000, 800),
                AnchorBounds: new Rect(220, 150, 44, 18)),
            new Size(332, 184));

        Assert.AreEqual(SmartPlacementSlot.BelowRange, result.Slot);
        Assert.AreEqual(176d, result.Location.Y);
    }

    [TestMethod]
    public void AvoidsCoveringCaretWhenPossible()
    {
        var field = new Rect(100, 100, 400, 200);
        var caret = new Rect(510, 110, 2, 18); // sits where right-top would land
        var withCaret = SmartPopupPlacementService.PlaceIndicator(
            new SmartPlacementContext(
                WorkArea: new Rect(0, 0, 1000, 800),
                FieldBounds: field,
                CaretBounds: caret),
            new Size(78, 38));

        var popup = new Rect(withCaret.Location, withCaret.Size);
        // Score prefers a non-intersecting candidate when available.
        Assert.IsFalse(popup.IntersectsWith(caret) && withCaret.Slot == SmartPlacementSlot.RightTop);
    }

    [TestMethod]
    public void MultiMonitorWorkAreaIsRespected()
    {
        // Secondary monitor to the left of primary (negative origin).
        var work = new Rect(-1920, 0, 1920, 1080);
        var result = SmartPopupPlacementService.PlaceAnchoredPopup(
            new SmartPlacementContext(
                WorkArea: work,
                AnchorBounds: new Rect(-400, 200, 40, 18)),
            new Size(300, 160));

        Assert.IsTrue(work.Contains(new Rect(result.Location, result.Size)));
        Assert.IsLessThan(0, result.Location.X);
    }

    [TestMethod]
    public void DpiScaleConvertsPhysicalToDip()
    {
        var scaled = SmartPopupPlacementService.Scale(new Rect(200, 100, 400, 200), 2, 2);
        Assert.AreEqual(new Rect(100, 50, 200, 100), scaled);
    }

    [TestMethod]
    public void LegacyFacadeStillReturnsRightWhenSpaceExists()
    {
        var result = OverlayPlacementService.PlaceIndicator(
            new Rect(100, 100, 300, 100),
            new Rect(0, 0, 1000, 800),
            new Size(78, 38));

        Assert.AreEqual(OverlayPlacement.Right, result.Placement);
    }

    [TestMethod]
    public void PopupDoesNotPreferHardCornerBinding()
    {
        // Plenty of space below the anchor — must not jump to a fixed screen corner.
        var result = SmartPopupPlacementService.PlaceAnchoredPopup(
            new SmartPlacementContext(
                WorkArea: new Rect(0, 0, 1400, 900),
                AnchorBounds: new Rect(500, 300, 50, 16)),
            new Size(320, 180));

        Assert.AreNotEqual(new Point(0, 0), result.Location);
        Assert.IsTrue(result.Location.Y >= 316);
    }

    [TestMethod]
    public void InsidePlacementIsNotChosenWhenExteriorFits()
    {
        var result = SmartPopupPlacementService.PlaceIndicator(
            new SmartPlacementContext(
                WorkArea: new Rect(0, 0, 1000, 800),
                FieldBounds: new Rect(100, 100, 300, 100)),
            new Size(78, 38));

        Assert.AreNotEqual(SmartPlacementSlot.InsideBottomRight, result.Slot);
    }

    [TestMethod]
    public void InsidePlacementRejectedWhenOverlapsCaret()
    {
        var field = new Rect(100, 100, 200, 80);
        var popup = new Rect(field.Right - 78 - 8, field.Bottom - 38 - 8, 78, 38);
        var caret = new Rect(popup.Left + 10, popup.Top + 5, 2, 16);
        Assert.IsFalse(SmartPopupPlacementService.IsInsidePlacementAllowed(
            popup,
            new SmartPlacementContext(
                WorkArea: new Rect(0, 0, 1000, 800),
                FieldBounds: field,
                CaretBounds: caret)));
    }

    [TestMethod]
    public void InsidePlacementRejectedWhenOverlapsAnchorWord()
    {
        var field = new Rect(100, 100, 200, 80);
        var popup = new Rect(field.Right - 78 - 8, field.Bottom - 38 - 8, 78, 38);
        var anchor = new Rect(popup.Left + 4, popup.Top + 4, 40, 16);
        Assert.IsFalse(SmartPopupPlacementService.IsInsidePlacementAllowed(
            popup,
            new SmartPlacementContext(
                WorkArea: new Rect(0, 0, 1000, 800),
                FieldBounds: field,
                AnchorBounds: anchor)));
    }

    [TestMethod]
    public void IndicatorNeverUsesInsideFieldFallback()
    {
        var field = new Rect(0, 0, 100, 100);
        var result = SmartPopupPlacementService.PlaceIndicator(
            new SmartPlacementContext(WorkArea: new Rect(0, 0, 100, 100), FieldBounds: field),
            new Size(78, 38));

        Assert.AreNotEqual(SmartPlacementSlot.InsideBottomRight, result.Slot);
    }
}
