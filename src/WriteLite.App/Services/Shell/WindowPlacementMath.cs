namespace WriteLite.Services.Shell;

/// <summary>A rectangle in physical device pixels, the way Win32 hands them over.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public static readonly PixelRect Empty = new(0, 0, 0, 0);
}

/// <summary>Where and how large a maximised window should be, relative to its monitor's origin.</summary>
public readonly record struct MaximizedPlacement(int X, int Y, int Width, int Height);

/// <summary>Which screen edge an auto-hiding taskbar lives on, if any.</summary>
public enum ScreenEdge
{
    None,
    Left,
    Top,
    Right,
    Bottom
}

/// <summary>
/// The arithmetic behind a maximised or fullscreen window.
/// </summary>
/// <remarks>
/// Split out from the window itself so it can be tested without a desktop: every
/// value here arrives from <c>GetMonitorInfo</c>, and the bug this exists to fix
/// was a sizing mistake, not a rendering one.
///
/// The mistake: a chromeless window (<c>WindowStyle=None</c> plus
/// <see cref="System.Windows.Shell.WindowChrome"/>) keeps <c>WS_THICKFRAME</c>, so
/// Windows maximises it to the whole monitor inflated by the resize frame — on a
/// 1920×1080 screen with a 48 px taskbar the window ran from −7,−7 to 1927,1087
/// while the work area ended at 1032. Because <c>WM_NCCALCSIZE</c> has already been
/// told there is no non-client area, all 55 px below the work area are live UI, and
/// the bottom of the application sits under the taskbar. Answering
/// <c>WM_GETMINMAXINFO</c> with the work area is the fix; a negative margin would
/// only move the symptom.
/// </remarks>
public static class WindowPlacementMath
{
    /// <summary>
    /// Placement for a normally maximised window: exactly the monitor's work area.
    /// </summary>
    /// <remarks>
    /// <c>WM_GETMINMAXINFO</c> wants monitor-relative coordinates, so the monitor's
    /// own origin is subtracted — on a secondary screen placed left of the primary
    /// both rectangles have negative absolute coordinates and only their difference
    /// is meaningful.
    /// </remarks>
    public static MaximizedPlacement ForWorkArea(PixelRect monitor, PixelRect work, ScreenEdge autoHideEdge = ScreenEdge.None)
    {
        var x = work.Left - monitor.Left;
        var y = work.Top - monitor.Top;
        var width = Math.Max(1, work.Width);
        var height = Math.Max(1, work.Height);

        // An auto-hiding taskbar is not subtracted from the work area, so a window
        // that fills it covers the reserved strip and the taskbar can no longer be
        // summoned by pushing the pointer at the edge. One pixel is enough to keep
        // the appbar's hit target alive and is invisible against a near-black UI.
        switch (autoHideEdge)
        {
            case ScreenEdge.Left:
                x += 1;
                width = Math.Max(1, width - 1);
                break;
            case ScreenEdge.Top:
                y += 1;
                height = Math.Max(1, height - 1);
                break;
            case ScreenEdge.Right:
                width = Math.Max(1, width - 1);
                break;
            case ScreenEdge.Bottom:
                height = Math.Max(1, height - 1);
                break;
        }

        return new MaximizedPlacement(x, y, width, height);
    }

    /// <summary>
    /// Placement for fullscreen: the entire monitor, taskbar included.
    /// </summary>
    /// <remarks>
    /// No auto-hide inset here. Fullscreen is a deliberate request for the whole
    /// display, and leaving a pixel for the taskbar would make the edge of the
    /// reading canvas twitch as the pointer passed it.
    /// </remarks>
    public static MaximizedPlacement ForFullScreen(PixelRect monitor) =>
        new(0, 0, Math.Max(1, monitor.Width), Math.Max(1, monitor.Height));
}
