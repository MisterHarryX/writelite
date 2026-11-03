using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using WriteLite.Controls;
using WriteLite.Services.Notes;
using WriteLite.Views.Notes;
using WriteLite.Views.Pages;
using CheckBox = System.Windows.Controls.CheckBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace WriteLite.Tests.Notes;

/// <summary>
/// The note editor and the projects page driven through their own controls: what is typed
/// reaches the store, AI results go back without flattening formatting, and the project
/// overview's quick task field really creates tasks in the project.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NoteEditorFlowTests
{
    private string _directory = string.Empty;
    private string _path = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "wl-noteeditor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "notes.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not a test failure.
        }
    }

    private static void Pump(int milliseconds = 200)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private static void Host(FrameworkElement content, Action body)
    {
        WpfTestHost.EnsureThemeApplied();
        var window = new Window
        {
            Width = 1280,
            Height = 820,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
            Content = content
        };

        window.Show();
        window.UpdateLayout();
        Pump();

        try
        {
            body();
        }
        finally
        {
            window.Close();
        }
    }

    // ── Writing results back ─────────────────────────────────────────────────

    [TestMethod]
    public void An_accepted_fix_keeps_the_bold_word_next_to_it_bold() => WpfTestHost.Run(() =>
    {
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run("Я пошол в "));
        paragraph.Inlines.Add(new Bold(new Run("магазин")));
        paragraph.Inlines.Add(new Run(" вечером."));
        var document = new FlowDocument(paragraph);

        var piece = RichTextPieces.WholeDocument(document).Single();
        Assert.AreEqual("Я пошол в магазин вечером.", piece.Text);

        Assert.IsTrue(RichTextPieces.Apply(piece, "Я пошёл в магазин вечером."));

        Assert.AreEqual("Я пошёл в магазин вечером.", RichTextPieces.TextOf(paragraph));
        var bold = paragraph.Inlines.OfType<Bold>().Single();
        Assert.AreEqual("магазин", new TextRange(bold.ContentStart, bold.ContentEnd).Text);
    });

    [TestMethod]
    public void A_result_for_text_that_changed_meanwhile_is_not_written() => WpfTestHost.Run(() =>
    {
        var paragraph = new Paragraph(new Run("Исходный текст абзаца."));
        var document = new FlowDocument(paragraph);
        var piece = RichTextPieces.WholeDocument(document).Single();

        ((Run)paragraph.Inlines.FirstInline).Text = "Пользователь успел всё переписать.";

        Assert.IsFalse(RichTextPieces.Apply(piece, "Предложенный текст абзаца."));
        Assert.AreEqual("Пользователь успел всё переписать.", RichTextPieces.TextOf(paragraph));
    });

    [TestMethod]
    public void A_selection_is_read_as_the_covered_part_of_each_paragraph() => WpfTestHost.Run(() =>
    {
        var first = new Paragraph(new Run("Первый абзац целиком."));
        var second = new Paragraph(new Run("Второй абзац тоже."));
        var document = new FlowDocument();
        document.Blocks.Add(first);
        document.Blocks.Add(second);

        var start = RichTextPieces.PositionAt(first, 7);
        var end = RichTextPieces.PositionAt(second, 6);
        var pieces = RichTextPieces.FromSelection(document, start, end);

        Assert.AreEqual(2, pieces.Count);
        Assert.AreEqual("абзац целиком.", pieces[0].Text);
        Assert.AreEqual("Второй", pieces[1].Text);

        Assert.IsTrue(RichTextPieces.Apply(pieces[1], "Третий"));
        Assert.AreEqual("Третий абзац тоже.", RichTextPieces.TextOf(second));
    });

    [TestMethod]
    public void A_checklist_line_offers_only_its_text_to_the_model() => WpfTestHost.Run(() =>
    {
        var paragraph = NoteDocumentMapper.BuildParagraph(
            new NoteBlock { Kind = NoteBlockKind.Check, Spans = [new NoteSpan { Text = "Купит хлеб" }] },
            new Embeds());
        var document = new FlowDocument(paragraph);

        var piece = RichTextPieces.WholeDocument(document).Single();
        Assert.AreEqual("Купит хлеб", piece.Text);
        Assert.IsTrue(RichTextPieces.Apply(piece, "Купить хлеб"));
        Assert.IsNotNull(NoteDocumentMapper.FindCheck(paragraph), "The tick box stays.");
        Assert.AreEqual("Купить хлеб", RichTextPieces.TextOf(paragraph));
    });

    private sealed class Embeds : INoteEmbedFactory
    {
        public UIElement CreateImage(NoteBlock block) => new Border();

        public NoteSketchSurface CreateSketch(NoteBlock block) => new(block.Ink, block.Height);

        public CheckBox CreateCheck(bool isChecked) => new() { IsChecked = isChecked };
    }

    // ── The editor ───────────────────────────────────────────────────────────

    [TestMethod]
    public void What_is_typed_in_the_editor_reaches_the_store() => WpfTestHost.Run(() =>
    {
        var notes = new NotesService(_path);
        var note = notes.Create(NoteKind.Note, "Черновик");
        var editor = new NoteEditorView();

        Host(editor, () =>
        {
            editor.Open(notes, note, ai: null, "Все записи");
            Pump();

            ((Paragraph)editor.Body.Document.Blocks.LastBlock).ContentEnd.InsertTextInRun("Новая мысль");
            editor.Commit();

            var stored = notes.Find(note.Id)!;
            Assert.IsNotNull(stored.Content);
            StringAssert.Contains(stored.Text, "Новая мысль");
            Assert.AreEqual("Черновик", stored.Title);
        });

        notes.Dispose();
    });

    [TestMethod]
    public void An_edit_still_held_by_the_editor_is_saved_when_the_store_shuts_down() => WpfTestHost.Run(() =>
    {
        var notes = new NotesService(_path);
        var note = notes.Create(NoteKind.Note, "Перед выходом");
        var editor = new NoteEditorView();

        Host(editor, () =>
        {
            editor.Open(notes, note, ai: null, "Все записи");
            Pump();

            ((Paragraph)editor.Body.Document.Blocks.LastBlock).ContentEnd.InsertTextInRun("Последняя строка");

            // No commit: the application exits inside the editor's own debounce.
            notes.Dispose();
        });

        var reopened = new NotesService(_path);
        StringAssert.Contains(reopened.Find(note.Id)!.Text, "Последняя строка");
        reopened.Dispose();
    });

    [TestMethod]
    public void A_note_created_and_left_empty_is_withdrawn_on_close() => WpfTestHost.Run(() =>
    {
        var notes = new NotesService(_path);
        var note = notes.Create(NoteKind.Note);
        var editor = new NoteEditorView();

        Host(editor, () =>
        {
            editor.Open(notes, note, ai: null, "Все записи");
            Pump();
            editor.Close();
        });

        Assert.IsNull(notes.Find(note.Id));
        notes.Dispose();
    });

    [TestMethod]
    public void An_inserted_picture_is_stored_and_referenced_by_the_note() => WpfTestHost.Run(() =>
    {
        var notes = new NotesService(_path);
        var note = notes.Create(NoteKind.Note, "Скриншоты");
        var file = Path.Combine(_directory, "shot.png");
        File.WriteAllBytes(file, NoteContentPersistenceTests.TinyPng);
        var editor = new NoteEditorView();

        Host(editor, () =>
        {
            editor.Open(notes, note, ai: null, "Все записи");
            Pump();

            Assert.IsTrue(editor.InsertImageFile(file));
            editor.Commit();
        });

        notes.Dispose();
        var reopened = new NotesService(_path);
        var image = reopened.Find(note.Id)!.Content!.Single(block => block.Kind == NoteBlockKind.Image);
        Assert.IsNotNull(reopened.Assets.PathOf(note.Id, image.AssetId), "The picture survives the restart's sweep.");
        reopened.Dispose();
    });

    // ── The projects page ────────────────────────────────────────────────────

    [TestMethod]
    public void A_folder_shows_only_its_notes_and_new_notes_are_filed_in_it() => WpfTestHost.Run(() =>
    {
        var notes = new NotesService(_path);
        var project = notes.CreateProject("Переезд");
        notes.Create(NoteKind.Note, "Список вещей", project.Id);
        notes.Create(NoteKind.Note, "Не из папки");

        var page = new NotesPage();
        page.Bind(notes);

        Host(page, () =>
        {
            page.SelectFolder(NotesFolderKind.Project, project.Id);
            Pump();

            var rows = ((StackPanel)page.FindName("NoteList")!).Children.OfType<Border>()
                .Select(row => notes.Find((string)row.Tag)!.DisplayTitle).ToArray();
            CollectionAssert.AreEqual(new[] { "Список вещей" }, rows);

            page.CreateNote();
            Pump();
            var created = notes.Find(page.Editor.NoteId)!;
            Assert.AreEqual(project.Id, created.ProjectId, "A note made inside a folder is filed in it.");
        });

        notes.Dispose();
    });

    [TestMethod]
    public void Opening_a_card_shows_the_editor_and_going_back_shows_the_board() => WpfTestHost.Run(() =>
    {
        var notes = new NotesService(_path);
        var note = notes.Create(NoteKind.Note, "Открываемая");
        var page = new NotesPage();
        page.Bind(notes);

        Host(page, () =>
        {
            page.Open(note);
            Pump();
            Assert.AreEqual(Visibility.Visible, page.Editor.Visibility);
            Assert.AreEqual(note.Id, page.Editor.NoteId);

            page.Editor.Close();
            Pump();
            Assert.AreEqual(Visibility.Collapsed, page.Editor.Visibility);
            Assert.IsNotNull(notes.Find(note.Id), "A note with a title is kept.");
        });

        notes.Dispose();
    });
}
