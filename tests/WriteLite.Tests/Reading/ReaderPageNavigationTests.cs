using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WriteLite.Services.Reading;
using WriteLite.Views.Pages;
using Button = System.Windows.Controls.Button;
using RichTextBox = System.Windows.Controls.RichTextBox;
using TextBox = System.Windows.Controls.TextBox;

namespace WriteLite.Tests.Reading;

/// <summary>
/// Page navigation in the reader, end to end through the real control.
/// </summary>
/// <remarks>
/// <see cref="ReadingPaginationTests"/> covers the division itself. These cover the part a
/// person touches: that the number shown is the page they are on, that the arrows move by
/// exactly one page, that typing a page goes there, and that nonsense in the box does not
/// take the reader anywhere or leave the box lying about where they are.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ReaderPageNavigationTests
{
    private string _root = string.Empty;
    private string _bookPath = string.Empty;

    /// <summary>Long enough to have many pages, and paragraphed so pages break cleanly.</summary>
    private static string Book()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 120; i++)
        {
            builder.Append("Абзац номер ").Append(i).Append(". ");
            builder.Append(new string('я', 600));
            builder.Append("\n\n");
        }

        return builder.ToString();
    }

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "wl-reader-pages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _bookPath = Path.Combine(_root, "kniga.txt");
        File.WriteAllText(_bookPath, Book(), Encoding.UTF8);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not a test failure.
        }
    }

    [TestMethod]
    public void AnOpenedBookShowsItsPageCountAndOpensOnPageOne()
    {
        WpfTestHost.Run(() =>
        {
            using var library = NewLibrary();
            var view = Open(library, library.OpenOrCreate(_bookPath), out var host);
            try
            {
                Assert.AreEqual("1", PageBox(view).Text);

                var count = PageCount(view);
                Assert.IsGreaterThan(1, count, "a 70 000-character book is more than one page");

                Assert.IsFalse(Nav(view, "PreviousPageButton").IsEnabled, "no page before the first");
                Assert.IsTrue(Nav(view, "NextPageButton").IsEnabled);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [TestMethod]
    public void TheForwardArrowMovesExactlyOnePage()
    {
        WpfTestHost.Run(() =>
        {
            using var library = NewLibrary();
            var view = Open(library, library.OpenOrCreate(_bookPath), out var host);
            try
            {
                Press(Nav(view, "NextPageButton"));
                Pump(() => PageBox(view).Text == "2");
                Assert.AreEqual("2", PageBox(view).Text);

                Press(Nav(view, "NextPageButton"));
                Pump(() => PageBox(view).Text == "3");
                Assert.AreEqual("3", PageBox(view).Text);

                Press(Nav(view, "PreviousPageButton"));
                Pump(() => PageBox(view).Text == "2");
                Assert.AreEqual("2", PageBox(view).Text);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [TestMethod]
    public void TypingAPageNumberGoesToThatPage()
    {
        WpfTestHost.Run(() =>
        {
            using var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            var view = Open(library, project, out var host);
            try
            {
                var box = PageBox(view);
                box.Text = "7";
                box.RaiseEvent(new RoutedEventArgs(FrameworkElement.LostFocusEvent));

                Pump(() => box.Text == "7");
                Assert.AreEqual("7", box.Text);

                // The jump is a statement about where the reader wants to be, so it is
                // recorded rather than left for the scroll handler to notice.
                Assert.IsGreaterThan(0, project.Position);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [TestMethod]
    public void APageNumberPastTheEndIsRefusedAndTheBoxSaysWhereTheReaderReallyIs()
    {
        WpfTestHost.Run(() =>
        {
            using var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            var view = Open(library, project, out var host);
            try
            {
                var box = PageBox(view);
                var before = project.Position;

                box.Text = "99999";
                box.RaiseEvent(new RoutedEventArgs(FrameworkElement.LostFocusEvent));
                Pump(() => box.Text != "99999");

                Assert.AreEqual("1", box.Text, "the box must return to the page actually shown");
                Assert.AreEqual(before, project.Position, "a refused page must not move the reader");
            }
            finally
            {
                host.Close();
            }
        });
    }

    [TestMethod]
    public void TextInThePageBoxIsRefusedTheSameWay()
    {
        WpfTestHost.Run(() =>
        {
            using var library = NewLibrary();
            var view = Open(library, library.OpenOrCreate(_bookPath), out var host);
            try
            {
                var box = PageBox(view);
                box.Text = "сорок два";
                box.RaiseEvent(new RoutedEventArgs(FrameworkElement.LostFocusEvent));
                Pump(() => box.Text != "сорок два");

                Assert.AreEqual("1", box.Text);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [TestMethod]
    public void ChangingTheTypeSizeDoesNotChangeThePageCount()
    {
        // The guarantee the virtual model exists to give. A page count that moved when
        // someone made the type bigger would make the number meaningless between sessions.
        WpfTestHost.Run(() =>
        {
            using var library = NewLibrary();
            var view = Open(library, library.OpenOrCreate(_bookPath), out var host);
            try
            {
                var before = PageCount(view);

                Press(Nav(view, "FontLargerButton"));
                Press(Nav(view, "FontLargerButton"));
                Pump(() => false, milliseconds: 400);

                Assert.AreEqual(before, PageCount(view));
            }
            finally
            {
                host.Close();
            }
        });
    }

    [TestMethod]
    public void ResizingTheWindowDoesNotChangeThePageCount()
    {
        WpfTestHost.Run(() =>
        {
            using var library = NewLibrary();
            var view = Open(library, library.OpenOrCreate(_bookPath), out var host);
            try
            {
                var before = PageCount(view);

                host.Width = 700;
                host.UpdateLayout();
                Pump(() => false, milliseconds: 400);

                Assert.AreEqual(before, PageCount(view));
            }
            finally
            {
                host.Close();
            }
        });
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private ReadingLibraryService NewLibrary() => new(
        Path.Combine(_root, "library.json"),
        Path.Combine(_root, "projects"));

    private static ReaderView Open(
        ReadingLibraryService library,
        ReadingProject project,
        out Window host)
    {
        WpfTestHost.EnsureThemeApplied();

        var view = new ReaderView();
        view.Bind(library, new StudyCardDraftService(null));

        host = new Window
        {
            Width = 1280,
            Height = 800,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
            Content = view
        };

        host.Show();
        host.UpdateLayout();

        _ = view.OpenAsync(project);

        Pump(() => ((RichTextBox)view.FindName("Canvas")!).Document.Blocks.Count > 1);
        Pump(() => PageBox(view).Text.Length > 0);
        Pump(() => false, milliseconds: 250);

        return view;
    }

    private static TextBox PageBox(ReaderView view) => (TextBox)view.FindName("PageBox")!;

    private static Button Nav(ReaderView view, string name) => (Button)view.FindName(name)!;

    private static int PageCount(ReaderView view)
    {
        var label = ((TextBlock)view.FindName("PageCountText")!).Text;
        return int.TryParse(label.Replace("/", string.Empty).Trim(), out var value) ? value : -1;
    }

    private static void Press(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static bool Pump(Func<bool> until, int milliseconds = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (until()) return true;

            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }

        return until();
    }
}
