using System.Windows;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace WriteLite.Services;

/// <summary>
/// Shared multi-candidate placement for the indicator, suggestions panel,
/// correction popup, and lexical card. DPI-aware via pre-scaled inputs.
/// </summary>
public enum SmartPlacementSlot
{
    RightTop,
    RightBottom,
    LeftTop,
    LeftBottom,
    AboveRange,
    BelowRange,
    AboveField,
    BelowField,
    InsideBottomRight,
    Clamped
}

public sealed record SmartPlacementContext(
    Rect WorkArea,
    Rect? FieldBounds = null,
    Rect? AnchorBounds = null,
    Rect? CaretBounds = null,
    Rect? HostWindowBounds = null,
    double? Gap = null,
    double? MinEdgeMargin = null)
{
    /// <summary>Разрыв (в px) от привязки; по умолчанию из единого конфига.</summary>
    public double GapOrDefault => Gap ?? WriteLiteDefaults.Ui.SmartPlacementGapPx;

    /// <summary>Минимальный отступ от края рабочей области; по умолчанию из конфига.</summary>
    public double MinEdgeMarginOrDefault => MinEdgeMargin ?? WriteLiteDefaults.Ui.SmartPlacementMinEdgeMarginPx;
}

public sealed record SmartPlacementResult(
    Point Location,
    SmartPlacementSlot Slot,
    Size Size,
    double Score,
    OverlayPlacement LegacyPlacement);

public static class SmartPopupPlacementService
{
    /// <summary>Indicator near the active field; prefers right/top when space allows.</summary>
    public static SmartPlacementResult PlaceIndicator(SmartPlacementContext context, Size size)
        // An indicator is supplementary UI, never a reason to cover the text the
        // user is editing.  If Windows leaves no exterior slot, callers can hide it
        // rather than accepting an inside-field fallback.
        => Place(context, size, PreferIndicator, allowInside: false);

    /// <summary>Suggestions / corrections panel near the field.</summary>
    public static SmartPlacementResult PlacePanel(SmartPlacementContext context, Size requested)
    {
        var size = ClampSize(requested, context.WorkArea);
        return Place(context, size, PreferPanel, allowInside: false);
    }

    /// <summary>Correction or lexical popup near a word/error anchor.</summary>
    public static SmartPlacementResult PlaceAnchoredPopup(SmartPlacementContext context, Size requested)
    {
        var size = ClampSize(requested, context.WorkArea);
        return Place(context, size, PreferAnchoredPopup, allowInside: false);
    }

    public static OverlayPlacementResult ToLegacy(SmartPlacementResult result)
        => new(result.Location, result.LegacyPlacement, result.Size);

    public static Rect Scale(Rect rect, double scaleX, double scaleY)
        => new(rect.X / scaleX, rect.Y / scaleY, rect.Width / scaleX, rect.Height / scaleY);

    public static Point ClampToWorkArea(Point location, Size size, Rect workArea)
        => new(
            Math.Min(Math.Max(location.X, workArea.Left), Math.Max(workArea.Left, workArea.Right - size.Width)),
            Math.Min(Math.Max(location.Y, workArea.Top), Math.Max(workArea.Top, workArea.Bottom - size.Height)));

    /// <summary>
    /// Inside-field placement is a last-resort only: never when it covers caret,
    /// selected/error anchor, or a substantial portion of the field, and never when
    /// any safe exterior candidate fits the work area.
    /// </summary>
    public static bool IsInsidePlacementAllowed(Rect popup, SmartPlacementContext context)
    {
        if (context.CaretBounds is Rect caret && !caret.IsEmpty && popup.IntersectsWith(caret))
            return false;

        if (context.AnchorBounds is Rect anchor && !anchor.IsEmpty && popup.IntersectsWith(anchor))
            return false;

        if (context.FieldBounds is Rect field && !field.IsEmpty && field.Width > 0 && field.Height > 0)
        {
            var overlap = IntersectionArea(popup, field);
            var fieldArea = field.Width * field.Height;
            if (fieldArea > 0 && overlap / fieldArea > WriteLiteDefaults.SmartPlacementScoring.MaxFieldOverlapRatio)
                return false;
        }

        return true;
    }

