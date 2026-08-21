using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WriteLite.Views.Pages;

namespace WriteLite.Tests.Shell;

/// <summary>
/// Nothing important may sit outside the viewport at a small window size.
/// </summary>
/// <remarks>
/// This is the regression guard for the bug that started the whole layout pass: the
/// bottom of the application was unreachable, and nothing in the build or the test
/// suite noticed because "unreachable" is a geometry fact, not an exception. Every
/// case below measures a page at a real window size and asserts that the strip which
/// must always be reachable — a status bar, a toolbar, the rail's foot — has its
/// bottom edge inside the page.
///
/// The sizes are the ones a person actually has: a 1366 × 768 laptop, a half-screen
/// window on 1080p, and the smallest the shell will let itself become
/// (<c>MinWidth 820 × MinHeight 560</c>).
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ResponsiveLayoutTests
{
    /// <summary>Page sizes, after the 216 px rail and the 44 px title bar are taken out.</summary>
    private static readonly (double Width, double Height, string Label)[] Sizes =
    [
        (1024, 736, "1240 × 780 — the default window"),
        (1150, 724, "1366 × 768 — a small laptop"),
        (744, 604, "960 × 648 — a half-screen window"),
        (604, 516, "820 × 560 — the shell's minimum")
    ];

    [TestMethod]
    public void The_notes_status_bar_is_always_on_screen() =>
        AssertAlwaysVisible(() => new NotesPage(), "StatusText", "StorageText");

    [TestMethod]
    public void The_notes_filters_and_search_are_always_on_screen() =>
        AssertAlwaysVisible(() => new NotesPage(), "TabAll", "TabCompleted", "SearchBox", "NewNoteButton");

    [TestMethod]
    public void The_reading_library_status_bar_is_always_on_screen() =>
        AssertAlwaysVisible(() => new ReadingPage(), "StatusText", "OpenBookButton");

    [TestMethod]
    public void The_reader_toolbar_and_status_bar_are_always_on_screen() =>
        AssertAlwaysVisible(
            () => new ReaderView(),
            "SummaryText",
            "HighlightButton",
            "BookmarkButton",
            "BackButton",
            "PanelToggle");

    [TestMethod]
    public void The_editor_status_bar_is_always_on_screen() =>
        AssertAlwaysVisible(() => new EditorPage(), "StatusText");

    [TestMethod]
    public void The_dictionary_search_box_is_always_on_screen() =>
        AssertAlwaysVisible(() => new DictionaryPage(), "SearchBox");

    /// <summary>
    /// Measures a page at every size and checks the named elements stay inside it.
    /// </summary>
    /// <remarks>
    /// Bounds are taken relative to the page rather than to the screen, so this holds
    /// regardless of where the hidden host window is parked, and it fails for the two
    /// ways an element goes missing: pushed past the bottom edge by a row that will
    /// not shrink, or collapsed to nothing by a container that ran out of space.
    /// </remarks>
    private static void AssertAlwaysVisible(Func<FrameworkElement> factory, params string[] names) =>
        WpfTestHost.Run(() =>
        {
            WpfTestHost.EnsureThemeApplied();

            foreach (var (width, height, label) in Sizes)
            {
                var page = factory();

                var window = new Window
                {
                    Width = width,
                    Height = height,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Opacity = 0,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000,
                    Top = -4000,
                    Content = page
                };

                try
                {
                    window.Show();
                    window.UpdateLayout();
                    page.Measure(new Size(width, height));
                    page.Arrange(new Rect(0, 0, width, height));
                    page.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

                    foreach (var name in names)
                    {
                        var element = page.FindName(name) as FrameworkElement;
                        Assert.IsNotNull(element, $"{name} is not on this page.");

                        if (element.Visibility != Visibility.Visible)
                        {
                            continue;
                        }

                        var bounds = element.TransformToAncestor(page)
                            .TransformBounds(new Rect(element.RenderSize));

                        Assert.IsTrue(
                            bounds.Height > 0 && bounds.Width > 0,
                            $"At {label}, «{name}» collapsed to nothing.");

                        Assert.IsTrue(
                            bounds.Bottom <= height + 0.5,
                            $"At {label}, «{name}» ends at y={bounds.Bottom:F0} — {bounds.Bottom - height:F0} px " +
                            "below the bottom of the viewport, where it cannot be reached.");

                        Assert.IsTrue(
                            bounds.Top >= -0.5,
                            $"At {label}, «{name}» starts above the top of the viewport.");
                    }
                }
                finally
                {
                    window.Close();
                }
            }
        });

    /// <summary>
    /// The rail's own foot, which is where the clipping was actually seen.
    /// </summary>
    /// <remarks>
    /// «Завершить WriteLite» and the beta footnote sit at the bottom of the
    /// navigation rail. In the maximised window they were the first things to
    /// disappear under the taskbar, so they get their own check against the shell
    /// rather than against a page.
    /// </remarks>
    [TestMethod]
    public void The_navigation_rail_keeps_its_foot_inside_the_window() => WpfTestHost.Run(() =>
    {
        WpfTestHost.EnsureThemeApplied();

        var window = new WriteLite.Views.MainWindow
        {
            ShowInTaskbar = false,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
            Width = 900,
            Height = 600
        };

        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            var root = (FrameworkElement)window.Content;

            foreach (var name in new[] { "ExitButton", "RailFootnote" })
            {
                var element = (FrameworkElement)window.FindName(name)!;

                if (element.Visibility != Visibility.Visible)
                {
                    continue;
                }

                var bounds = element.TransformToAncestor(root)
                    .TransformBounds(new Rect(element.RenderSize));

                Assert.IsTrue(
                    bounds.Bottom <= root.ActualHeight + 0.5,
                    $"«{name}» ends {bounds.Bottom - root.ActualHeight:F0} px below the shell.");
            }
        }
        finally
        {
            window.ForceClose();
        }
    });

    /// <summary>
    /// The reading canvas keeps a usable width; the marks panel is what gives way.
    /// </summary>
    /// <remarks>
    /// The panel was a fixed 340 px column beside a bare star column, so narrowing the
    /// window took its width straight out of the text: at the shell's own minimum the
    /// book was a few characters wide, and the reader — a surface whose entire job is to
    /// show text — was the thing that disappeared while the sidebar stayed.
    /// </remarks>
    [TestMethod]
    public void The_reading_canvas_keeps_its_width_and_the_panel_yields() => WpfTestHost.Run(() =>
    {
        WpfTestHost.EnsureThemeApplied();

        var reader = new ReaderView();

        var window = new Window
        {
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
            Width = 1240,
            Height = 780,
            Content = reader
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var scroll = (FrameworkElement)reader.FindName("CanvasScroll")!;
            var panel = (FrameworkElement)reader.FindName("Panel")!;

            Assert.IsTrue(panel.ActualWidth > 0, "At a normal width the panel is shown.");

            foreach (var width in new double[] { 1240, 1000, 860, 760, 700, 604 })
            {
                window.Width = width;
                window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                window.UpdateLayout();

                Assert.IsTrue(
                    scroll.ActualWidth >= 300,
                    $"At a {width:F0} px reader the text column is only {scroll.ActualWidth:F0} px wide. " +
                    "The panel must step aside before the book does.");
            }

            Assert.AreEqual(0, panel.ActualWidth, 0.5,
                "At the narrowest size the panel has yielded entirely.");

            // And it comes back, because the toggle's own state was never touched.
            window.Width = 1240;
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            window.UpdateLayout();

            Assert.IsTrue(panel.ActualWidth > 0, "Widening the window brings the panel back.");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>The rail drops to icons rather than squeezing the page at small widths.</summary>
    [TestMethod]
    public void The_rail_becomes_compact_before_it_starves_the_page() => WpfTestHost.Run(() =>
    {
        WpfTestHost.EnsureThemeApplied();

        var window = new WriteLite.Views.MainWindow
        {
            ShowInTaskbar = false,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
            Width = 1240,
            Height = 700
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var rail = (System.Windows.Controls.ColumnDefinition)window.FindName("RailColumn")!;
            Assert.AreEqual(216, rail.Width.Value, 0.5, "At a normal width the rail carries its labels.");

            window.Width = 900;
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            Assert.AreEqual(60, rail.Width.Value, 0.5,
                "Below 1000 px the rail must step aside so the content keeps its measure.");
        }
        finally
        {
            window.ForceClose();
        }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
