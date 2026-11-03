using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WriteLite.Services.Notes;
using WriteLite.Views.Pages;

namespace WriteLite.Tests;

/// <summary>
/// Renders pages to PNG for design review. Runs only when WRITELITE_SNAPSHOT_DIR is set, so the
/// normal suite never writes files; the pages are built with sample data in a temp folder.
/// </summary>
[TestClass]
public sealed class DesignSnapshotTests
{
    private static string? Dir => Environment.GetEnvironmentVariable("WRITELITE_SNAPSHOT_DIR");

    private static void Save(FrameworkElement element, string name, double width = 1100, double height = 720)
    {
        var window = new Window
        {
            Content = element,
            Width = width,
            Height = height,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -10000,
            Top = -10000,
        };
        window.SetResourceReference(Window.BackgroundProperty, "WlBgBase");
        window.Show();
        window.UpdateLayout();
        element.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Dir!, name + ".png"));
        encoder.Save(file);
        window.Close();
    }

    private static NotesService SampleNotes(string folder)
    {
        var notes = new NotesService(Path.Combine(folder, "notes.json"));
        var work = notes.CreateProject("Работа");
        notes.CreateProject("Учёба");
        var n1 = notes.Create(NoteKind.Note, "Идеи для отпуска");
        n1.Text = "Посмотреть билеты в Калининград, взять палатку и новый фотоаппарат.";
        notes.Update(n1);
        var n2 = notes.Create(NoteKind.Checklist, "Покупки", work.Id);
        n2.Items = [new NoteChecklistItem { Text = "Молоко", IsDone = true }, new NoteChecklistItem { Text = "Хлеб" }, new NoteChecklistItem { Text = "Кофе" }];
        notes.Update(n2);
        var n3 = notes.Create(NoteKind.Event, "Созвон с командой");
        n3.DueDate = new DateTimeOffset(DateTime.Today.AddHours(15.5));
        notes.Update(n3);
        var n4 = notes.Create(NoteKind.Task, "Сдать отчёт");
        n4.DueDate = new DateTimeOffset(DateTime.Today.AddDays(2));
        n4.IsAllDay = true;
        notes.Update(n4);
        notes.SetPinned(n1.Id, true);
        return notes;
    }

    [TestMethod]
    public void Notes_page_snapshots()
    {
        if (string.IsNullOrEmpty(Dir)) Assert.Inconclusive("WRITELITE_SNAPSHOT_DIR not set");
        var folder = Path.Combine(Path.GetTempPath(), "wl-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        WpfTestHost.Run(() =>
        {
            WpfTestHost.EnsureThemeApplied();
            var notes = SampleNotes(folder);
            var page = new NotesPage();
            page.Bind(notes);
            Save(page, "notes-list");

            page = new NotesPage();
            page.Bind(notes);
            page.Open(notes.All.First(n => n.Title == "Покупки"));
            Save(page, "notes-open");

            page = new NotesPage();
            page.Bind(notes);
            var calendar = (WriteLite.Views.Notes.NotesCalendarView)page.FindName("CalendarView")!;
            foreach (var child in ((System.Windows.Controls.StackPanel)page.FindName("SmartFolders")!).Children)
            {
                if (child is System.Windows.Controls.RadioButton { Tag: "Calendar" } button) button.IsChecked = true;
            }

            calendar.SelectDay(DateTime.Today);
            Save(page, "notes-calendar");
            notes.Dispose();
        });
    }
}
