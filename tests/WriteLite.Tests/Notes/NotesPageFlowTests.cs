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
/// The three-pane notes page driven through its own controls.
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

    private static string[] Rows(NotesPage page, NotesService notes) =>
        ((System.Windows.Controls.StackPanel)page.FindName("NoteList")!).Children
            .OfType<System.Windows.Controls.Border>()
            .Select(row => notes.Find((string)row.Tag)!.DisplayTitle)
            .ToArray();

    [TestMethod]
    public void Stored_notes_appear_in_the_list_newest_first()
    {
        var notes = new NotesService(_path);
        notes.Create(NoteKind.Task, "Первая задача");
        Thread.Sleep(20);
        notes.Create(NoteKind.Note, "Вторая заметка");

        WithBoard(notes, (page, _) =>
        {
            CollectionAssert.AreEqual(new[] { "Вторая заметка", "Первая задача" }, Rows(page, notes));
        });

        notes.Dispose();
    }

    [TestMethod]
    public void Deleting_moves_a_note_to_the_trash_and_restoring_brings_it_back()
    {
        var notes = new NotesService(_path);
        var note = notes.Create(NoteKind.Note, "Удаляемая");
        notes.Create(NoteKind.Note, "Остаётся");

        WithBoard(notes, (page, _) =>
        {
            notes.MoveToTrash(note.Id);
            Pump();
            CollectionAssert.AreEqual(new[] { "Остаётся" }, Rows(page, notes));

            page.SelectFolder(NotesFolderKind.Trash, null);
            Pump();
            CollectionAssert.AreEqual(new[] { "Удаляемая" }, Rows(page, notes));

            notes.Restore(note.Id);
            Pump();
            Assert.AreEqual(0, Rows(page, notes).Length, "The trash empties when the note is restored.");
        });

        notes.Dispose();
        var reopened = new NotesService(_path);
        Assert.IsFalse(reopened.Find(note.Id)!.IsDeleted, "Restoring is written to disk.");
        reopened.Dispose();
    }

    [TestMethod]
    public void Trash_older_than_thirty_days_is_purged()
    {
        var notes = new NotesService(_path);
        var old = notes.Create(NoteKind.Note, "Старая");
        var recent = notes.Create(NoteKind.Note, "Свежая");
        notes.MoveToTrash(old.Id);
        notes.MoveToTrash(recent.Id);

        var removed = notes.PurgeExpiredTrash(DateTimeOffset.Now.AddDays(31));
        Assert.AreEqual(2, removed);
        Assert.IsNull(notes.Find(old.Id));
        notes.Dispose();
    }

    [TestMethod]
    public void Completed_tasks_are_in_their_own_folder()
    {
        var notes = new NotesService(_path);
        var task = notes.Create(NoteKind.Task, "Купить кофе");
        notes.SetCompleted(task.Id, true);
        notes.Create(NoteKind.Task, "Открытая задача");

        WithBoard(notes, (page, _) =>
        {
            page.SelectFolder(NotesFolderKind.Completed, null);
            Pump();
            CollectionAssert.AreEqual(new[] { "Купить кофе" }, Rows(page, notes));

            page.SelectFolder(NotesFolderKind.Tasks, null);
            Pump();
            CollectionAssert.AreEqual(new[] { "Открытая задача" }, Rows(page, notes));
        });

        notes.Dispose();
    }

    [TestMethod]
    public void Search_filters_the_list()
    {
        var notes = new NotesService(_path);
        notes.Create(NoteKind.Note, "Купить кофе");
        notes.Create(NoteKind.Note, "Позвонить маме");

        WithBoard(notes, (page, _) =>
        {
            ((TextBox)page.FindName("SearchBox")!).Text = "кофе";
            Pump();
            CollectionAssert.AreEqual(new[] { "Купить кофе" }, Rows(page, notes));
        });

        notes.Dispose();
    }

    [TestMethod]
    public void The_empty_list_explains_itself()
    {
        var notes = new NotesService(_path);
        WithBoard(notes, (page, _) =>
        {
            var empty = (FrameworkElement)page.FindName("ListEmpty")!;
            Assert.AreEqual(Visibility.Visible, empty.Visibility);
            Assert.IsFalse(string.IsNullOrWhiteSpace(((System.Windows.Controls.TextBlock)page.FindName("ListEmptyHint")!).Text));
        });

        notes.Dispose();
    }

    [TestMethod]
    public void Leaving_a_new_empty_note_discards_it()
    {
        var notes = new NotesService(_path);
        var kept = notes.Create(NoteKind.Note, "Есть текст");

        WithBoard(notes, (page, _) =>
        {
            page.CreateNote();
            Pump();
            var blank = page.Editor.NoteId;
            page.Open(notes.Find(kept.Id)!);
            Pump();
            Assert.IsNull(notes.Find(blank), "A note created and left without a word in it is withdrawn.");
        });

        notes.Dispose();
    }

    [TestMethod]
    public void Calendar_adds_a_timed_event_on_the_selected_day()
    {
        var notes = new NotesService(_path);
        WithBoard(notes, (page, _) =>
        {
            var calendar = (WriteLite.Views.Notes.NotesCalendarView)page.FindName("CalendarView")!;
            var day = new DateTime(2026, 11, 12);
            calendar.SelectDay(day);
            var created = calendar.AddOnSelectedDay("Созвон с командой 15:30", NoteKind.Event)!;
            Assert.AreEqual("Созвон с командой", created.Title);
            Assert.AreEqual(day.AddHours(15.5), created.DueDate!.Value.DateTime);
            Assert.IsFalse(created.IsAllDay);
            Assert.AreEqual(1, calendar.ItemsOn(day).Count);

            var allDay = calendar.AddOnSelectedDay("День рождения Ани", NoteKind.Event)!;
            Assert.IsTrue(allDay.IsAllDay);
            Assert.AreEqual(2, calendar.ItemsOn(day).Count);
        });

        notes.Dispose();
    }

    [TestMethod]
    [DataRow("Созвон 15:30", "Созвон", 15, 30)]
    [DataRow("Обед в 13", "Обед", 13, 0)]
    [DataRow("Встреча в 9.15", "Встреча", 9, 15)]
    public void Typed_times_are_read(string text, string title, int hours, int minutes)
    {
        var (t, time) = WriteLite.Views.Notes.NotesCalendarView.SplitTime(text);
        Assert.AreEqual(title, t);
        Assert.AreEqual(new TimeSpan(hours, minutes, 0), time);
    }

    [TestMethod]
    public void A_title_with_a_number_is_not_mistaken_for_a_time()
    {
        var (t, time) = WriteLite.Views.Notes.NotesCalendarView.SplitTime("Купить 2");
        Assert.AreEqual("Купить 2", t);
        Assert.IsNull(time);
    }
}
