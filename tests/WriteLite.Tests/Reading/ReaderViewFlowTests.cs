using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using WriteLite.Services.Reading;
using WriteLite.Views.Pages;
using Button = System.Windows.Controls.Button;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace WriteLite.Tests.Reading;

/// <summary>
/// The reader driven the way a person drives it.
/// </summary>
/// <remarks>
/// Deliberately not a service test. <see cref="ReadingLibraryServiceTests"/> already
/// proves the store round-trips a highlight; what this proves is the part that was
/// worth being sceptical about — that pressing the button in the toolbar reaches the
/// store at all, and that a mark made in one reader instance is painted by the next
/// one built from the file. The chain under test is toolbar → view → service → disk →
/// a fresh view, which is the whole feature.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ReaderViewFlowTests
{
    private const string Book =
        "Глава первая. О языках запросов\n\n" +
        "SQL — декларативный язык для работы с реляционными базами данных.\n\n" +
        "Декларативный подход описывает цель, а не путь к ней.\n\n" +
        "Глава вторая. О нормализации\n\n" +
        "Нормализация устраняет избыточность данных и уменьшает вероятность аномалий.\n\n" +
        "Первая нормальная форма требует атомарных значений в каждом поле.\n";

    private string _root = string.Empty;
    private string _bookPath = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "wl-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _bookPath = Path.Combine(_root, "kniga.txt");
        File.WriteAllText(_bookPath, Book, Encoding.UTF8);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory left behind is not a test failure.
        }
    }

    private ReadingLibraryService NewLibrary() => new(
        Path.Combine(_root, "library.json"),
        Path.Combine(_root, "projects"));

    /// <summary>Builds a reader inside a hidden window and opens a project in it.</summary>
    private static ReaderView Open(ReadingLibraryService library, ReadingProject project, out Window host)
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

        // Import is asynchronous and the restore happens a dispatcher hop later.
        Pump(() => Canvas(view).Document.Blocks.Count > 1);
        Pump(() => true, milliseconds: 250);

        return view;
    }

    private static RichTextBox Canvas(ReaderView view) => (RichTextBox)view.FindName("Canvas")!;

    private static Button Toolbar(ReaderView view, string name) => (Button)view.FindName(name)!;

    private static void Press(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static bool Pump(Func<bool> until, int milliseconds = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (until())
            {
                return true;
            }

            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(15);
        }

        return until();
    }

    /// <summary>
    /// Selects a passage in the rendered book, the way dragging across it would.
    /// </summary>
    /// <remarks>
    /// Anchored on the run that contains the words rather than on a character count
    /// from the start of the document. Counting forwards is what a test naturally
    /// reaches for and it is wrong here: <c>TextRange.Text</c> renders a paragraph
    /// break as two characters while a single insertion step crosses it, so the
    /// selection drifts by one per paragraph — which says nothing about the product.
    /// Selecting inside one run has no such ambiguity.
    /// </remarks>
    private static void SelectPassage(ReaderView view, string passage)
    {
        var canvas = Canvas(view);

        foreach (var block in canvas.Document.Blocks.OfType<System.Windows.Documents.Paragraph>())
        {
            foreach (var run in block.Inlines.OfType<Run>())
            {
                var index = run.Text.IndexOf(passage, StringComparison.Ordinal);
                if (index < 0)
                {
                    continue;
                }

                var from = run.ContentStart.GetPositionAtOffset(index, LogicalDirection.Forward)!;
                var to = run.ContentStart.GetPositionAtOffset(index + passage.Length, LogicalDirection.Forward)!;
                canvas.Selection.Select(from, to);
                return;
            }
        }

        Assert.Fail($"«{passage}» is not in the rendered book.");
    }

    // ── The chain ────────────────────────────────────────────────────────────

    [TestMethod]
    public void Opening_a_book_renders_it_and_the_reader_can_mark_it_up()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);

            var view = Open(library, project, out var host);

            try
            {
                var canvas = Canvas(view);
                var rendered = new TextRange(canvas.Document.ContentStart, canvas.Document.ContentEnd).Text;

                StringAssert.Contains(rendered, "декларативный язык",
                    "The book did not reach the reading canvas.");

                // Highlight, from the toolbar the reader actually presses.
                SelectPassage(view, "декларативный язык");
                Press(Toolbar(view, "HighlightButton"));

                Assert.AreEqual(1, view.Project!.Highlights.Count,
                    "Pressing «Выделить» must reach the project.");
                Assert.AreEqual("декларативный язык", view.Project.Highlights[0].Anchor.Quote);

                // Bookmark at the current place.
                Press(Toolbar(view, "BookmarkButton"));
                Assert.AreEqual(1, view.Project.Bookmarks.Count);

                // A card, added the way the panel's editor adds one.
                view.Project.Cards.Add(new StudyCard
                {
                    Front = "Что такое SQL?",
                    Back = "Декларативный язык для работы с реляционными базами данных.",
                    Source = view.Project.Highlights[0].Anchor.Copy()
                });

                view.Project.Annotations.Add(new Annotation
                {
                    Anchor = view.Project.Highlights[0].Anchor.Copy(),
                    Note = "Моя мысль по этому поводу"
                });

                library.Save(view.Project);
                view.Close();
            }
            finally
            {
                host.Close();
            }

            // ── The restart ──────────────────────────────────────────────────
            // A brand-new service, a brand-new view, reading from the file.
            var reopenedLibrary = NewLibrary();
            var reopenedProject = reopenedLibrary.Load(project.Id)!;

            Assert.AreEqual(1, reopenedProject.Highlights.Count);
            Assert.AreEqual(1, reopenedProject.Annotations.Count);
            Assert.AreEqual(1, reopenedProject.Bookmarks.Count);
            Assert.AreEqual(1, reopenedProject.Cards.Count);

            var second = Open(reopenedLibrary, reopenedProject, out var secondHost);

            try
            {
                var canvas = Canvas(second);
                var anchor = reopenedProject.Highlights[0].Anchor;

                // The mark is not merely stored: it is on the page again. Walking the
                // rendered document for a run carrying a background is what proves the
                // offset survived the round trip through JSON and a fresh layout.
                var painted = new TextRange(canvas.Document.ContentStart, canvas.Document.ContentEnd)
                    .GetPropertyValue(TextElement.BackgroundProperty);

                var highlighted = FindHighlightedText(canvas);

                Assert.IsTrue(
                    highlighted.Contains("декларативный язык", StringComparison.Ordinal),
                    $"The saved highlight was not repainted. Painted text: «{highlighted}»; " +
                    $"document-wide background: {painted}; anchor at {anchor.Start}.");
            }
            finally
            {
                second.Close();
                secondHost.Close();
            }
        });
    }

    /// <summary>Concatenates every run in the document that carries a background.</summary>
    private static string FindHighlightedText(RichTextBox canvas)
    {
        var builder = new StringBuilder();

        foreach (var block in canvas.Document.Blocks)
        {
            if (block is not System.Windows.Documents.Paragraph paragraph)
            {
                continue;
            }

            foreach (var inline in paragraph.Inlines)
            {
                if (inline is Run run
                    && run.ReadLocalValue(TextElement.BackgroundProperty) != DependencyProperty.UnsetValue)
                {
                    builder.Append(run.Text);
                }
            }
        }

        return builder.ToString();
    }

    [TestMethod]
    public void The_reading_position_comes_back_on_the_next_open()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);

            // Somewhere in the second half, saved the way a scroll would save it.
            project.Position = Book.IndexOf("Нормализация", StringComparison.Ordinal);
            project.Progress = 0.6;
            library.Save(project);

            var reopened = NewLibrary().Load(project.Id)!;
            var view = Open(NewLibrary(), reopened, out var host);

            try
            {
                Assert.AreEqual(project.Position, reopened.Position,
                    "«Продолжить с места остановки» is this number surviving.");

                var canvas = Canvas(view);
                var scroll = (System.Windows.Controls.ScrollViewer)view.FindName("CanvasScroll")!;

                Pump(() => scroll.VerticalOffset > 0, milliseconds: 4000);

                Assert.IsTrue(canvas.Document.Blocks.Count > 1, "The book is open …");
            }
            finally
            {
                view.Close();
                host.Close();
            }
        });
    }

    [TestMethod]
    public void A_missing_source_file_still_opens_the_project_and_keeps_its_marks()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);

            project.Annotations.Add(new Annotation
            {
                Anchor = new ReadingAnchor { Start = 0, Length = 5, Quote = "Глава" },
                Note = "Не потерять"
            });

            library.Save(project);
            File.Delete(_bookPath);

            var reopened = NewLibrary().Load(project.Id)!;
            var view = Open(NewLibrary(), reopened, out var host);

            try
            {
                var error = (FrameworkElement)view.FindName("ErrorState")!;
                Assert.IsTrue(
                    Pump(() => error.Visibility == Visibility.Visible, milliseconds: 4000),
                    "A book that has moved must be reported, not silently blank.");

                Assert.AreEqual(1, reopened.Annotations.Count, "And the reader's work must still be there.");

                var relocate = (Button)view.FindName("RelocateButton")!;
                Assert.AreEqual(Visibility.Visible, relocate.Visibility,
                    "There must be a way back: pointing the project at the file again.");
            }
            finally
            {
                view.Close();
                host.Close();
            }
        });
    }

    [TestMethod]
    public void The_type_size_control_changes_the_book_and_is_remembered()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            var startingSize = project.Typography.FontSize;

            var view = Open(library, project, out var host);

            try
            {
                Press(Toolbar(view, "FontLargerButton"));
                Press(Toolbar(view, "FontLargerButton"));
                Press(Toolbar(view, "FontLargerButton"));

                Assert.AreEqual(startingSize + 3, view.Project!.Typography.FontSize, 0.0001,
                    "Pressing «+» must change the reader's type size.");

                // Re-laying the book out is asynchronous; the canvas picks the new size
                // up once the rebuild lands.
                Pump(() => Math.Abs(Canvas(view).Document.FontSize - (startingSize + 3)) < 0.001,
                    milliseconds: 8000);

                Assert.AreEqual(startingSize + 3, Canvas(view).Document.FontSize, 0.001,
                    "The rendered book must actually be set in the chosen size.");
            }
            finally
            {
                view.Close();
                host.Close();
            }

            var reopened = NewLibrary().Load(project.Id)!;

            Assert.AreEqual(startingSize + 3, reopened.Typography.FontSize, 0.0001,
                "Type settings belong to the book and come back with it.");
        });
    }

    [TestMethod]
    public void Marks_that_no_longer_match_the_file_are_kept_rather_than_deleted()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);

            project.Annotations.Add(new Annotation
            {
                Anchor = new ReadingAnchor { Start = 10, Length = 22, Quote = "фраза которой тут нет" },
                Note = "Написано по другой редакции"
            });

            library.Save(project);

            var reopened = NewLibrary().Load(project.Id)!;
            var view = Open(NewLibrary(), reopened, out var host);

            try
            {
                Assert.AreEqual(1, view.Project!.Annotations.Count,
                    "A quote the current file no longer contains must not cost the reader their note.");
                Assert.AreEqual("Написано по другой редакции", view.Project.Annotations[0].Note);
            }
            finally
            {
                view.Close();
                host.Close();
            }
        });
    }
}
