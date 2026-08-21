using System.IO;
using System.Windows;
using System.Windows.Threading;
using WriteLite.Controls;
using WriteLite.Services.Notes;
using WriteLite.Views.Pages;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;

namespace WriteLite.Tests.Notes;

/// <summary>
/// Ticking a task off, which is the whole point of the board.
/// </summary>
/// <remarks>
/// The reported defect was that the checkmark never appeared and the application
/// stopped responding. The cause was structural rather than visual: every mutation
/// raised <c>Changed</c>, the page answered by clearing the board and constructing a
/// fresh <see cref="NoteCard"/> for every note, and the card holding the checkbox the
/// user had just clicked was therefore destroyed within a dispatcher hop — before any
/// frame of the two-hundred-millisecond tick could be drawn. Fifty quick ticks meant
/// fifty full rebuilds queued behind fifty clicks.
///
/// These tests pin the properties that make that impossible to reintroduce: the card
/// survives its own gesture, the board reconciles rather than rebuilds, and a burst of
/// toggling leaves the store consistent.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class NotesCompletionTests
{
    private string _directory = string.Empty;
    private string _path = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "wl-notes-done-" + Guid.NewGuid().ToString("N"));
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
            Thread.Sleep(8);
        }
    }

    private void WithBoard(NotesService notes, Action<NotesPage> body) => WpfTestHost.Run(() =>
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
            body(page);
        }
        finally
        {
            window.Close();
        }
    });

    private static NoteBoard Board(NotesPage page) => (NoteBoard)page.FindName("Board")!;

    private static NoteCard[] Cards(NotesPage page) => [.. Board(page).Cards];

    private static CheckBox DoneBox(NoteCard card) => (CheckBox)card.FindName("DoneBox")!;

    // ── The gesture ──────────────────────────────────────────────────────────

    [TestMethod]
    public void The_ticked_card_is_still_the_same_card_a_moment_later()
    {
        var notes = new NotesService(_path);
        notes.Create(NoteKind.Task, "Отправить отчёт");
        notes.Create(NoteKind.Task, "Позвонить в банк");

        WithBoard(notes, page =>
        {
            var before = Cards(page);
            var survivor = before.Single(card => card.Note!.DisplayTitle == "Позвонить в банк");

            DoneBox(before.Single(card => card.Note!.DisplayTitle == "Отправить отчёт")).IsChecked = true;

            // One dispatcher turn: long enough for the old code to have thrown the
            // whole board away, far too short for any animation to have finished.
            Pump(60);

            Assert.AreSame(survivor, Cards(page).Single(card => card.Note!.DisplayTitle == "Позвонить в банк"),
                "Completing one card must not rebuild the others. Every card being " +
                "reconstructed on every change is what made the board freeze.");
        });

        notes.Dispose();
    }

    [TestMethod]
    public void The_checkbox_shows_its_tick_before_the_card_leaves_the_board()
    {
        var notes = new NotesService(_path);
        notes.Create(NoteKind.Task, "Отправить отчёт");

        WithBoard(notes, page =>
        {
            var card = Cards(page).Single();
            var box = DoneBox(card);

            box.IsChecked = true;
            Pump(60);

            Assert.IsTrue(box.IsChecked == true,
                "The box must still be ticked a frame later — the card it lives on used " +
                "to be destroyed before the tick could be drawn.");

            Assert.IsTrue(Cards(page).Contains(card),
                "And the card must still be on the board while its tick is being drawn.");
        });

        notes.Dispose();
    }

    [TestMethod]
    public void A_completed_card_restyles_itself_immediately()
    {
        var notes = new NotesService(_path);
        notes.Create(NoteKind.Task, "Отправить отчёт");

        WithBoard(notes, page =>
        {
            var card = Cards(page).Single();
            var title = (System.Windows.Controls.TextBlock)card.FindName("TitleText")!;

            Assert.IsNull(title.TextDecorations, "Precondition: the task is open.");

            DoneBox(card).IsChecked = true;

            // No pump at all. The card's own appearance is not something to wait for.
            Assert.IsNotNull(title.TextDecorations,
                "The card strikes its own title through the instant it is ticked, rather " +
                "than waiting for a round trip through the store.");
        });

        notes.Dispose();
    }

    [TestMethod]
    public void A_completed_task_leaves_the_active_board_once_its_gesture_has_played()
    {
        var notes = new NotesService(_path);
        var task = notes.Create(NoteKind.Task, "Уходящая задача");

        WithBoard(notes, page =>
        {
            DoneBox(Cards(page).Single()).IsChecked = true;
            Pump(700);

            Assert.AreEqual(0, Cards(page).Length, "A finished card leaves the active board …");
            Assert.IsTrue(notes.Find(task.Id)!.IsCompleted);

            ((RadioButton)page.FindName("TabCompleted")!).IsChecked = true;
            Pump();

            Assert.AreEqual(1, Cards(page).Length, "… and appears under «Выполненные».");
        });

        notes.Dispose();
    }

    [TestMethod]
    public void Unticking_before_the_card_has_finished_leaving_puts_it_back()
    {
        var notes = new NotesService(_path);
        var task = notes.Create(NoteKind.Task, "Передумал");

        WithBoard(notes, page =>
        {
            var card = Cards(page).Single();

            DoneBox(card).IsChecked = true;
            Pump(40);

            DoneBox(card).IsChecked = false;
            Pump(700);

            var remaining = Cards(page);

            Assert.AreEqual(1, remaining.Length, "The card must come back, once.");
            Assert.AreSame(card, remaining[0], "And be the same card, not a ghost beside a new one.");
            Assert.AreEqual(1, remaining[0].Opacity, 0.001, "Fully restored, not left mid-fade.");
            Assert.IsFalse(notes.Find(task.Id)!.IsCompleted);
        });

        notes.Dispose();
    }

    // ── Under load ───────────────────────────────────────────────────────────

    [TestMethod]
    public void Fifty_rapid_toggles_leave_the_store_consistent()
    {
        var notes = new NotesService(_path);
        var task = notes.Create(NoteKind.Task, "Туда-сюда");

        WithBoard(notes, page =>
        {
            for (var round = 0; round < 50; round++)
            {
                // Read from the store rather than from the board: the card leaves the
                // active tab when it is completed and comes back when it is not.
                notes.SetCompleted(task.Id, round % 2 == 0);
            }

            Pump(1000);

            Assert.IsFalse(notes.Find(task.Id)!.IsCompleted,
                "Fifty toggles ending on «open» must leave the task open.");
            Assert.AreEqual(1, notes.All.Count, "And must not have duplicated the note.");
            Assert.AreEqual(1, Cards(page).Length, "Nor the card.");
        });

        notes.Dispose();

        var reopened = new NotesService(_path);
        Assert.AreEqual(1, reopened.All.Count);
        Assert.IsFalse(reopened.Find(task.Id)!.IsCompleted, "And the last state is the one on disk.");
        reopened.Dispose();
    }

    [TestMethod]
    public void Toggling_a_card_on_screen_fifty_times_keeps_one_card_on_the_board()
    {
        var notes = new NotesService(_path);
        notes.Create(NoteKind.Task, "Быстрые щелчки");

        WithBoard(notes, page =>
        {
            for (var round = 0; round < 50; round++)
            {
                var card = Cards(page).FirstOrDefault();
                if (card is null)
                {
                    // Completed and gone; bring it back through the store the way the
                    // «Выполненные» tab would.
                    notes.SetCompleted(notes.All.Single().Id, false);
                    Pump(30);
                    continue;
                }

                DoneBox(card).IsChecked = DoneBox(card).IsChecked != true;
                Pump(20);
            }

            Pump(1000);

            var onBoard = Cards(page);
            Assert.IsTrue(
                onBoard.Length <= 1,
                "The board must never accumulate duplicates. On the board: " +
                string.Join(" | ", onBoard.Select(card =>
                    $"{card.Note?.Id[..6]} done={card.Note?.IsCompleted} opacity={card.Opacity:0.00}")));

            Assert.AreEqual(1, notes.All.Count);
        });

        notes.Dispose();
    }

    [TestMethod]
    public void Searching_reuses_the_cards_that_still_match()
    {
        var notes = new NotesService(_path);
        notes.Create(NoteKind.Note, "Заметка про кофе");
        notes.Create(NoteKind.Note, "Заметка про чай");

        WithBoard(notes, page =>
        {
            var coffee = Cards(page).Single(card => card.Note!.DisplayTitle.Contains("кофе"));

            ((System.Windows.Controls.TextBox)page.FindName("SearchBox")!).Text = "кофе";
            Pump(600);

            Assert.AreEqual(1, Cards(page).Length);
            Assert.AreSame(coffee, Cards(page).Single(),
                "A card that still matches must be kept, not rebuilt. Typing in the search " +
                "box used to reconstruct every card on the board per keystroke.");
        });

        notes.Dispose();
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void Binding_the_page_twice_does_not_leave_two_subscriptions()
    {
        var notes = new NotesService(_path);
        notes.Create(NoteKind.Task, "Одна задача");

        WithBoard(notes, page =>
        {
            page.Bind(notes);
            page.Bind(notes);
            Pump(300);

            Assert.AreEqual(1, Cards(page).Length,
                "Re-binding must replace the subscription rather than add another. " +
                "The page was holding the service's Changed event with a lambda it " +
                "never removed, so every bind added a redraw and kept the page alive.");
        });

        notes.Dispose();
    }
}