    private static SmartPlacementResult Place(
        SmartPlacementContext context,
        Size size,
        Func<SmartPlacementSlot, int> preferenceRank,
        bool allowInside)
    {
        var work = context.WorkArea;
        if (work.Width <= 0 || work.Height <= 0)
        {
            return new SmartPlacementResult(new Point(0, 0), SmartPlacementSlot.Clamped, size, 0, OverlayPlacement.Right);
        }

        size = ClampSize(size, work);
        var field = context.FieldBounds ?? context.AnchorBounds ?? Rect.Empty;
        var anchor = context.AnchorBounds ?? field;
        var gap = context.GapOrDefault;

        var candidates = BuildCandidates(field, anchor, size, gap, includeInside: false);
        SmartPlacementResult? bestExterior = null;

        foreach (var (slot, location, legacy) in candidates)
        {
            var rect = new Rect(location, size);
            var score = ScoreCandidate(rect, slot, context, preferenceRank(slot));
            if (bestExterior is null || score > bestExterior.Score)
            {
                bestExterior = new SmartPlacementResult(location, slot, size, score, legacy);
            }
        }

        // Exterior candidate that fully fits the work area wins over any inside placement.
        if (bestExterior is not null && bestExterior.Score >= 0 && work.Contains(new Rect(bestExterior.Location, size)))
        {
            return bestExterior;
        }

        if (allowInside && !field.IsEmpty)
        {
            var insideLocation = new Point(
                field.Right - size.Width - gap,
                field.Bottom - size.Height - gap);
            var insideRect = new Rect(insideLocation, size);
            if (work.Contains(insideRect) && IsInsidePlacementAllowed(insideRect, context))
            {
                var insideScore = ScoreCandidate(insideRect, SmartPlacementSlot.InsideBottomRight, context, 0);
                // Only accept inside when no exterior option scored as viable.
                if (bestExterior is null || bestExterior.Score < 0)
                {
                    return new SmartPlacementResult(
                        insideLocation,
                        SmartPlacementSlot.InsideBottomRight,
                        size,
                        insideScore,
                        OverlayPlacement.InsideBottomRight);
                }
            }
        }

        if (bestExterior is null || bestExterior.Score < 0)
        {
            var fallbackOrigin = anchor.IsEmpty
                ? new Point(work.Left + context.MinEdgeMarginOrDefault, work.Top + context.MinEdgeMarginOrDefault)
                : new Point(anchor.Left, anchor.Bottom + gap);
            var clamped = ClampToWorkArea(fallbackOrigin, size, InflateInner(work, context.MinEdgeMarginOrDefault));
            return new SmartPlacementResult(clamped, SmartPlacementSlot.Clamped, size, 0, OverlayPlacement.Below);
        }

        if (!work.Contains(new Rect(bestExterior.Location, size)))
        {
            var clamped = ClampToWorkArea(bestExterior.Location, size, InflateInner(work, context.MinEdgeMarginOrDefault));
            return bestExterior with { Location = clamped, Slot = SmartPlacementSlot.Clamped, LegacyPlacement = OverlayPlacement.Below };
        }

        return bestExterior;
    }

    private static List<(SmartPlacementSlot Slot, Point Location, OverlayPlacement Legacy)> BuildCandidates(
        Rect field,
        Rect anchor,
        Size size,
        double gap,
        bool includeInside)
    {
        var list = new List<(SmartPlacementSlot, Point, OverlayPlacement)>(12);
        var hasField = !field.IsEmpty && field.Width > 0 && field.Height > 0;
        var hasAnchor = !anchor.IsEmpty && (anchor.Width > 0 || anchor.Height > 0);

        if (hasField)
        {
            list.Add((SmartPlacementSlot.RightTop, new Point(field.Right + gap, field.Top), OverlayPlacement.Right));
            list.Add((SmartPlacementSlot.RightBottom, new Point(field.Right + gap, field.Bottom - size.Height), OverlayPlacement.Right));
            list.Add((SmartPlacementSlot.LeftTop, new Point(field.Left - size.Width - gap, field.Top), OverlayPlacement.Left));
            list.Add((SmartPlacementSlot.LeftBottom, new Point(field.Left - size.Width - gap, field.Bottom - size.Height), OverlayPlacement.Left));
            list.Add((SmartPlacementSlot.AboveField, new Point(field.Left, field.Top - size.Height - gap), OverlayPlacement.Above));
            list.Add((SmartPlacementSlot.BelowField, new Point(field.Left, field.Bottom + gap), OverlayPlacement.Below));
            if (includeInside)
            {
                list.Add((SmartPlacementSlot.InsideBottomRight,
                    new Point(field.Right - size.Width - gap, field.Bottom - size.Height - gap),
                    OverlayPlacement.InsideBottomRight));
            }
        }

        if (hasAnchor)
        {
            list.Add((SmartPlacementSlot.BelowRange, new Point(anchor.Left, anchor.Bottom + gap), OverlayPlacement.Below));
            list.Add((SmartPlacementSlot.AboveRange, new Point(anchor.Left, anchor.Top - size.Height - gap), OverlayPlacement.Above));
            list.Add((SmartPlacementSlot.RightTop, new Point(anchor.Right + gap, anchor.Top), OverlayPlacement.Right));
            list.Add((SmartPlacementSlot.LeftTop, new Point(anchor.Left - size.Width - gap, anchor.Top), OverlayPlacement.Left));
        }

        if (list.Count == 0)
        {
            list.Add((SmartPlacementSlot.BelowRange, new Point(0, 0), OverlayPlacement.Below));
        }

        return list;
    }

