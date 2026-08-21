using System.Windows;
using System.Windows.Threading;
using WriteLite.Controls;
using WriteLite.Services.Notes;
using WriteLite.Views.Pages;

namespace WriteLite.Tests;

/// <summary>
/// Renders every page against the real theme.
/// </summary>
/// <remarks>
/// Control templates are only expanded when a control is first laid out, so a broken
/// template throws <c>XamlParseException</c> at navigation time — long after the build
/// succeeded and the page's constructor ran. These tests measure and arrange each page
/// inside a hidden window, which applies every template and surfaces that failure in CI.
///
/// Not parallelised: every UI test marshals onto the one shared dispatcher, and
/// pumping it from inside one test would otherwise run another test's body nested
/// inside this one.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class PageRenderSmokeTests
{
    [TestMethod]
    public void Home_page_renders() => AssertRenders(() => new HomePage());

    [TestMethod]
    public void Editor_page_renders() => AssertRenders(() => new EditorPage());

    [TestMethod]
    public void Converter_page_renders() => AssertRenders(() => new ConverterPage());

    [TestMethod]
    public void Dictionary_page_renders() => AssertRenders(() => new DictionaryPage());

    [TestMethod]
    public void Word_manager_page_renders() => AssertRenders(() => new WordManagerPage());

    [TestMethod]
    public void Settings_page_renders() => AssertRenders(() => new SettingsPage());

    [TestMethod]
    public void Diagnostics_page_renders() => AssertRenders(() => new DiagnosticsPage());

    [TestMethod]
    public void Local_engine_page_renders() => AssertRenders(() => new LocalEnginePage());

    [TestMethod]
    public void Placeholder_page_renders() => AssertRenders(() =>
    {
        var page = new PlaceholderPage();
        page.Configure("09", "О программе", "Текст раздела.");
        return page;
    });

    [TestMethod]
    public void Notes_page_renders() => AssertRenders(() => new NotesPage());

    [TestMethod]
    public void Reading_page_renders() => AssertRenders(() => new ReadingPage());

    [TestMethod]
    public void Reader_view_renders() => AssertRenders(() => new ReaderView());

    /// <summary>
    /// Builds a card of every kind, with everything a card can carry.
    /// </summary>
    /// <remarks>
    /// A card is built in code at runtime rather than declared on a page, so it never
    /// goes through XAML compilation and the first thing that touches its resource
    /// keys is a user opening the board. That is exactly how a
    /// <c>BasedOn="{StaticResource …}"</c> pointing at another dictionary shipped as a
    /// crash that no build and no other test could see: the key resolves fine at
    /// compile time and throws the moment the first card is created.
    /// </remarks>
    [TestMethod]
    public void Note_cards_render_for_every_kind() => WpfTestHost.Run(() =>
    {
        WpfTestHost.EnsureThemeApplied();

        var panel = new NoteBoard();

        foreach (var kind in Enum.GetValues<NoteKind>())
        {
            var note = new Note
            {
                Kind = kind,
                Title = $"Карточка «{kind}»",
                Text = "Текст заметки, достаточно длинный, чтобы попасть в обрезку.",
                Category = "работа",
                DueDate = DateTimeOffset.Now.AddDays(-1),
                IsPinned = kind == NoteKind.Goal,
                Items =
                [
                    new NoteChecklistItem { Text = "Первый пункт", IsDone = true },
                    new NoteChecklistItem { Text = "Второй пункт" },
                    new NoteChecklistItem { Text = "Третий пункт" },
                    new NoteChecklistItem { Text = "Четвёртый пункт" },
                    new NoteChecklistItem { Text = "Пятый пункт" }
                ]
            };

            var card = new NoteCard();
            card.Bind(note);
            panel.AddCard(card);

            // And once more finished, which restyles the title and dims the card.
            var completed = new NoteCard();
            note.IsCompleted = true;
            note.CompletedAt = DateTimeOffset.Now;
            completed.Bind(note);
            panel.AddCard(completed);
        }

        var window = new Window
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
            Content = panel
        };

        window.Show();
        window.UpdateLayout();
        panel.Measure(new Size(1280, 800));
        panel.Arrange(new Rect(0, 0, 1280, 800));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        window.Close();
    });

    /// <summary>
    /// The dialogs, which are the other surfaces built only on demand.
    /// </summary>
    [TestMethod]
    public void Workspace_dialogs_render() => WpfTestHost.Run(() =>
    {
        WpfTestHost.EnsureThemeApplied();

        Window[] dialogs =
        [
            new WriteLite.Views.NoteEditorWindow(new Note
            {
                Kind = NoteKind.Checklist,
                Title = "Список",
                Items = [new NoteChecklistItem { Text = "Пункт" }]
            }),
            new WriteLite.Views.AnnotationEditorWindow("Цитата из книги."),
            new WriteLite.Views.StudyCardEditorWindow("Фрагмент для карточки.", null),
            new WriteLite.Views.TextPromptWindow("Название проекта", "Книга")
        ];

        foreach (var dialog in dialogs)
        {
            dialog.ShowInTaskbar = false;
            dialog.Opacity = 0;
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = -4000;
            dialog.Top = -4000;

            dialog.Show();
            dialog.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            dialog.Close();
        }
    });

    private static void AssertRenders(Func<FrameworkElement> factory) => WpfTestHost.Run(() =>
    {
        WpfTestHost.EnsureThemeApplied();

        var page = factory();

        // A hidden host window gives the page a presentation source, which is what
        // triggers template expansion for every control it contains.
        //
        // Parked off-screen rather than at the default centred position. A window
        // under the real mouse pointer puts controls into their hover state, which
        // starts the hover storyboards; those are still running when the test shuts
        // down its dispatcher a moment later, and an animation clock outliving its
        // dispatcher takes the whole test host down.
        var window = new Window
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
            Content = page
        };

        window.Show();
        window.UpdateLayout();
        page.Measure(new Size(1280, 800));
        page.Arrange(new Rect(0, 0, 1280, 800));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        window.Close();
    });
}
