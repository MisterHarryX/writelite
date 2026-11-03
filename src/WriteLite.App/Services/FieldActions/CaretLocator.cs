using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace WriteLite.Services.FieldActions;

/// <summary>Where the caret is in another application's field: offset into its text and a screen rectangle.</summary>
public readonly record struct CaretPosition(int Offset, Rect ScreenRect);

/// <summary>
/// Reads the caret of a field through UI Automation, off the UI thread and with a short timeout.
/// </summary>
/// <remarks>
/// A collapsed range usually reports no bounding rectangle, so the caret's place on screen is taken
/// from the character before it (its right edge) — or after it at the very start of a field.
/// Anything that cannot be read returns null; the prediction chip is simply not shown then.
/// </remarks>
public static class CaretLocator
{
    public static async Task<CaretPosition?> TryLocateAsync(AutomationElement element, string text, CancellationToken cancellationToken)
    {
        try
        {
            var read = Task.Run(() => Locate(element, text), cancellationToken);
            var done = await Task.WhenAny(read, Task.Delay(350, cancellationToken)).ConfigureAwait(false);
            return done == read ? await read.ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CompatibilityLogger.Technical("caret-locate-failed", $"type={ex.GetType().Name}");
            return null;
        }
    }

    private static CaretPosition? Locate(AutomationElement element, string text)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var obj) || obj is not TextPattern pattern) return null;
        var ranges = pattern.GetSelection();
        if (ranges is null || ranges.Length == 0) return null;
        var caret = ranges[0];
        if (!string.IsNullOrEmpty(caret.GetText(1))
            && caret.CompareEndpoints(TextPatternRangeEndpoint.Start, caret, TextPatternRangeEndpoint.End) != 0)
        {
            return null; // a selection, not a caret
        }

        var before = pattern.DocumentRange.Clone();
        before.MoveEndpointByRange(TextPatternRangeEndpoint.End, caret, TextPatternRangeEndpoint.Start);
        var prefix = before.GetText(-1) ?? string.Empty;
        var offset = Math.Min(prefix.Length, text.Length);

        // The character just before the caret gives its screen position.
        var probe = caret.Clone();
        probe.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1);
        var rects = probe.GetBoundingRectangles();
        if (rects is { Length: > 0 } && !rects[^1].IsEmpty)
        {
            var r = rects[^1];
            return new CaretPosition(offset, new Rect(r.Right, r.Top, 1, r.Height));
        }

        return null;
    }
}