    private static double ScoreCandidate(
        Rect popup,
        SmartPlacementSlot slot,
        SmartPlacementContext context,
        int preferenceRank)
    {
        var work = context.WorkArea;
        if (popup.Width <= 0 || popup.Height <= 0) return double.NegativeInfinity;

        if (!work.Contains(popup)) return -WriteLiteDefaults.SmartPlacementScoring.OutsideFieldFallbackRankOffset + preferenceRank;

        var score = WriteLiteDefaults.Ui.SmartPlacementBaseScore + preferenceRank * WriteLiteDefaults.Ui.SmartPlacementPreferenceRankStep;

        var field = context.FieldBounds;
        if (field is Rect f && !f.IsEmpty)
        {
            var overlap = IntersectionArea(popup, f);
            score -= overlap * WriteLiteDefaults.SmartPlacementScoring.FieldOverlapPenalty;
            score -= DistanceBetween(popup, f) * WriteLiteDefaults.SmartPlacementScoring.FieldDistancePenalty;
        }

        var anchor = context.AnchorBounds;
        if (anchor is Rect a && !a.IsEmpty)
        {
            var anchorOverlap = IntersectionArea(popup, a);
            score -= anchorOverlap * WriteLiteDefaults.SmartPlacementScoring.AnchorOverlapPenalty;
            score -= DistanceBetween(popup, a) * WriteLiteDefaults.SmartPlacementScoring.AnchorDistancePenalty;
        }

        if (context.CaretBounds is Rect caret && !caret.IsEmpty && popup.IntersectsWith(caret))
        {
            score -= WriteLiteDefaults.SmartPlacementScoring.CaretIntersectionPenalty;
        }

        if (context.HostWindowBounds is Rect host && !host.IsEmpty)
        {
            var hostOverlap = IntersectionArea(popup, host);
            score += Math.Min(hostOverlap, popup.Width * popup.Height) * WriteLiteDefaults.SmartPlacementScoring.HostOverlapBonus;
        }

        if (slot == SmartPlacementSlot.InsideBottomRight)
        {
            score -= WriteLiteDefaults.SmartPlacementScoring.InsideBottomRightPenalty; // heavy penalty — last resort only
            if (!IsInsidePlacementAllowed(popup, context))
                return -WriteLiteDefaults.SmartPlacementScoring.OutsideFieldFallbackRankOffset;
        }

        return score;
    }

    private static int PreferIndicator(SmartPlacementSlot slot) => slot switch
    {
        SmartPlacementSlot.RightTop => 8,
        SmartPlacementSlot.RightBottom => 7,
        SmartPlacementSlot.LeftTop => 6,
        SmartPlacementSlot.LeftBottom => 5,
        SmartPlacementSlot.AboveField => 4,
        SmartPlacementSlot.BelowField => 3,
        SmartPlacementSlot.InsideBottomRight => 0,
        _ => 1
    };

    private static int PreferPanel(SmartPlacementSlot slot) => slot switch
    {
        SmartPlacementSlot.RightTop => 8,
        SmartPlacementSlot.LeftTop => 7,
        SmartPlacementSlot.AboveField => 6,
        SmartPlacementSlot.BelowField => 5,
        SmartPlacementSlot.RightBottom => 4,
        SmartPlacementSlot.LeftBottom => 3,
        SmartPlacementSlot.InsideBottomRight => 0,
        _ => 1
    };

    private static int PreferAnchoredPopup(SmartPlacementSlot slot) => slot switch
    {
        SmartPlacementSlot.BelowRange => 9,
        SmartPlacementSlot.AboveRange => 8,
        SmartPlacementSlot.RightTop => 6,
        SmartPlacementSlot.LeftTop => 5,
        SmartPlacementSlot.BelowField => 4,
        SmartPlacementSlot.AboveField => 3,
        SmartPlacementSlot.InsideBottomRight => 0,
        _ => 1
    };

    private static Size ClampSize(Size requested, Rect workArea)
        => new(
            Math.Min(requested.Width, Math.Max(1, workArea.Width)),
            Math.Min(requested.Height, Math.Max(1, workArea.Height)));

    private static Rect InflateInner(Rect work, double margin)
    {
        if (work.Width <= margin * 2 || work.Height <= margin * 2) return work;
        return new Rect(work.Left + margin, work.Top + margin, work.Width - margin * 2, work.Height - margin * 2);
    }

    private static double IntersectionArea(Rect a, Rect b)
    {
        var x = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        var y = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        return x * y;
    }

    private static double DistanceBetween(Rect a, Rect b)
    {
        var dx = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right));
        var dy = Math.Max(0, Math.Max(a.Top - b.Bottom, b.Top - a.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
