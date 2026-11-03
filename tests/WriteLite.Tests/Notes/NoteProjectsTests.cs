using System.IO;
using System.Text.Json;
using WriteLite.Services.Notes;

namespace WriteLite.Tests.Notes;

/// <summary>
/// Projects: grouping notes, tasks and links, and every one of those surviving a restart.
/// </summary>
[TestClass]
public sealed class NoteProjectsTests
{
    private string _directory = string.Empty;
    private string _path = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "wl-projects-" + Guid.NewGuid().ToString("N"));
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

    [TestMethod]
    public void A_project_with_notes_tasks_and_links_survives_a_restart()
    {
        var service = new NotesService(_path);
        var project = service.CreateProject("  Диплом  ");
        service.SetProjectDescription(project.Id, "Защита в июне");
        var note = service.Create(NoteKind.Note, "Литература", project.Id);
        var task = service.Create(NoteKind.Task, "Написать введение", project.Id);
        service.SetCompleted(task.Id, true);
        var link = service.AddProjectBookmark(project.Id, "Методичка", "https://example.org/guide");

        var reopened = Restart(service);

        var loaded = reopened.FindProject(project.Id);
        Assert.IsNotNull(loaded);
        Assert.AreEqual("Диплом", loaded.Name, "Names are stored trimmed.");
        Assert.AreEqual("Защита в июне", loaded.Description);
        Assert.AreEqual(1, loaded.Bookmarks.Count);
        Assert.AreEqual(link!.Id, loaded.Bookmarks[0].Id);
        Assert.AreEqual("https://example.org/guide", loaded.Bookmarks[0].Target);

        Assert.AreEqual(project.Id, reopened.Find(note.Id)!.ProjectId);
        Assert.AreEqual(project.Id, reopened.Find(task.Id)!.ProjectId);
        Assert.IsTrue(reopened.Find(task.Id)!.IsCompleted);
        reopened.Dispose();
    }

    [TestMethod]
    public void The_file_is_written_at_schema_two_with_a_projects_list()
    {
        var service = new NotesService(_path);
        service.CreateProject("Переезд");
        service.Dispose();

        using var json = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.AreEqual(2, json.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.AreEqual(1, json.RootElement.GetProperty("Projects").GetArrayLength());
    }

    [TestMethod]
    public void A_version_one_file_loads_with_its_notes_and_no_projects()
    {
        File.WriteAllText(_path, """
            {
              "SchemaVersion": 1,
              "Notes": [ { "Id": "abc123", "Kind": "Note", "Title": "Старая", "Text": "Строка один\nСтрока два" } ]
            }
            """);

        var service = new NotesService(_path);

        var note = service.Find("abc123");
        Assert.IsNotNull(note);
        Assert.IsNull(note.Content, "A legacy note has no rich body until it is edited.");
        Assert.IsNull(note.ProjectId);
        Assert.AreEqual(0, service.Projects.Count);

        var blocks = NoteContent.BlocksOf(note);
        Assert.AreEqual(2, blocks.Count, "Each legacy line becomes a paragraph.");
        Assert.AreEqual("Строка два", blocks[1].Text);
        service.Dispose();
    }

    [TestMethod]
    public void A_note_pointing_at_a_missing_project_loads_as_unfiled()
    {
        File.WriteAllText(_path, """
            {
              "SchemaVersion": 2,
              "Notes": [ { "Id": "n1", "Title": "Сирота", "ProjectId": "gone" } ],
              "Projects": []
            }
            """);

        var service = new NotesService(_path);
        Assert.IsNull(service.Find("n1")!.ProjectId);
        service.Dispose();
    }

    [TestMethod]
    public void Creating_into_a_project_that_does_not_exist_files_the_note_nowhere()
    {
        var service = new NotesService(_path);
        var note = service.Create(NoteKind.Note, "Куда-то", projectId: "missing");
        Assert.IsNull(note.ProjectId);
        service.Dispose();
    }

    [TestMethod]
    public void Rename_changes_the_name_and_keeps_the_identity()
    {
        var service = new NotesService(_path);
        var project = service.CreateProject("Черновик");
        service.RenameProject(project.Id, "Запуск сайта");

        var reopened = Restart(service);
        Assert.AreEqual("Запуск сайта", reopened.FindProject(project.Id)!.Name);
        reopened.Dispose();
    }

    [TestMethod]
    public void Moving_a_note_between_projects_and_out_of_them()
    {
        var service = new NotesService(_path);
        var first = service.CreateProject("Первый");
        var second = service.CreateProject("Второй");
        var note = service.Create(NoteKind.Note, "Путешественница", first.Id);

        service.MoveNote(note.Id, second.Id);
        Assert.AreEqual(second.Id, service.Find(note.Id)!.ProjectId);

        service.MoveNote(note.Id, null);
        Assert.IsNull(service.Find(note.Id)!.ProjectId);

        service.MoveNote(note.Id, "no-such-project");
        Assert.IsNull(service.Find(note.Id)!.ProjectId, "A move into nothing is ignored.");
        service.Dispose();
    }

    [TestMethod]
    public void Deleting_a_project_keeps_its_notes_by_default()
    {
        var service = new NotesService(_path);
        var project = service.CreateProject("Временный");
        var note = service.Create(NoteKind.Note, "Важная мысль", project.Id);

        service.DeleteProject(project.Id);

        Assert.IsNull(service.FindProject(project.Id));
        Assert.IsNotNull(service.Find(note.Id), "Deleting a folder must not cost what was written in it.");
        Assert.IsNull(service.Find(note.Id)!.ProjectId);
        service.Dispose();
    }

    [TestMethod]
    public void Deleting_a_project_with_its_notes_removes_them_and_their_pictures()
    {
        var service = new NotesService(_path);
        var project = service.CreateProject("Черновики");
        var note = service.Create(NoteKind.Note, "С картинкой", project.Id);
        var asset = service.Assets.Import(note.Id, NoteContentPersistenceTests.TinyPng, ".png");
        var assetPath = service.Assets.PathOf(note.Id, asset)!;

        service.DeleteProject(project.Id, deleteNotes: true);

        Assert.IsNull(service.Find(note.Id));
        Assert.IsFalse(File.Exists(assetPath), "A deleted note's pictures go with it.");
        service.Dispose();
    }

    [TestMethod]
    public void Scopes_narrow_the_board()
    {
        var service = new NotesService(_path);
        var project = service.CreateProject("Проект");
        service.Create(NoteKind.Note, "В проекте", project.Id);
        service.Create(NoteKind.Task, "Задача проекта", project.Id);
        service.Create(NoteKind.Note, "Сама по себе");

        string[] Titles(NotesScope scope, NotesFilter filter = NotesFilter.All) =>
            service.Query(filter, null, NotesSort.Title, scope).Select(note => note.DisplayTitle).ToArray();

        CollectionAssert.AreEquivalent(new[] { "В проекте", "Задача проекта", "Сама по себе" }, Titles(NotesScope.Everything));
        CollectionAssert.AreEquivalent(new[] { "Сама по себе" }, Titles(NotesScope.Unfiled));

        // Inside a project the plain tasks live in the overview's list, not on the board…
        CollectionAssert.AreEquivalent(new[] { "В проекте" }, Titles(NotesScope.Project(project.Id)));

        // …unless the tasks tab asks for them.
        CollectionAssert.AreEquivalent(new[] { "Задача проекта" }, Titles(NotesScope.Project(project.Id), NotesFilter.Tasks));

        Assert.AreEqual(2, service.CountIn(NotesScope.Project(project.Id)));
        Assert.AreEqual(1, service.CountIn(NotesScope.Unfiled));
        service.Dispose();
    }

    [TestMethod]
    public void The_overview_lists_open_tasks_first_and_counts_progress()
    {
        var service = new NotesService(_path);
        var project = service.CreateProject("Ремонт");
        var first = service.Create(NoteKind.Task, "Купить краску", project.Id);
        var second = service.Create(NoteKind.Task, "Покрасить стены", project.Id);
        var third = service.Create(NoteKind.Task, "Повесить полки", project.Id);
        service.Create(NoteKind.Note, "Размеры комнаты", project.Id);
        service.SetCompleted(first.Id, true);

        var overview = service.Overview(project.Id)!;

        CollectionAssert.AreEqual(
            new[] { second.Id, third.Id, first.Id },
            overview.Tasks.Select(task => task.Id).ToArray(),
            "Open tasks in the order written, finished ones after them.");
        Assert.AreEqual(1, overview.TasksDone);
        Assert.AreEqual(1.0 / 3, overview.Progress, 1e-9);
        Assert.AreEqual(1, overview.Notes.Count);
        service.Dispose();
    }

    [TestMethod]
    public void The_overview_collects_the_bookmarks_set_inside_its_notes()
    {
        var service = new NotesService(_path);
        var project = service.CreateProject("Книга");
        var note = service.Create(NoteKind.Note, "Глава 1", project.Id);
        note.Content =
        [
            NoteBlock.Paragraph("Вступление"),
            new NoteBlock
            {
                Kind = NoteBlockKind.Heading2,
                Spans = [new NoteSpan { Text = "Главный конфликт" }],
                BookmarkId = "b1"
            },
            new NoteBlock
            {
                Kind = NoteBlockKind.Paragraph,
                Spans = [new NoteSpan { Text = "Финал" }],
                BookmarkId = "b2",
                BookmarkName = "Развязка"
            }
        ];
        service.Update(note);

        var marks = service.Overview(project.Id)!.NoteBookmarks;

        Assert.AreEqual(2, marks.Count);
        Assert.AreEqual("Главный конфликт", marks[0].Name, "Unnamed bookmarks are named after their line.");
        Assert.AreEqual("Развязка", marks[1].Name);
        Assert.IsTrue(marks.All(mark => mark.NoteId == note.Id && mark.NoteTitle == "Глава 1"));
        service.Dispose();
    }

    [TestMethod]
    public void Removing_a_link_leaves_the_others()
    {
        var service = new NotesService(_path);
        var project = service.CreateProject("Ссылки");
        var keep = service.AddProjectBookmark(project.Id, "Оставить", "https://a.example");
        var drop = service.AddProjectBookmark(project.Id, "Убрать", "https://b.example");

        service.RemoveProjectBookmark(project.Id, drop!.Id);

        var reopened = Restart(service);
        var links = reopened.FindProject(project.Id)!.Bookmarks;
        Assert.AreEqual(1, links.Count);
        Assert.AreEqual(keep!.Id, links[0].Id);
        reopened.Dispose();
    }

    [TestMethod]
    public void An_empty_link_is_not_added()
    {
        var service = new NotesService(_path);
        var project = service.CreateProject("Пусто");
        Assert.IsNull(service.AddProjectBookmark(project.Id, "Ничего", "   "));
        Assert.AreEqual(0, service.FindProject(project.Id)!.Bookmarks.Count);
        service.Dispose();
    }

    [TestMethod]
    public void Projects_returned_are_copies_and_do_not_leak_edits()
    {
        var service = new NotesService(_path);
        var project = service.CreateProject("Оригинал");

        var copy = service.Projects.Single();
        copy.Name = "Изменено снаружи";

        Assert.AreEqual("Оригинал", service.FindProject(project.Id)!.Name);
        service.Dispose();
    }

    [TestMethod]
    public void Project_links_are_parsed_from_title_and_address()
    {
        var (title, target) = WriteLite.Views.Pages.NotesPage.ParseLink("Методичка — https://example.org/m");
        Assert.AreEqual("Методичка", title);
        Assert.AreEqual("https://example.org/m", target);

        (title, target) = WriteLite.Views.Pages.NotesPage.ParseLink("www.example.org");
        Assert.AreEqual("https://www.example.org", target);
        Assert.AreEqual(target, title);

        Assert.IsTrue(WriteLite.Views.Pages.NotesPage.IsOpenableTarget("https://example.org"));
        Assert.IsTrue(WriteLite.Views.Pages.NotesPage.IsOpenableTarget(_directory), "An existing folder can be linked.");
        Assert.IsFalse(WriteLite.Views.Pages.NotesPage.IsOpenableTarget("javascript:alert(1)"));
        Assert.IsFalse(WriteLite.Views.Pages.NotesPage.IsOpenableTarget("просто текст"));
    }
}
