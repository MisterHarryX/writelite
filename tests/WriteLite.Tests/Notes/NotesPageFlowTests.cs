using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using WriteLite.Controls;
using WriteLite.Services.Notes;
using WriteLite.Views.Pages;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace WriteLite.Tests.Notes;

/// <summary>
/// The notes board driven through its own controls.
/// </summary>
/// <remarks>
/// <see cref="NotesServiceTests"/> proves the rules; this proves they are reachable.
/// A card is built in code at runtime, so "the service works" says nothing about
/// whether a checkbox on a card is wired to it — which is the difference between a
/// feature and a class that happens to compile.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class NotesPageFlowTests
{
    private string _directory = string.Empty;
    private string _path = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "wl-notespage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "notes.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory left behind is not a test failure.
        }
    }

    private static void Pump(int milliseconds = 400)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private void WithBoard(NotesService notes, Action<NotesPage, Window> body) => WpfTestHost.Run(() =>
    {
        WpfTestHost.EnsureThemeApplied();

        var page = new NotesPage();
        page.Bind(notes);

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
        Pump();

        try
        {
            body(page, window);
        }
        finally
        {
            window.Close();
        }
    });

    private static NoteCard[] Cards(NotesPage page) =>
        ((NoteBoard)page.FindName("Board")!).Children.OfType<NoteCard>().ToArray();

    [TestMethod]
    public void Stored_notes_appear_on_the_board_as_cards()
    {
        var notes = new NotesService(_path);
        notes.Create(NoteKind.Task, "Первая задача");
        notes.Create(NoteKind.Goal, "Большая цель");
        notes.Create(NoteKind.Note, "Просто заметка");

        WithBoard(notes, (page, _) =>
        {
            var cards = Cards(page);

            Assert.AreEqual(3, cards.Length, "Every stored note must be drawn.");
            CollectionAssert.AreEquivalent(
                new[] { "Первая задача", "Большая цель", "Просто заметка" },
                cards.Select(card => card.Note!.DisplayTitle).ToArray());
        });

        notes.Dispose();
    }

    [TestMethod]
    public void Ticking_a_card_completes_the_note_and_writes_it_to_disk()
    {
        var notes = new NotesService(_path);
        var task = notes.Create(NoteKind.Task, "Отправить отчёт");

        WithBoard(notes, (page, _) =>
        {
            var card = Cards(page).Single();
            var box = (CheckBox)card.FindName("DoneBox")!;

            Assert.AreEqual(Visibility.Visible, box.Visibility,
                "A task must offer a way to finish it on the card itself.");

            // The gesture: the checkbox on the card, not a service call.
            box.IsChecked = true;
            Pump();

            Assert.IsTrue(notes.Find(task.Id)!.IsCompleted);
        });

        notes.Dispose();

        var reopened = new NotesService(_path);
        Assert.IsTrue(reopened.Find(task.Id)!.IsCompleted, "And it survives the restart.");
        reopened.Dispose();
    }

    [TestMethod]
    public void A_plain_note_offers_no_completion_checkbox()
    {
        var notes = new NotesService(_path);
        notes.Create(NoteKind.Note, "Мысль");

        WithBoard(notes, (page, _) =>
        {
            var box = (CheckBox)Cards(page).Single().FindName("DoneBox")!;
            Assert.AreEqual(Visibility.Collapsed, box.Visibility,
                "There is nothing to finish about a note.");
        });

        notes.Dispose();
    }

    [TestMethod]
    public void Completing_a_card_moves_it_to_the_completed_tab()
    {
        var notes = new NotesService(_path);
        var task = notes.Create(NoteKind.Task, "Уходящая задача");

        WithBoard(notes, (page, _) =>
        {
            notes.SetCompleted(task.Id, true);
            Pump();

            Assert.AreEqual(0, Cards(page).Length, "A finished card leaves the active board …");

            ((RadioButton)page.FindName("TabCompleted")!).IsChecked = true;
            Pump();

            Assert.AreEqual(1, Cards(page).Length, "… and appears under «Выполненные».");
        });

        notes.Dispose();
    }

    [TestMethod]
    public void Search_filters_the_board()
    {
        var notes = new NotesService(_path);
        notes.Create(NoteKind.Note, "Заметка про кофе");
        notes.Create(NoteKind.Note, "Заметка про чай");

        WithBoard(notes, (page, _) =>
        {
            Assert.AreEqual(2, Cards(page).Length);

            ((TextBox)page.FindName("SearchBox")!).Text = "кофе";
            Pump();

            Assert.AreEqual(1, Cards(page).Length);
            StringAssert.Contains(Cards(page).Single().Note!.DisplayTitle, "кофе");
        });

        notes.Dispose();
    }

    [TestMethod]
    public void Ticking_a_checklist_line_on_the_card_updates_the_store()
    {
        var notes = new NotesService(_path);
        var list = notes.Create(NoteKind.Checklist, "Сборы");
        var item = new NoteChecklistItem { Text = "Паспорт" };
        list.Items.Add(item);
        notes.Update(list);

        WithBoard(notes, (page, _) =>
        {
            var card = Cards(page).Single();

            var line = Descendants<CheckBox>(card)
                .FirstOrDefault(box => AutomationProperties.GetName(box) == "Паспорт");

            Assert.IsNotNull(line, "A checklist line must be tickable from the card face.");

            line.IsChecked = true;
            Pump();

            Assert.IsTrue(notes.Find(list.Id)!.Items.Single().IsDone);
        });

        notes.Dispose();

        var reopened = new NotesService(_path);
        Assert.IsTrue(reopened.Find(list.Id)!.Items.Single().IsDone);
        reopened.Dispose();
    }

    [TestMethod]
    public void The_empty_board_explains_itself_rather_than_showing_nothing()
    {
        var notes = new NotesService(_path);

        WithBoard(notes, (page, _) =>
        {
            var empty = (FrameworkElement)page.FindName("EmptyState")!;
            Assert.AreEqual(Visibility.Visible, empty.Visibility);

            var title = (System.Windows.Controls.TextBlock)page.FindName("EmptyTitle")!;
            Assert.IsFalse(string.IsNullOrWhiteSpace(title.Text));
        });

        notes.Dispose();
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
