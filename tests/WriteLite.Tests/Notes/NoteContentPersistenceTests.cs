using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using WriteLite.Services.Notes;
using WriteLite.Services.Storage;
using WriteLite.Views.Notes;
using CheckBox = System.Windows.Controls.CheckBox;

namespace WriteLite.Tests.Notes;

/// <summary>
/// Rich note bodies, pictures and drawings: everything a note holds must come back exactly
/// as it was saved — through the JSON file, through the editor's document, and through a
/// restart of the store.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NoteContentPersistenceTests
{
    /// <summary>A valid 1×1 PNG.</summary>
    internal static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private string _directory = string.Empty;
    private string _path = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "wl-notecontent-" + Guid.NewGuid().ToString("N"));
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

    private NotesService Restart(NotesService service)
    {
        service.Dispose();
        return new NotesService(_path);
    }

    private static string Json(IEnumerable<NoteBlock> blocks) =>
        JsonSerializer.Serialize(blocks, AtomicJsonFile.Options);

    internal static List<NoteBlock> EveryKindOfBlock(string? assetId = null, string? ink = null) =>
    [
        new() { Kind = NoteBlockKind.Heading1, Spans = [new NoteSpan { Text = "План" }], BookmarkId = "top", BookmarkName = "Начало" },
        new()
        {
            Kind = NoteBlockKind.Paragraph,
            Spans =
            [
                new NoteSpan { Text = "Обычный, " },
                new NoteSpan { Text = "жирный", Bold = true },
                new NoteSpan { Text = ", " },
                new NoteSpan { Text = "курсив", Italic = true },
                new NoteSpan { Text = ", " },
                new NoteSpan { Text = "подчёркнутый", Underline = true },
                new NoteSpan { Text = ", " },
                new NoteSpan { Text = "зачёркнутый", Strike = true },
                new NoteSpan { Text = " и " },
                new NoteSpan { Text = "маркер", Highlight = true },
                new NoteSpan { Text = "." }
            ]
        },
        new() { Kind = NoteBlockKind.Heading2, Spans = [new NoteSpan { Text = "Задачи" }] },
        new() { Kind = NoteBlockKind.Check, Spans = [new NoteSpan { Text = "Купить билеты" }], IsChecked = true },
        new() { Kind = NoteBlockKind.Check, Spans = [new NoteSpan { Text = "Забронировать отель" }] },
        new() { Kind = NoteBlockKind.Bullet, Spans = [new NoteSpan { Text = "Первый пункт" }] },
        new() { Kind = NoteBlockKind.Bullet, Spans = [new NoteSpan { Text = "Вложенный" }], Indent = 1 },
        new() { Kind = NoteBlockKind.Bullet, Spans = [new NoteSpan { Text = "Снова первый уровень" }] },
        new() { Kind = NoteBlockKind.Divider },
        new() { Kind = NoteBlockKind.Numbered, Spans = [new NoteSpan { Text = "Раз" }] },
        new() { Kind = NoteBlockKind.Numbered, Spans = [new NoteSpan { Text = "Два" }] },
        new() { Kind = NoteBlockKind.Quote, Spans = [new NoteSpan { Text = "Цитата\nв две строки" }] },
        new() { Kind = NoteBlockKind.Heading3, Spans = [new NoteSpan { Text = "Схема" }] },
        new() { Kind = NoteBlockKind.Image, AssetId = assetId ?? "0123456789abcdef0123456789abcdef.png", Width = 240 },
        new() { Kind = NoteBlockKind.Sketch, Ink = ink, Height = 300, BookmarkId = "sk", BookmarkName = "Рисунок схемы" },
        new() { Kind = NoteBlockKind.Paragraph, Spans = [new NoteSpan { Text = "Конец." }] }
    ];

    // ── JSON and the store ───────────────────────────────────────────────────

    [TestMethod]
    public void A_rich_body_survives_a_restart_block_for_block()
    {
        var service = new NotesService(_path);
        var note = service.Create(NoteKind.Note, "Поездка");
        note.Content = EveryKindOfBlock();
        note.Text = NoteContent.ToPlainText(note.Content);
        service.Update(note);
        var expected = Json(note.Content);

        var reopened = Restart(service);

        Assert.AreEqual(expected, Json(reopened.Find(note.Id)!.Content!));
        reopened.Dispose();
    }

    [TestMethod]
    public void Plain_text_keeps_list_markers_and_skips_pictures()
    {
        var text = NoteContent.ToPlainText(EveryKindOfBlock());

        StringAssert.Contains(text, "☑ Купить билеты");
        StringAssert.Contains(text, "☐ Забронировать отель");
        StringAssert.Contains(text, "• Первый пункт");
        StringAssert.Contains(text, "  • Вложенный");
        StringAssert.Contains(text, "1. Раз\n2. Два");
        Assert.IsFalse(text.Contains(".png", StringComparison.Ordinal), "A picture contributes no words.");
    }

    [TestMethod]
    public void Search_finds_text_inside_a_rich_body_and_bookmark_names()
    {
        var service = new NotesService(_path);
        var note = service.Create(NoteKind.Note, "Поездка");
        note.Content = EveryKindOfBlock();
        note.Text = NoteContent.ToPlainText(note.Content);
        service.Update(note);

        Assert.AreEqual(1, service.Query(NotesFilter.All, "отель", NotesSort.Modified).Count);
        Assert.AreEqual(1, service.Query(NotesFilter.All, "рисунок схемы", NotesSort.Modified).Count);
        service.Dispose();
    }

    [TestMethod]
    public void A_pasted_picture_is_stored_beside_the_note_and_survives_a_restart()
    {
        var service = new NotesService(_path);
        var note = service.Create(NoteKind.Note, "С картинкой");
        var asset = service.Assets.Import(note.Id, TinyPng, ".png");
        note.Content = [new NoteBlock { Kind = NoteBlockKind.Image, AssetId = asset }];
        service.Update(note);

        var reopened = Restart(service);

        var path = reopened.Assets.PathOf(note.Id, asset);
        Assert.IsNotNull(path, "A referenced picture must survive the load-time sweep.");
        CollectionAssert.AreEqual(TinyPng, File.ReadAllBytes(path));
        StringAssert.StartsWith(Path.GetFullPath(path), Path.GetFullPath(_directory));
        reopened.Dispose();
    }

    [TestMethod]
    public void Pictures_no_note_references_are_swept_at_load()
    {
        var service = new NotesService(_path);
        var note = service.Create(NoteKind.Note, "Без картинки в тексте");
        var orphan = service.Assets.Import(note.Id, TinyPng, ".png");
        var orphanPath = service.Assets.PathOf(note.Id, orphan)!;

        var reopened = Restart(service);

        Assert.IsFalse(File.Exists(orphanPath), "An image whose block was deleted is garbage once no undo can bring it back.");
        reopened.Dispose();
    }

    [TestMethod]
    public void An_unreadable_notes_file_never_triggers_the_sweep()
    {
        var service = new NotesService(_path);
        var note = service.Create(NoteKind.Note, "Важная");
        var asset = service.Assets.Import(note.Id, TinyPng, ".png");
        note.Content = [new NoteBlock { Kind = NoteBlockKind.Image, AssetId = asset }];
        service.Update(note);
        var assetPath = service.Assets.PathOf(note.Id, asset)!;
        service.Dispose();

        // Both the file and its backup are unreadable: the store loads empty…
        File.WriteAllText(_path, "{ not json");
        File.WriteAllText(_path + ".bak", "{ not json either");
        var broken = new NotesService(_path);

        // …and must not conclude that every picture is unreferenced.
        Assert.AreEqual(0, broken.All.Count);
        Assert.IsTrue(File.Exists(assetPath));
        broken.Dispose();
    }

    [TestMethod]
    public void Deleting_a_note_deletes_its_pictures()
    {
        var service = new NotesService(_path);
        var note = service.Create(NoteKind.Note, "Удаляемая");
        var asset = service.Assets.Import(note.Id, TinyPng, ".png");
        var assetPath = service.Assets.PathOf(note.Id, asset)!;

        service.Delete(note.Id);

        Assert.IsFalse(File.Exists(assetPath));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(assetPath)));
        service.Dispose();
    }

    [TestMethod]
    public void A_duplicate_gets_its_own_copy_of_every_picture()
    {
        var service = new NotesService(_path);
        var note = service.Create(NoteKind.Note, "Оригинал");
        var asset = service.Assets.Import(note.Id, TinyPng, ".png");
        note.Content = [new NoteBlock { Kind = NoteBlockKind.Image, AssetId = asset }];
        service.Update(note);

        var copy = service.Duplicate(note.Id)!;
        service.Delete(note.Id);

        Assert.IsNotNull(service.Assets.PathOf(copy.Id, asset), "Deleting the original must not blank the copy.");
        Assert.AreEqual(asset, copy.Content!.Single().AssetId);
        service.Dispose();
    }

    [TestMethod]
    public void The_asset_store_refuses_unknown_formats_and_unsafe_ids()
    {
        var service = new NotesService(_path);
        var note = service.Create(NoteKind.Note, "Проверка");

        Assert.ThrowsExactly<ArgumentException>(() => service.Assets.Import(note.Id, TinyPng, ".exe"));
        Assert.ThrowsExactly<ArgumentException>(() => service.Assets.Import("..\\..\\Windows", TinyPng, ".png"));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.Assets.Import(note.Id, [], ".png"));
        Assert.IsNull(service.Assets.PathOf(note.Id, "..\\notes.json"));
        Assert.IsNull(service.Assets.PathOf("..", "0123456789abcdef0123456789abcdef.png"));
        service.Dispose();
    }

    // ── The editor's document ────────────────────────────────────────────────

    private sealed class TestEmbeds : INoteEmbedFactory
    {
        public UIElement CreateImage(NoteBlock block) => new Border { Width = 10, Height = 10 };

        public NoteSketchSurface CreateSketch(NoteBlock block) => new(block.Ink, block.Height);

        public CheckBox CreateCheck(bool isChecked) => new() { IsChecked = isChecked };
    }

    private static string InkWithStrokes(int count)
    {
        var strokes = new StrokeCollection();
        for (var index = 0; index < count; index++)
        {
            strokes.Add(new Stroke(new StylusPointCollection
            {
                new StylusPoint(10 + index, 10),
                new StylusPoint(40 + index, 60),
                new StylusPoint(90 + index, 20)
            }));
        }

        using var stream = new MemoryStream();
        strokes.Save(stream, compress: true);
        return Convert.ToBase64String(stream.ToArray());
    }

    [TestMethod]
    public void Every_block_kind_round_trips_through_the_editor_document() => WpfTestHost.Run(() =>
    {
        var ink = InkWithStrokes(3);
        var blocks = EveryKindOfBlock(ink: ink);

        var document = new FlowDocument();
        NoteDocumentMapper.Load(document, blocks, new TestEmbeds());
        var read = NoteDocumentMapper.Read(document);

        // Ink is compared by strokes, not bytes: ISF re-encoding is allowed to differ.
        var expectedSketch = blocks.Single(block => block.Kind == NoteBlockKind.Sketch);
        var actualSketch = read.Single(block => block.Kind == NoteBlockKind.Sketch);
        Assert.AreEqual(3, Strokes(actualSketch.Ink).Count);
        expectedSketch.Ink = actualSketch.Ink = null;

        Assert.AreEqual(Json(blocks), Json(read));
    });

    [TestMethod]
    public void A_drawing_keeps_its_strokes_and_points() => WpfTestHost.Run(() =>
    {
        var surface = new NoteSketchSurface(InkWithStrokes(2), 260);
        surface.Canvas.Strokes.Add(new Stroke(new StylusPointCollection { new StylusPoint(5, 5), new StylusPoint(6, 7) }));

        var saved = surface.SaveInk();
        var reloaded = new NoteSketchSurface(saved, 260);

        Assert.AreEqual(3, reloaded.Canvas.Strokes.Count);
        Assert.AreEqual(260, reloaded.SurfaceHeight);
        var last = reloaded.Canvas.Strokes[2].StylusPoints;
        Assert.AreEqual(5, last[0].X, 0.05, "ISF stores coordinates in HIMETRIC, so they come back rounded.");
        Assert.AreEqual(7, last[1].Y, 0.05);
    });

    [TestMethod]
    public void An_empty_drawing_saves_nothing_and_a_corrupt_one_loads_empty() => WpfTestHost.Run(() =>
    {
        Assert.IsNull(new NoteSketchSurface(null, null).SaveInk());
        Assert.IsTrue(new NoteSketchSurface("not base64 ink!", null).IsEmpty);
    });

    [TestMethod]
    public void A_drawing_survives_the_store_and_the_editor_together() => WpfTestHost.Run(() =>
    {
        var service = new NotesService(_path);
        var note = service.Create(NoteKind.Note, "Набросок");
        note.Content = [new NoteBlock { Kind = NoteBlockKind.Sketch, Ink = InkWithStrokes(4), Height = 320 }];
        service.Update(note);

        var reopened = Restart(service);
        var document = new FlowDocument();
        NoteDocumentMapper.Load(document, reopened.Find(note.Id)!.Content!, new TestEmbeds());
        var sketch = (NoteSketchSurface)((BlockUIContainer)document.Blocks.FirstBlock).Child;

        Assert.AreEqual(4, sketch.Canvas.Strokes.Count);
        Assert.AreEqual(320, sketch.SurfaceHeight);
        reopened.Dispose();
    });

    [TestMethod]
    public void Pasted_foreign_content_is_flattened_not_dropped() => WpfTestHost.Run(() =>
    {
        var document = new FlowDocument();
        var table = new Table();
        var group = new TableRowGroup();
        var row = new TableRow();
        row.Cells.Add(new TableCell(new Paragraph(new Run("Ячейка"))));
        group.Rows.Add(row);
        table.RowGroups.Add(group);
        document.Blocks.Add(new Section(new Paragraph(new Bold(new Run("Из раздела")))));
        document.Blocks.Add(table);

        var read = NoteDocumentMapper.Read(document);

        Assert.AreEqual("Из раздела", read[0].Text);
        Assert.IsTrue(read[0].Spans.Single().Bold);
        Assert.AreEqual("Ячейка", read[1].Text);
    });

    private static StrokeCollection Strokes(string? ink)
    {
        using var stream = new MemoryStream(Convert.FromBase64String(ink!));
        return new StrokeCollection(stream);
    }
}
