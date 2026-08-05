using System.Windows;
using WriteLite.Models;

namespace WriteLite.Services;

/// <summary>Screen-space geometry obtained from UI Automation for one current issue.</summary>
public sealed record InlineIssueGeometry(TextIssue Issue, IReadOnlyList<Rect> Rectangles)
{
    public bool HasVisibleRectangles => Rectangles.Count > 0;
}

public sealed class InlineIssueClickedEventArgs : EventArgs
{
    public InlineIssueClickedEventArgs(TextIssue issue, Rect anchor) { Issue = issue; Anchor = anchor; }
    public TextIssue Issue { get; }
    public Rect Anchor { get; }
}

public static class InlineIssueGeometryBuilder
{
    /// <summary>
    /// Builds geometries only for issues with at least one valid, finite rectangle.
    /// Empty / field-wide fallback rectangles are rejected so we never paint a long
    /// decoration across the entire control when precise UIA coordinates are missing.
    /// </summary>
    public static IReadOnlyList<InlineIssueGeometry> Build(
        IReadOnlyList<TextIssue> issues,
        IReadOnlyList<IReadOnlyList<Rect>> rectangles,
        Rect? fieldBounds = null)
    {
        var result = new List<InlineIssueGeometry>(issues.Count);
        for (var index = 0; index < issues.Count; index++)
        {
            var rects = index < rectangles.Count ? rectangles[index] : [];
            var precise = FilterPreciseRectangles(rects, fieldBounds);
            if (precise.Count > 0)
            {
                result.Add(new InlineIssueGeometry(issues[index], precise));
            }
        }

        return result;
    }

    public static IReadOnlyList<Rect> FilterPreciseRectangles(IReadOnlyList<Rect> rectangles, Rect? fieldBounds = null)
    {
        if (rectangles.Count == 0) return [];

        var filtered = new List<Rect>(rectangles.Count);
        foreach (var rect in rectangles)
        {
            if (!IsPreciseRangeRectangle(rect, fieldBounds)) continue;
            filtered.Add(rect);
        }

        return filtered;
    }

    /// <summary>
    /// Reject empty, infinite, or field-spanning rectangles that would produce false geometry.
    /// </summary>
    public static bool IsPreciseRangeRectangle(Rect rect, Rect? fieldBounds = null)
    {
        if (rect.IsEmpty) return false;
        if (double.IsNaN(rect.X) || double.IsNaN(rect.Y) || double.IsNaN(rect.Width) || double.IsNaN(rect.Height))
            return false;
        if (double.IsInfinity(rect.X) || double.IsInfinity(rect.Y) || double.IsInfinity(rect.Width) || double.IsInfinity(rect.Height))
            return false;
        if (rect.Width < 0.5 || rect.Height < 0.5) return false;

        if (fieldBounds is Rect field && !field.IsEmpty && field.Width > 0)
        {
            // A rectangle covering nearly the entire field is not a precise word range.
            if (rect.Width >= field.Width * 0.92 && rect.Height >= Math.Min(field.Height, rect.Height) * 0.5)
            {
                return false;
            }
        }

        return true;
    }

    public static Rect CreateInsertionMarker(Rect anchor)
    {
        var height = Math.Max(anchor.Height, 12);
        return new Rect(anchor.Left - 2, anchor.Bottom - height, 5, height);
    }
}
