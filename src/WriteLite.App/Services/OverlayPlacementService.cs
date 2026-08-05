using System.Windows;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace WriteLite.Services;

public enum OverlayPlacement
{
    Right,
    Left,
    Above,
    Below,
    InsideBottomRight
}

public sealed record OverlayPlacementResult(Point Location, OverlayPlacement Placement, Size Size);

/// <summary>
/// Backward-compatible façade over <see cref="SmartPopupPlacementService"/>.
/// Existing call sites and tests keep working while gaining multi-candidate scoring.
/// </summary>
public static class OverlayPlacementService
{
    private const double Gap = 8;

    public static OverlayPlacementResult PlaceIndicator(Rect field, Rect workArea, Size indicator, Rect? caret = null)
    {
        var result = SmartPopupPlacementService.PlaceIndicator(
            new SmartPlacementContext(workArea, FieldBounds: field, CaretBounds: caret, Gap: Gap),
            indicator);
        return SmartPopupPlacementService.ToLegacy(result);
    }

    public static OverlayPlacementResult PlacePanel(Rect field, Rect workArea, Size requestedPanel, Rect? caret = null)
    {
        var result = SmartPopupPlacementService.PlacePanel(
            new SmartPlacementContext(workArea, FieldBounds: field, CaretBounds: caret, Gap: Gap),
            requestedPanel);
        return SmartPopupPlacementService.ToLegacy(result);
    }

    public static OverlayPlacementResult PlaceCorrectionPopup(Rect anchor, Rect workArea, Size requestedPopup)
    {
        var result = SmartPopupPlacementService.PlaceAnchoredPopup(
            new SmartPlacementContext(workArea, AnchorBounds: anchor, FieldBounds: null, Gap: Gap),
            requestedPopup);
        return SmartPopupPlacementService.ToLegacy(result);
    }

    public static Rect Scale(Rect rect, double scaleX, double scaleY)
        => SmartPopupPlacementService.Scale(rect, scaleX, scaleY);
}
