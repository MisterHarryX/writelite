using WriteLite.Services.Shell;

namespace WriteLite.Tests.Shell;

/// <summary>
/// How large "maximised" and "fullscreen" are.
/// </summary>
/// <remarks>
/// The numbers in the first test are the ones measured from the real bug: on a
/// 1920 × 1080 screen with a 48 px taskbar, the maximised window ran from −7,−7 to
/// 1927,1087 while the work area ended at 1032 — 55 px of live UI underneath the
/// taskbar, which is why the bottom of the application could not be reached. The
/// arithmetic is split out from the window so that case stays pinned without needing
/// a desktop to run on.
/// </remarks>
[TestClass]
public sealed class WindowPlacementMathTests
{
    [TestMethod]
    public void A_maximised_window_fills_the_work_area_and_stops_at_the_taskbar()
    {
        var monitor = new PixelRect(0, 0, 1920, 1080);
        var work = new PixelRect(0, 0, 1920, 1032);

        var placement = WindowPlacementMath.ForWorkArea(monitor, work);

        Assert.AreEqual(0, placement.X);
        Assert.AreEqual(0, placement.Y);
        Assert.AreEqual(1920, placement.Width);
        Assert.AreEqual(1032, placement.Height);
        Assert.AreEqual(1032, placement.Y + placement.Height,
            "The window must end where the work area ends, not 55 px below it.");
    }

    [TestMethod]
    public void A_taskbar_on_the_left_moves_the_window_right()
    {
        var monitor = new PixelRect(0, 0, 1920, 1080);
        var work = new PixelRect(72, 0, 1920, 1080);

        var placement = WindowPlacementMath.ForWorkArea(monitor, work);

        Assert.AreEqual(72, placement.X);
        Assert.AreEqual(1848, placement.Width);
        Assert.AreEqual(1080, placement.Height);
    }

    [TestMethod]
    public void A_taskbar_at_the_top_moves_the_window_down()
    {
        var monitor = new PixelRect(0, 0, 1920, 1080);
        var work = new PixelRect(0, 40, 1920, 1080);

        var placement = WindowPlacementMath.ForWorkArea(monitor, work);

        Assert.AreEqual(40, placement.Y);
        Assert.AreEqual(1040, placement.Height);
    }

    [TestMethod]
    public void A_secondary_monitor_is_answered_in_its_own_coordinates()
    {
        // A screen to the left of the primary has negative absolute coordinates, and
        // WM_GETMINMAXINFO wants the position relative to that monitor's own origin —
        // an absolute value here puts the window on the wrong screen.
        var monitor = new PixelRect(-1920, 0, 0, 1080);
        var work = new PixelRect(-1920, 0, 0, 1032);

        var placement = WindowPlacementMath.ForWorkArea(monitor, work);

        Assert.AreEqual(0, placement.X);
        Assert.AreEqual(0, placement.Y);
        Assert.AreEqual(1920, placement.Width);
        Assert.AreEqual(1032, placement.Height);
    }

    [TestMethod]
    public void A_monitor_above_and_left_of_the_primary_still_gets_a_zero_origin()
    {
        var monitor = new PixelRect(-2560, -1440, 0, 0);
        var work = new PixelRect(-2560, -1440, 0, -48);

        var placement = WindowPlacementMath.ForWorkArea(monitor, work);

        Assert.AreEqual(0, placement.X);
        Assert.AreEqual(0, placement.Y);
        Assert.AreEqual(2560, placement.Width);
        Assert.AreEqual(1392, placement.Height);
    }

    [TestMethod]
    public void Higher_scaling_needs_no_special_case()
    {
        // WM_GETMINMAXINFO is answered in physical pixels, so 125 %, 150 % and 175 %
        // differ only in how large the work area already is. A DIP constant would have
        // been wrong at each of them by a different amount; this arithmetic is not.
        foreach (var (width, height, taskbar) in new[]
                 {
                     (1920, 1080, 48),   // 100 %
                     (1920, 1080, 60),   // 125 %
                     (2560, 1440, 72),   // 150 %
                     (3840, 2160, 84)    // 175 %
                 })
        {
            var monitor = new PixelRect(0, 0, width, height);
            var work = new PixelRect(0, 0, width, height - taskbar);

            var placement = WindowPlacementMath.ForWorkArea(monitor, work);

            Assert.AreEqual(width, placement.Width);
            Assert.AreEqual(height - taskbar, placement.Height);
        }
    }

    [TestMethod]
    public void An_auto_hiding_taskbar_keeps_one_pixel_so_it_can_still_be_summoned()
    {
        var monitor = new PixelRect(0, 0, 1920, 1080);

        // Auto-hide is not subtracted from the work area, so without the inset the
        // window would cover the strip that brings the taskbar back.
        var work = new PixelRect(0, 0, 1920, 1080);

        var bottom = WindowPlacementMath.ForWorkArea(monitor, work, ScreenEdge.Bottom);
        Assert.AreEqual(1079, bottom.Height);
        Assert.AreEqual(0, bottom.Y);

        var top = WindowPlacementMath.ForWorkArea(monitor, work, ScreenEdge.Top);
        Assert.AreEqual(1, top.Y);
        Assert.AreEqual(1079, top.Height);

        var left = WindowPlacementMath.ForWorkArea(monitor, work, ScreenEdge.Left);
        Assert.AreEqual(1, left.X);
        Assert.AreEqual(1919, left.Width);

        var right = WindowPlacementMath.ForWorkArea(monitor, work, ScreenEdge.Right);
        Assert.AreEqual(0, right.X);
        Assert.AreEqual(1919, right.Width);
    }

    [TestMethod]
    public void Fullscreen_takes_the_whole_monitor_including_the_taskbar()
    {
        var monitor = new PixelRect(0, 0, 1920, 1080);

        var placement = WindowPlacementMath.ForFullScreen(monitor);

        Assert.AreEqual(0, placement.X);
        Assert.AreEqual(0, placement.Y);
        Assert.AreEqual(1920, placement.Width);
        Assert.AreEqual(1080, placement.Height);
    }

    [TestMethod]
    public void Fullscreen_leaves_no_pixel_for_an_auto_hiding_taskbar()
    {
        // Deliberate: fullscreen is a request for the whole display, and a reserved
        // pixel would make the edge of the reading canvas twitch as the pointer passed.
        var placement = WindowPlacementMath.ForFullScreen(new PixelRect(0, 0, 2560, 1440));

        Assert.AreEqual(1440, placement.Height);
    }

    [TestMethod]
    public void A_degenerate_monitor_never_produces_a_zero_sized_window()
    {
        var placement = WindowPlacementMath.ForWorkArea(
            new PixelRect(0, 0, 0, 0),
            new PixelRect(0, 0, 0, 0));

        Assert.IsTrue(placement.Width >= 1);
        Assert.IsTrue(placement.Height >= 1);
    }
}
