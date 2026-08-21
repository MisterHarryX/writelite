using System.Windows;
using System.Windows.Threading;
using WriteLite.Views;

namespace WriteLite.Tests.Shell;

/// <summary>
/// F11, and getting back exactly where you were.
/// </summary>
/// <remarks>
/// The restore is the half worth testing. Entering fullscreen is easy to get right and
/// obvious when it is wrong; leaving it is where a window quietly loses its size, or
/// comes back windowed after having been maximised, and neither shows up until someone
/// has been using the application for an hour.
///
/// The window is real but parked off-screen at zero opacity, so nothing appears on the
/// desktop while these run.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class FullScreenTests
{
    private static void WithWindow(Action<MainWindow> body) => WpfTestHost.Run(() =>
    {
        WpfTestHost.EnsureThemeApplied();

        var window = new MainWindow
        {
            ShowInTaskbar = false,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 120,
            Top = 90,
            Width = 1100,
            Height = 700
        };

        try
        {
            window.Show();
            window.UpdateLayout();
            Settle();
            body(window);
        }
        finally
        {
            window.ForceClose();
        }
    });

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(30);
        }
    }

    [TestMethod]
    public void Fullscreen_hides_the_title_bar_and_gives_its_row_back()
    {
        WithWindow(window =>
        {
            var titleBar = (FrameworkElement)window.FindName("TitleBar")!;
            var row = (System.Windows.Controls.RowDefinition)window.FindName("TitleBarRow")!;

            Assert.AreEqual(Visibility.Visible, titleBar.Visibility);
            Assert.AreEqual(44, row.Height.Value, 0.5);

            window.ToggleFullScreen();
            Settle();

            Assert.IsTrue(window.IsFullScreen);
            Assert.AreEqual(Visibility.Collapsed, titleBar.Visibility,
                "Fullscreen means the whole display belongs to the content.");
            Assert.AreEqual(0, row.Height.Value, 0.5,
                "A collapsed title bar must give its row back, not leave a 44 px gap.");
        });
    }

    [TestMethod]
    public void The_caption_area_is_released_so_clicks_reach_the_content()
    {
        WithWindow(window =>
        {
            window.ToggleFullScreen();
            Settle();

            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
            Assert.IsNotNull(chrome);
            Assert.AreEqual(0, chrome.CaptionHeight, 0.5,
                "A 44 px drag strip over a full-screen reader would swallow clicks meant for the text.");
            Assert.AreEqual(0, chrome.ResizeBorderThickness.Top, 0.5);
        });
    }

    [TestMethod]
    public void Leaving_fullscreen_restores_the_exact_previous_geometry()
    {
        WithWindow(window =>
        {
            var left = window.Left;
            var top = window.Top;
            var width = window.Width;
            var height = window.Height;

            window.ToggleFullScreen();
            Settle();
            Assert.IsTrue(window.IsFullScreen);

            window.ToggleFullScreen();
            Settle();

            Assert.IsFalse(window.IsFullScreen);
            Assert.AreEqual(WindowState.Normal, window.WindowState);
            Assert.AreEqual(left, window.Left, 1.0);
            Assert.AreEqual(top, window.Top, 1.0);
            Assert.AreEqual(width, window.Width, 1.0);
            Assert.AreEqual(height, window.Height, 1.0);

            var titleBar = (FrameworkElement)window.FindName("TitleBar")!;
            Assert.AreEqual(Visibility.Visible, titleBar.Visibility, "The title bar comes back.");

            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
            Assert.AreEqual(44, chrome!.CaptionHeight, 0.5, "And so does the drag area.");
        });
    }

    [TestMethod]
    public void A_window_that_was_maximised_comes_back_maximised()
    {
        WithWindow(window =>
        {
            window.WindowState = WindowState.Maximized;
            Settle();

            window.ToggleFullScreen();
            Settle();
            Assert.IsTrue(window.IsFullScreen);

            window.ToggleFullScreen();
            Settle();

            Assert.AreEqual(WindowState.Maximized, window.WindowState,
                "Leaving fullscreen restores the state it was entered from, not a default.");
        });
    }

    [TestMethod]
    public void Toggling_twice_from_maximised_does_not_drift()
    {
        WithWindow(window =>
        {
            var restore = window.RestoreBounds;

            window.WindowState = WindowState.Maximized;
            Settle();

            for (var i = 0; i < 3; i++)
            {
                window.ToggleFullScreen();
                Settle();
                window.ToggleFullScreen();
                Settle();
            }

            Assert.AreEqual(WindowState.Maximized, window.WindowState);
            Assert.IsFalse(window.IsFullScreen);

            window.WindowState = WindowState.Normal;
            Settle();

            Assert.AreEqual(restore.Width, window.Width, 2.0,
                "Three round trips must not shrink the window a little each time.");
            Assert.AreEqual(restore.Height, window.Height, 2.0);
        });
    }

    [TestMethod]
    public void Exiting_when_not_in_fullscreen_does_nothing()
    {
        WithWindow(window =>
        {
            var width = window.Width;

            // Escape reaches the shell whenever nothing in front of it took the key,
            // which is most of the time. It must be a no-op then.
            window.ToggleFullScreen();
            Settle();
            window.ToggleFullScreen();
            Settle();
            window.ToggleFullScreen();
            Settle();
            window.ToggleFullScreen();
            Settle();

            Assert.IsFalse(window.IsFullScreen);
            Assert.AreEqual(width, window.Width, 1.0);
        });
    }
}
