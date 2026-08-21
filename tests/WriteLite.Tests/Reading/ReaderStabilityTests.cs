using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using WriteLite.Services.Reading;
using WriteLite.Views.Pages;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace WriteLite.Tests.Reading;

/// <summary>
/// The reader's release blockers, each pinned by the behaviour that was wrong.
/// </summary>
/// <remarks>
/// Every test here corresponds to something a person reported: the type buttons
/// freezing the application, the old Windows context menu appearing over the book,
/// right-click actions doing nothing on visibly selected text, and marks that only
/// showed up in the panel after leaving the section and coming back.
///
/// They are written against the observable consequences rather than against the
/// internals that produce them — the same <see cref="FlowDocument"/> instance surviving
/// a type change is what "the book was not re-imported" means from outside, and it is
/// the property that would break first if the rebuild ever came back.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ReaderStabilityTests
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
        _root = Path.Combine(Path.GetTempPath(), "wl-reader-stab-" + Guid.NewGuid().ToString("N"));
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

    // ── Harness ──────────────────────────────────────────────────────────────

    private ReadingLibraryService NewLibrary() => new(
        Path.Combine(_root, "library.json"),
        Path.Combine(_root, "projects"));

    private static ReaderView Open(
        ReadingLibraryService library,
        ReadingProject project,
        out Window host,
        StudyCardDraftService? drafts = null)
    {
        WpfTestHost.EnsureThemeApplied();

        var view = new ReaderView();
        view.Bind(library, drafts ?? new StudyCardDraftService(null));

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

        Pump(() => Canvas(view).Document.Blocks.Count > 1);
        Pump(() => true, milliseconds: 250);

        return view;
    }

    private static RichTextBox Canvas(ReaderView view) => (RichTextBox)view.FindName("Canvas")!;

    private static Panel MarksList(ReaderView view) => (Panel)view.FindName("MarksList")!;

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
            Thread.Sleep(10);
        }

        return until();
    }

    private static void SelectPassage(ReaderView view, string passage)
    {
        var canvas = Canvas(view);

        foreach (var block in canvas.Document.Blocks.OfType<Paragraph>())
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

    /// <summary>
    /// Raises the event WPF raises when the reader right-clicks the book.
    /// </summary>
    /// <remarks>
    /// <see cref="ContextMenuEventArgs"/> has no public constructor — WPF creates it
    /// itself and hands it to <c>ContextMenuOpening</c> — so the only way to exercise
    /// the handler as the framework calls it is to build the arguments the same way.
    /// The alternative, calling the handler directly, would prove nothing about the
    /// event actually being wired up, which is half of what these tests are for.
    /// </remarks>
    private static ContextMenu OpenContextMenu(ReaderView view)
    {
        var canvas = Canvas(view);

        var constructor = typeof(ContextMenuEventArgs).GetConstructor(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            binder: null,
            [typeof(object), typeof(bool)],
            modifiers: null);

        Assert.IsNotNull(constructor, "WPF changed the shape of ContextMenuEventArgs.");

        var args = (ContextMenuEventArgs)constructor.Invoke([canvas, true]);
        args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;

        canvas.RaiseEvent(args);

        return canvas.ContextMenu!;
    }

    private static MenuItem Item(ContextMenu menu, string startsWith) =>
        menu.Items.OfType<MenuItem>().First(item =>
            item.Header is string header
            && header.StartsWith(startsWith, StringComparison.Ordinal));

    private static void Click(MenuItem item) =>
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    // ── Typography ───────────────────────────────────────────────────────────

    [TestMethod]
    public void Changing_the_type_size_relays_the_book_out_without_re_importing_it()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            var view = Open(library, project, out var host);

            try
            {
                var canvas = Canvas(view);
                var documentBefore = canvas.Document;
                var runBefore = canvas.Document.Blocks.OfType<Paragraph>()
                    .SelectMany(paragraph => paragraph.Inlines.OfType<Run>())
                    .First();

                var sizeBefore = documentBefore.FontSize;

                Press(Toolbar(view, "FontLargerButton"));
                Pump(() => true, milliseconds: 300);

                Assert.AreSame(documentBefore, canvas.Document,
                    "Pressing «+» must re-lay the book out, not build a new one. A fresh " +
                    "FlowDocument means the source file was parsed again — seconds of work " +
                    "on the dispatcher, per click.");

                Assert.AreSame(runBefore, canvas.Document.Blocks.OfType<Paragraph>()
                        .SelectMany(paragraph => paragraph.Inlines.OfType<Run>())
                        .First(),
                    "The runs must survive too: every stored offset and every painted mark " +
                    "is anchored to them.");

                Assert.AreEqual(sizeBefore + 1, canvas.Document.FontSize, 0.001,
                    "…and the book must actually be set in the new size.");
            }
            finally
            {
                view.Close();
                host.Close();
            }
        });
    }

    [TestMethod]
    public void A_highlight_stays_painted_across_a_type_change()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            var view = Open(library, project, out var host);

            try
            {
                SelectPassage(view, "декларативный язык");
                Press(Toolbar(view, "HighlightButton"));

                Press(Toolbar(view, "FontLargerButton"));
                Press(Toolbar(view, "FontSmallerButton"));
                Pump(() => true, milliseconds: 300);

                Assert.IsTrue(
                    PaintedText(Canvas(view)).Contains("декларативный язык", StringComparison.Ordinal),
                    "Changing the type size must not lose a mark. It used to re-import the " +
                    "book, which threw away the runs the highlight was painted on.");
            }
            finally
            {
                view.Close();
                host.Close();
            }
        });
    }

    [TestMethod]
    public void Rapid_type_changes_leave_the_reader_alive_and_on_the_last_size()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            project.Typography.FontSize = 16;

            var view = Open(library, project, out var host);

            try
            {
                // 16 → 30 → 18 → 28 → 20 → 25, three times over, as fast as the buttons
                // can be pressed. Every one of these used to queue a full re-import.
                foreach (var target in new[] { 30d, 18, 28, 20, 25, 30, 18, 25 })
                {
                    while (view.Project!.Typography.FontSize < target)
                    {
                        Press(Toolbar(view, "FontLargerButton"));
                    }

                    while (view.Project.Typography.FontSize > target)
                    {
                        Press(Toolbar(view, "FontSmallerButton"));
                    }
                }

                Pump(() => true, milliseconds: 500);

                Assert.AreEqual(25, view.Project!.Typography.FontSize, 0.001);
                Assert.AreEqual(25, Canvas(view).Document.FontSize, 0.001,
                    "The rendered size must agree with the stored one: overlapping rebuilds " +
                    "used to race, and whichever finished last won.");
                Assert.IsTrue(Canvas(view).Document.Blocks.Count > 1, "The book is still there.");
            }
            finally
            {
                view.Close();
                host.Close();
            }
        });
    }

    /// <summary>
    /// The crash that the stress driver found, reduced to its cause.
    /// </summary>
    /// <remarks>
    /// Pressing «+» invalidates the document's layout. Pressing it again before WPF has
    /// re-validated used to ask the text view where the viewport was, and a text view
    /// with a pending layout throws <see cref="InvalidOperationException"/> rather than
    /// answering — which took the whole application down from a button anyone can press
    /// twice quickly. No pumping between the presses here: that is the point.
    /// </remarks>
    [TestMethod]
    public void Pressing_the_type_buttons_back_to_back_does_not_throw()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            var view = Open(library, project, out var host);

            try
            {
                var larger = Toolbar(view, "FontLargerButton");
                var smaller = Toolbar(view, "FontSmallerButton");

                for (var burst = 0; burst < 6; burst++)
                {
                    Press(larger);
                    Press(larger);
                    Press(larger);
                    Press(smaller);
                    Press(smaller);
                    Press(larger);
                }

                Pump(() => true, milliseconds: 600);

                Assert.IsTrue(Canvas(view).Document.Blocks.Count > 1,
                    "The book survived the burst …");
                Assert.AreEqual(
                    view.Project!.Typography.FontSize,
                    Canvas(view).Document.FontSize,
                    0.001,
                    "… and the rendered size agrees with the stored one.");
            }
            finally
            {
                view.Close();
                host.Close();
            }
        });
    }

    [TestMethod]
    public void Type_settings_are_written_once_the_reader_settles()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            var view = Open(library, project, out var host);

            try
            {
                Press(Toolbar(view, "FontLargerButton"));
                Press(Toolbar(view, "FontLargerButton"));
                Pump(() => true, milliseconds: 200);

                view.SavePositionNow();
            }
            finally
            {
                view.Close();
                host.Close();
            }

            var reopened = NewLibrary().Load(project.Id)!;
            Assert.AreEqual(project.Typography.FontSize, reopened.Typography.FontSize, 0.001,
                "Deferring the write must not lose it.");
        });
    }

    // ── Context menu ─────────────────────────────────────────────────────────

    [TestMethod]
    public void The_reader_owns_its_context_menu_before_the_first_right_click()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            var view = Open(library, project, out var host);

            try
            {
                Assert.IsNotNull(Canvas(view).ContextMenu,
                    "A RichTextBox with no ContextMenu gets the framework's own Cut/Copy/Paste " +
                    "menu, because TextBoxBase handles ContextMenuOpening as a class handler " +
                    "before this control's instance handler runs. Owning the menu from the " +
                    "start is what stops the legacy menu ever being shown.");
            }
            finally
            {
                view.Close();
                host.Close();
            }
        });
    }

    [TestMethod]
    public void The_first_right_click_offers_the_writelite_actions()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            var view = Open(library, project, out var host);

            try
            {
                SelectPassage(view, "декларативный язык");

                var menu = OpenContextMenu(view);
                var headers = menu.Items.OfType<MenuItem>()
                    .Select(item => item.Header as string ?? string.Empty)
                    .ToArray();

                CollectionAssert.Contains(headers, "Выделить");
                CollectionAssert.Contains(headers, "Добавить пометку");
                CollectionAssert.Contains(headers, "Создать карточку");
                CollectionAssert.Contains(headers, "Добавить закладку");
                CollectionAssert.Contains(headers, "Копировать");

                Assert.IsTrue(
                    headers.Any(header => header.StartsWith("Открыть «", StringComparison.Ordinal)),
                    "A selected word must be offerable to the dictionary.");
            }
            finally
            {
                view.Close();
                host.Close();
            }
        });
    }

    [TestMethod]
    public void A_menu_action_uses_the_selection_the_menu_was_opened_on()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            var view = Open(library, project, out var host);

            try
            {
                SelectPassage(view, "декларативный язык");

                var menu = OpenContextMenu(view);

                // What opening a context menu does to a text control: the popup takes
                // focus and the selection is no longer something to read. This is the
                // exact condition under which «Выделить» used to do nothing at all.
                Canvas(view).Selection.Select(
                    Canvas(view).Document.ContentStart,
                    Canvas(view).Document.ContentStart);

                Assert.IsTrue(Canvas(view).Selection.IsEmpty, "Precondition: the live selection is gone.");

                var highlight = Item(menu, "Выделить");
                Click(highlight.Items.OfType<MenuItem>().First());

                Assert.AreEqual(1, view.Project!.Highlights.Count,
                    "The action must run against the selection captured when the menu opened.");
                Assert.AreEqual("декларативный язык", view.Project.Highlights[0].Anchor.Quote);
            }
            finally
            {
                view.Close();
                host.Close();
            }
        });
    }

    [TestMethod]
    public void The_toolbar_uses_the_live_selection_not_the_last_one_a_menu_saw()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            var view = Open(library, project, out var host);

            try
            {
                // A menu is opened over one passage and dismissed without being used.
                SelectPassage(view, "декларативный язык");
                OpenContextMenu(view);

                // Then a different passage is selected and the toolbar is pressed.
                SelectPassage(view, "Нормализация");
                Press(Toolbar(view, "HighlightButton"));

                Assert.AreEqual(1, view.Project!.Highlights.Count);
                Assert.AreEqual(
                    "Нормализация",
                    view.Project.Highlights[0].Anchor.Quote,
                    "The toolbar must act on what is selected now. Holding the menu's " +
                    "snapshot in a field would make it highlight the earlier passage.");
            }
            finally
            {
                view.Close();
                host.Close();
            }
        });
    }

    [TestMethod]
    public void A_menu_opened_with_nothing_selected_disables_the_actions_that_need_one()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            var view = Open(library, project, out var host);

            try
            {
                var menu = OpenContextMenu(view);

                Assert.IsFalse(Item(menu, "Выделить").IsEnabled);
                Assert.IsFalse(Item(menu, "Добавить пометку").IsEnabled);
                Assert.IsTrue(Item(menu, "Добавить закладку").IsEnabled,
                    "A bookmark works on where the reader is, not on what they selected.");
            }
            finally
            {
                view.Close();
                host.Close();
            }
        });
    }

    // ── The panel ────────────────────────────────────────────────────────────

    [TestMethod]
    public void A_highlight_updates_the_counts_without_leaving_the_page()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            var view = Open(library, project, out var host);

            try
            {
                SelectPassage(view, "декларативный язык");
                Press(Toolbar(view, "HighlightButton"));
                Pump(() => true, milliseconds: 100);

                StringAssert.Contains(
                    view.Project!.Summary(),
                    "1 выделение",
                    "The mark must be in the project's own counts the moment it is made.");

                // The status line carries the acknowledgement first and the counts after
                // it, so this is the line the reader is actually left looking at.
                Assert.IsTrue(
                    Pump(() => Summary(view).Contains("1 выделение", StringComparison.OrdinalIgnoreCase),
                        milliseconds: 4000),
                    $"The status line must settle back to the counts. It reads: «{Summary(view)}».");
            }
            finally
            {
                view.Close();
                host.Close();
            }
        });
    }

    [TestMethod]
    public void A_bookmark_appears_in_the_panel_immediately()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            var view = Open(library, project, out var host);

            try
            {
                Press(Toolbar(view, "BookmarkButton"));
                Pump(() => true, milliseconds: 150);

                Assert.AreEqual(1, MarksList(view).Children.Count,
                    "The bookmark must be drawn without the reader navigating away and back.");

                // The second one is the case that was broken: the tab was already
                // «Закладки», so no Checked event fired and nothing redrew.
                Press(Toolbar(view, "BookmarkButton"));
                Pump(() => true, milliseconds: 150);

                Assert.AreEqual(2, MarksList(view).Children.Count,
                    "Adding a second mark to the tab that is already showing must redraw it. " +
                    "Relying on ToggleButton.Checked meant the second one never appeared.");
            }
            finally
            {
                view.Close();
                host.Close();
            }
        });
    }

    [TestMethod]
    public void A_card_appears_in_the_panel_and_in_the_counts_immediately()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            var view = Open(library, project, out var host);

            try
            {
                view.Project!.Cards.Add(new StudyCard { Front = "Что такое SQL?", Back = "Язык запросов." });
                ShowCardsTab(view);
                Pump(() => true, milliseconds: 150);

                Assert.AreEqual(1, MarksList(view).Children.Count);
            }
            finally
            {
                view.Close();
                host.Close();
            }
        });
    }

    [TestMethod]
    public void Deleting_a_mark_from_the_panel_redraws_the_panel()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            var view = Open(library, project, out var host);

            try
            {
                Press(Toolbar(view, "BookmarkButton"));
                Press(Toolbar(view, "BookmarkButton"));
                Pump(() => true, milliseconds: 150);
                Assert.AreEqual(2, MarksList(view).Children.Count);

                var remove = MarksList(view).Children.OfType<DependencyObject>()
                    .SelectMany(Descendants<Button>)
                    .First();

                Press(remove);
                Pump(() => true, milliseconds: 150);

                Assert.AreEqual(1, MarksList(view).Children.Count);
                Assert.AreEqual(1, view.Project!.Bookmarks.Count);
            }
            finally
            {
                view.Close();
                host.Close();
            }
        });
    }

    // ── Lifetime ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Closing_the_book_while_a_type_change_is_settling_does_not_throw()
    {
        WpfTestHost.Run(() =>
        {
            var library = NewLibrary();
            var project = library.OpenOrCreate(_bookPath);
            var view = Open(library, project, out var host);

            try
            {
                Press(Toolbar(view, "FontLargerButton"));

                // Closed inside the beat the position restore was queued for.
                view.Close();

                Pump(() => true, milliseconds: 400);

                Assert.IsNull(view.Project is null ? null : Canvas(view).Document.Blocks.FirstBlock,
                    "The canvas is empty after closing, and nothing queued against the old " +
                    "document ran against the new state.");
            }
            finally
            {
                host.Close();
            }
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void ShowCardsTab(ReaderView view) =>
        ((System.Windows.Controls.RadioButton)view.FindName("TabCards")!).IsChecked = true;

    /// <summary>
    /// The status line as a screen reader would hear it.
    /// </summary>
    /// <remarks>
    /// <c>Type.Tracked</c> renders each character with a thin space between them for the
    /// wide mono eyebrow, and mirrors the real string into the automation name. Reading
    /// <c>Text</c> here would compare against "В Ы Д Е Л Е Н И Е".
    /// </remarks>
    private static string Summary(ReaderView view) =>
        System.Windows.Automation.AutomationProperties.GetName(
            (System.Windows.Controls.TextBlock)view.FindName("SummaryText")!) ?? string.Empty;

    private static string PaintedText(RichTextBox canvas)
    {
        var builder = new StringBuilder();

        foreach (var paragraph in canvas.Document.Blocks.OfType<Paragraph>())
        {
            foreach (var run in paragraph.Inlines.OfType<Run>())
            {
                if (run.ReadLocalValue(TextElement.BackgroundProperty) != DependencyProperty.UnsetValue)
                {
                    builder.Append(run.Text);
                }
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
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
