using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace WriteLite.Services;

/// <summary>
/// Builds the wavy mark WriteLite draws under an issue.
/// </summary>
/// <remarks>
/// Shared by the in-app editor adorner and the inline overlay drawn on top of other
/// Windows applications, so a correction looks identical wherever it is shown.
/// </remarks>
public static class IssueWaveGeometry
{
    /// <summary>Distance from the text baseline box to the top of the wave.</summary>
    public const double BaselineOffset = 1.25;

    /// <summary>A wave spanning <paramref name="rect"/>, hanging just below its bottom edge.</summary>
    public static Geometry BuildWave(Rect rect, double waveHeight)
    {
        var y = Math.Max(1, rect.Bottom - BaselineOffset);
        var step = IssueUnderlineTheme.WaveStep;
        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(rect.Left, y), false, false);
            for (var x = rect.Left; x < rect.Right; x += step)
            {
                context.LineTo(new Point(Math.Min(x + step / 2, rect.Right), y + waveHeight), true, false);
                context.LineTo(new Point(Math.Min(x + step, rect.Right), y), true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Marker for a zero-length issue — a missing comma has no glyph to underline, so it
    /// gets a short vertical tick at the insertion point instead of a line across the field.
    /// </summary>
    public static Geometry BuildInsertionTick(Rect rect)
    {
        var x = rect.Left + 2;
        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(x, rect.Bottom - 7), false, false);
            context.LineTo(new Point(x, rect.Bottom - 1), true, false);
        }

        geometry.Freeze();
        return geometry;
    }
}
