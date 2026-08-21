using System.IO;
using WriteLite.Services.Notes;

namespace WriteLite.Tests.Notes;

/// <summary>
/// The notes board's rules and its file.
/// </summary>
/// <remarks>
/// Every test drives a service pointed at a temporary file and, where the point is
/// durability, throws that service away and builds a second one over the same path.
/// That second read is the whole assertion: it is what "survives a restart" means,
/// and asserting against the in-memory list would pass even if nothing were ever
/// written.
/// </remarks>
[TestClass]
public sealed class NotesServiceTests
{
    private string _directory = string.Empty;
    private string _path = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "wl-notes-" + Guid.NewGuid().ToString("N"));
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

    private NotesService NewService() => new(_path);

    /// <summary>Closes a service so its debounced write lands, then opens a fresh one.</summary>
    private NotesService Restart(NotesService service)
    {
        service.Dispose();
        return NewService();
    }

    // ── Create, edit, delete ─────────────────────────────────────────────────

    [TestMethod]
    public void Created_note_survives_restart()
    {
        var service = NewService();
        var note = service.Create(NoteKind.Task, "Купить хлеб");
        note.Text = "И молоко";
        service.Update(note);

        var reopened = Restart(service);

        var loaded = reopened.Find(note.Id);
        Assert.IsNotNull(loaded, "The note should still be there after a restart.");
        Assert.AreEqual("Купить хлеб", loaded.Title);
        Assert.AreEqual("И молоко", loaded.Text);
        Assert.AreEqual(NoteKind.Task, loaded.Kind);
        reopened.Dispose();
    }

    [TestMethod]
    public void Edited_note_keeps_its_identity()
    {
        var service = NewService();
        var note = service.Create();
        note.Title = "Черновик";
        service.Update(note);
        note.Title = "Готово";
        service.Update(note);

        Assert.AreEqual(1, service.All.Count, "Editing must not add a second card.");
        Assert.AreEqual("Готово", service.Find(note.Id)!.Title);
        service.Dispose();
    }

    [TestMethod]
    public void Deleted_note_does_not_come_back()
    {
        var service = NewService();
        var note = service.Create();
        service.Update(note);
        service.Delete(note.Id);

        var reopened = Restart(service);

        Assert.IsNull(reopened.Find(note.Id));
        Assert.AreEqual(0, reopened.All.Count);
        reopened.Dispose();
    }

    [TestMethod]
    public void Duplicate_copies_content_but_not_state()
    {
        var service = NewService();
        var note = service.Create(NoteKind.Checklist, "Сборы");
        note.Items.Add(new NoteChecklistItem { Text = "Паспорт", IsDone = true });
        note.IsPinned = true;
        note.IsCompleted = true;
        service.Update(note);

        var copy = service.Duplicate(note.Id);

        Assert.IsNotNull(copy);
        Assert.AreNotEqual(note.Id, copy.Id, "A duplicate is a new card.");
        Assert.AreEqual(1, copy.Items.Count);
        Assert.AreEqual("Паспорт", copy.Items[0].Text);
        Assert.AreNotEqual(note.Items[0].Id, copy.Items[0].Id, "Checklist items are copied, not shared.");
        Assert.IsFalse(copy.IsPinned, "A copy starts unpinned.");
        Assert.IsFalse(copy.IsCompleted, "A copy starts unfinished — that is why it was copied.");
        service.Dispose();
    }

    // ── Completion ───────────────────────────────────────────────────────────

    [TestMethod]
    public void Completing_a_task_records_when()
    {
        var service = NewService();
        var note = service.Create(NoteKind.Task);

        service.SetCompleted(note.Id, true);

        var stored = service.Find(note.Id)!;
        Assert.IsTrue(stored.IsCompleted);
        Assert.IsNotNull(stored.CompletedAt);
        service.Dispose();
    }

    [TestMethod]
    public void Completion_survives_restart()
    {
        var service = NewService();
        var note = service.Create(NoteKind.Task, "Отправить отчёт");
        service.SetCompleted(note.Id, true);

        var reopened = Restart(service);

        Assert.IsTrue(reopened.Find(note.Id)!.IsCompleted);
        reopened.Dispose();
    }

    [TestMethod]
    public void Restoring_a_task_clears_its_checklist()
    {
        var service = NewService();
        var note = service.Create(NoteKind.Checklist);
        note.Items.Add(new NoteChecklistItem { Text = "Первый" });
        note.Items.Add(new NoteChecklistItem { Text = "Второй" });
        service.Update(note);

        service.SetCompleted(note.Id, true);
        Assert.IsTrue(service.Find(note.Id)!.Items.All(item => item.IsDone),
            "Finishing the card ticks everything on it.");

        service.SetCompleted(note.Id, false);

        Assert.IsTrue(service.Find(note.Id)!.Items.All(item => !item.IsDone),
            "A card brought back to work must not look finished.");
        service.Dispose();
    }

    // ── Checklists ───────────────────────────────────────────────────────────

    [TestMethod]
    public void Checklist_item_state_survives_restart()
    {
        var service = NewService();
        var note = service.Create(NoteKind.Checklist, "Покупки");
        var milk = new NoteChecklistItem { Text = "Молоко" };
        var bread = new NoteChecklistItem { Text = "Хлеб" };
        note.Items.Add(milk);
        note.Items.Add(bread);
        service.Update(note);

        service.SetItemDone(note.Id, milk.Id, true);

        var reopened = Restart(service);

        var loaded = reopened.Find(note.Id)!;
        Assert.AreEqual(2, loaded.Items.Count);
        Assert.IsTrue(loaded.Items.Single(item => item.Id == milk.Id).IsDone);
        Assert.IsFalse(loaded.Items.Single(item => item.Id == bread.Id).IsDone);
        Assert.AreEqual(1, loaded.DoneCount);
        reopened.Dispose();
    }

    [TestMethod]
    public void All_items_done_does_not_complete_the_card_by_itself()
    {
        var service = NewService();
        var note = service.Create(NoteKind.Checklist);
        var only = new NoteChecklistItem { Text = "Единственный" };
        note.Items.Add(only);
        service.Update(note);

        service.SetItemDone(note.Id, only.Id, true);

        var stored = service.Find(note.Id)!;
        Assert.IsTrue(stored.AllItemsDone);
        Assert.IsFalse(stored.IsCompleted,
            "Ticking the last box offers to finish the card; it must not finish it silently.");
        service.Dispose();
    }

    // ── Board rules ──────────────────────────────────────────────────────────

    [TestMethod]
    public void Completed_cards_leave_the_active_board()
    {
        var service = NewService();
        var open = service.Create(NoteKind.Task, "Открытая");
        var done = service.Create(NoteKind.Task, "Закрытая");
        service.SetCompleted(done.Id, true);

        var active = service.Query(NotesFilter.All, null, NotesSort.Modified);
        var finished = service.Query(NotesFilter.Completed, null, NotesSort.Modified);

        CollectionAssert.AreEquivalent(new[] { open.Id }, active.Select(note => note.Id).ToArray());
        CollectionAssert.AreEquivalent(new[] { done.Id }, finished.Select(note => note.Id).ToArray());
        service.Dispose();
    }

    [TestMethod]
    public void Pinned_cards_come_first_whatever_the_sort()
    {
        var service = NewService();
        var first = service.Create(NoteKind.Note, "Аaa");
        var second = service.Create(NoteKind.Note, "Bbb");
        service.SetPinned(second.Id, true);

        var byTitle = service.Query(NotesFilter.All, null, NotesSort.Title);

        Assert.AreEqual(second.Id, byTitle[0].Id, "Pinning outranks the sort.");
        Assert.AreEqual(first.Id, byTitle[1].Id);
        service.Dispose();
    }

    [TestMethod]
    public void Goals_and_tasks_have_their_own_tabs()
    {
        var service = NewService();
        var task = service.Create(NoteKind.Task);
        var list = service.Create(NoteKind.Checklist);
        var goal = service.Create(NoteKind.Goal);
        service.Create(NoteKind.Note);

        var tasks = service.Query(NotesFilter.Tasks, null, NotesSort.Modified).Select(note => note.Id).ToArray();
        var goals = service.Query(NotesFilter.Goals, null, NotesSort.Modified).Select(note => note.Id).ToArray();

        CollectionAssert.AreEquivalent(new[] { task.Id, list.Id }, tasks);
        CollectionAssert.AreEquivalent(new[] { goal.Id }, goals);
        service.Dispose();
    }

    [TestMethod]
    public void Search_looks_inside_the_card_as_well_as_at_its_title()
    {
        var service = NewService();
        var byTitle = service.Create(NoteKind.Note, "Заголовок про кофе");
        var byBody = service.Create(NoteKind.Note, "Другое");
        byBody.Text = "Здесь тоже упоминается кофе";
        service.Update(byBody);

        var byItem = service.Create(NoteKind.Checklist, "Список");
        byItem.Items.Add(new NoteChecklistItem { Text = "Молотый кофе" });
        service.Update(byItem);

        service.Create(NoteKind.Note, "Совсем не про напитки");

        var found = service.Query(NotesFilter.All, "кофе", NotesSort.Modified).Select(note => note.Id).ToArray();

        CollectionAssert.AreEquivalent(new[] { byTitle.Id, byBody.Id, byItem.Id }, found);
        service.Dispose();
    }

    [TestMethod]
    public void Search_ignores_case()
    {
        var service = NewService();
        service.Create(NoteKind.Note, "Отчёт по КВАРТАЛУ");

        Assert.AreEqual(1, service.Query(NotesFilter.All, "кварталу", NotesSort.Modified).Count);
        service.Dispose();
    }

    [TestMethod]
    public void Reordering_moves_a_card_and_renumbers_the_rest()
    {
        var service = NewService();
        var a = service.Create(NoteKind.Note, "A");
        var b = service.Create(NoteKind.Note, "B");
        var c = service.Create(NoteKind.Note, "C");

        var before = service.Query(NotesFilter.All, null, NotesSort.Manual).Select(note => note.Id).ToArray();
        service.Reorder(before[0], 2);

        var after = service.Query(NotesFilter.All, null, NotesSort.Manual).Select(note => note.Id).ToArray();

        Assert.AreEqual(3, after.Length);
        Assert.AreEqual(before[0], after[2], "The dragged card lands where it was dropped.");
        CollectionAssert.AreEquivalent(new[] { a.Id, b.Id, c.Id }, after);
        service.Dispose();
    }

    // ── The file ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Store_writes_a_schema_version()
    {
        var service = NewService();
        service.Create(NoteKind.Note, "Что-нибудь");
        service.Dispose();

        var json = File.ReadAllText(_path);
        StringAssert.Contains(json, "\"SchemaVersion\"", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void Store_keeps_russian_text_readable_on_disk()
    {
        var service = NewService();
        service.Create(NoteKind.Note, "Проверка кириллицы");
        service.Dispose();

        StringAssert.Contains(File.ReadAllText(_path), "Проверка кириллицы");
    }

    [TestMethod]
    public void A_corrupt_file_costs_the_last_save_not_everything()
    {
        var service = NewService();
        service.Create(NoteKind.Note, "Первая");
        service.Dispose();

        // Two writes, so a backup exists; then the current file is destroyed the way
        // an interrupted write would leave it.
        var second = NewService();
        second.Create(NoteKind.Note, "Вторая");
        second.Dispose();

        File.WriteAllText(_path, "{ this is not json");

        var recovered = NewService();

        Assert.IsTrue(recovered.All.Count >= 1,
            "The backup written by the previous save must be read when the current file is unreadable.");
        recovered.Dispose();
    }

    [TestMethod]
    public void A_missing_file_is_an_empty_board_not_an_error()
    {
        var service = NewService();

        Assert.AreEqual(0, service.All.Count);
        Assert.AreEqual(0, service.Query(NotesFilter.All, null, NotesSort.Modified).Count);
        service.Dispose();
    }

    [TestMethod]
    public void Interrupted_write_never_destroys_the_previous_document()
    {
        var service = NewService();
        service.Create(NoteKind.Note, "Сохранено");
        service.Dispose();

        var original = File.ReadAllText(_path);

        // A temporary file left behind by a process that died mid-save.
        File.WriteAllText(_path + ".tmp", "{ half written");

        var reopened = NewService();

        Assert.AreEqual(1, reopened.All.Count);
        Assert.AreEqual(original, File.ReadAllText(_path), "A stale temporary file must not be adopted.");
        reopened.Dispose();
    }
}
