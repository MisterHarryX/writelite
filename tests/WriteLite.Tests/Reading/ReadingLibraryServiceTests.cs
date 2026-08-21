using System.IO;
using System.Text;
using WriteLite.Services.Reading;

namespace WriteLite.Tests.Reading;

/// <summary>
/// Reading projects: identity, persistence, and everything hung off a project.
/// </summary>
/// <remarks>
/// The behaviour worth defending hardest is that a book opened twice is one project.
/// A path-keyed library quietly produces a second, empty project whenever a file is
/// renamed or moved, and the reader's response — "my highlights are gone" — is
/// indistinguishable from data loss.
/// </remarks>
[TestClass]
public sealed class ReadingLibraryServiceTests
{
    private string _root = string.Empty;
    private string _books = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "wl-reading-" + Guid.NewGuid().ToString("N"));
        _books = Path.Combine(_root, "books");
        Directory.CreateDirectory(_books);
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

    private ReadingLibraryService NewLibrary() => new(
        Path.Combine(_root, "library.json"),
        Path.Combine(_root, "projects"));

    private string WriteBook(string name, string text)
    {
        var path = Path.Combine(_books, name);
        File.WriteAllText(path, text, Encoding.UTF8);
        return path;
    }

    // ── Identity ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Opening_the_same_book_twice_reuses_one_project()
    {
        var library = NewLibrary();
        var book = WriteBook("book.txt", "Содержимое книги, достаточно длинное для отпечатка.");

        var first = library.OpenOrCreate(book);
        var second = library.OpenOrCreate(book);

        Assert.AreEqual(first.Id, second.Id);
        Assert.AreEqual(1, library.Projects.Count, "A second open must not create a second project.");
    }

    [TestMethod]
    public void A_renamed_book_lands_in_its_existing_project()
    {
        var library = NewLibrary();
        var original = WriteBook("original.txt", "Одно и то же содержимое книги.");
        var project = library.OpenOrCreate(original);

        project.Highlights.Add(new Highlight
        {
            Anchor = new ReadingAnchor { Start = 4, Length = 5, Quote = "и то " }
        });

        library.Save(project);

        var renamed = Path.Combine(_books, "renamed-and-moved.txt");
        File.Move(original, renamed);

        var reopened = library.OpenOrCreate(renamed);

        Assert.AreEqual(project.Id, reopened.Id, "Identity is the content, not the path.");
        Assert.AreEqual(1, reopened.Highlights.Count, "The marks come with it.");
        Assert.AreEqual(renamed, reopened.SourcePath, "The project learns the new location.");
        Assert.AreEqual(1, library.Projects.Count);
    }

    [TestMethod]
    public void Two_different_books_are_two_projects()
    {
        var library = NewLibrary();

        var one = library.OpenOrCreate(WriteBook("one.txt", "Первая книга."));
        var two = library.OpenOrCreate(WriteBook("two.txt", "Вторая книга, другое содержимое."));

        Assert.AreNotEqual(one.Id, two.Id);
        Assert.AreEqual(2, library.Projects.Count);
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    [TestMethod]
    public void Everything_in_a_project_survives_restart()
    {
        var library = NewLibrary();
        var project = library.OpenOrCreate(WriteBook("full.txt", "Текст книги для проверки сохранения всех пометок."));

        project.Position = 21;
        project.Progress = 0.42;
        project.Typography.FontSize = 21;
        project.Typography.LineSpacing = 2.0;

        project.Highlights.Add(new Highlight
        {
            Anchor = new ReadingAnchor { Start = 5, Length = 5, Quote = "книги" },
            Tint = HighlightTint.Sage
        });

        project.Annotations.Add(new Annotation
        {
            Anchor = new ReadingAnchor { Start = 11, Length = 3, Quote = "для" },
            Note = "Моя мысль по этому поводу",
            Tag = "важное"
        });

        project.Bookmarks.Add(new Bookmark { Offset = 30, Fraction = 0.6, Preview = "проверки сохранения", Title = "Сюда" });

        project.Cards.Add(new StudyCard
        {
            Front = "Что такое SQL?",
            Back = "Декларативный язык для работы с реляционными базами данных.",
            Source = new ReadingAnchor { Start = 0, Length = 5, Quote = "Текст" },
            DraftedByAi = true
        });

        project.Words.Add(new InterestingWord { Word = "декларативный", Offset = 7 });

        library.Save(project);

        // A brand-new service over the same folder: nothing is carried in memory.
        var reopened = NewLibrary().Load(project.Id);

        Assert.IsNotNull(reopened);
        Assert.AreEqual(21, reopened.Position);
        Assert.AreEqual(0.42, reopened.Progress, 0.0001);
        Assert.AreEqual(21, reopened.Typography.FontSize, 0.0001);
        Assert.AreEqual(2.0, reopened.Typography.LineSpacing, 0.0001);

        Assert.AreEqual(1, reopened.Highlights.Count);
        Assert.AreEqual(HighlightTint.Sage, reopened.Highlights[0].Tint);
        Assert.AreEqual("книги", reopened.Highlights[0].Anchor.Quote);

        Assert.AreEqual(1, reopened.Annotations.Count);
        Assert.AreEqual("Моя мысль по этому поводу", reopened.Annotations[0].Note);
        Assert.AreEqual("важное", reopened.Annotations[0].Tag);
        Assert.AreEqual("для", reopened.Annotations[0].Anchor.Quote);

        Assert.AreEqual(1, reopened.Bookmarks.Count);
        Assert.AreEqual(30, reopened.Bookmarks[0].Offset);
        Assert.AreEqual("Сюда", reopened.Bookmarks[0].Title);

        Assert.AreEqual(1, reopened.Cards.Count);
        Assert.AreEqual("Что такое SQL?", reopened.Cards[0].Front);
        Assert.IsTrue(reopened.Cards[0].DraftedByAi);
        Assert.AreEqual("Текст", reopened.Cards[0].Source!.Quote);

        Assert.AreEqual(1, reopened.Words.Count);
        Assert.AreEqual("декларативный", reopened.Words[0].Word);
    }

    [TestMethod]
    public void Reading_position_is_what_continue_reading_restores()
    {
        var library = NewLibrary();
        var project = library.OpenOrCreate(WriteBook("position.txt", new string('я', 4000)));

        project.Position = 1234;
        project.Progress = 0.31;
        library.Save(project);

        var later = NewLibrary();
        var summary = later.ContinueReading;

        Assert.IsNotNull(summary);
        Assert.AreEqual(project.Id, summary.Id);
        Assert.AreEqual(1234, later.Load(project.Id)!.Position);
    }

    [TestMethod]
    public void Continue_reading_ignores_a_book_that_was_never_started()
    {
        var library = NewLibrary();
        library.OpenOrCreate(WriteBook("untouched.txt", "Совсем не начатая книга."));

        var summary = library.ContinueReading;

        // The shelf still names it; the reading page is what refuses to call a book
        // at 0 % "continue reading".
        Assert.IsNotNull(summary);
        Assert.AreEqual(0, summary.Progress, 0.0001);
    }

    [TestMethod]
    public void Library_index_carries_the_counts_the_shelf_shows()
    {
        var library = NewLibrary();
        var project = library.OpenOrCreate(WriteBook("counts.txt", "Книга с пометками."));

        project.Annotations.Add(new Annotation { Anchor = new ReadingAnchor { Quote = "Книга" }, Note = "раз" });
        project.Annotations.Add(new Annotation { Anchor = new ReadingAnchor { Quote = "с" }, Note = "два" });
        project.Bookmarks.Add(new Bookmark { Preview = "Книга" });
        project.Cards.Add(new StudyCard { Front = "Вопрос", Back = "Ответ" });
        project.Progress = 0.37;
        library.Save(project);

        var summary = NewLibrary().Projects.Single();

        Assert.AreEqual(2, summary.AnnotationCount);
        Assert.AreEqual(1, summary.BookmarkCount);
        Assert.AreEqual(1, summary.CardCount);
        StringAssert.Contains(summary.Details(), "37% прочитано");
        StringAssert.Contains(summary.Details(), "2 пометки");
    }

    [TestMethod]
    public void Deleting_a_project_removes_it_from_the_shelf_and_the_disk()
    {
        var library = NewLibrary();
        var project = library.OpenOrCreate(WriteBook("gone.txt", "Книга, которую удалят."));
        var file = Path.Combine(_root, "projects", project.Id + ".json");

        Assert.IsTrue(File.Exists(file));

        library.Delete(project.Id);

        Assert.IsFalse(File.Exists(file));
        Assert.AreEqual(0, NewLibrary().Projects.Count);
    }

    [TestMethod]
    public void Renaming_a_project_leaves_the_file_alone()
    {
        var library = NewLibrary();
        var book = WriteBook("original name.txt", "Содержимое.");
        var project = library.OpenOrCreate(book);

        library.Rename(project.Id, "Война и мир");

        Assert.AreEqual("Война и мир", NewLibrary().Load(project.Id)!.Title);
        Assert.IsTrue(File.Exists(book), "Renaming a project must not touch the book.");
    }

    // ── Missing sources ──────────────────────────────────────────────────────

    [TestMethod]
    public void A_missing_book_keeps_its_project_and_its_marks()
    {
        var library = NewLibrary();
        var book = WriteBook("temporary.txt", "Книга, которая исчезнет.");
        var project = library.OpenOrCreate(book);

        project.Annotations.Add(new Annotation
        {
            Anchor = new ReadingAnchor { Start = 0, Length = 5, Quote = "Книга" },
            Note = "Сохранить любой ценой"
        });

        library.Save(project);
        File.Delete(book);

        var summary = NewLibrary().Projects.Single();

        Assert.IsFalse(ReadingLibraryService.SourceExists(summary), "The file is gone …");
        Assert.AreEqual(1, NewLibrary().Load(project.Id)!.Annotations.Count, "… the reader's work is not.");
    }

    [TestMethod]
    public void Opening_a_file_that_is_not_there_is_reported_not_swallowed()
    {
        var library = NewLibrary();

        Assert.ThrowsExactly<FileNotFoundException>(
            () => library.OpenOrCreate(Path.Combine(_books, "never-existed.txt")));
    }

    [TestMethod]
    public void Typography_is_clamped_to_a_readable_band()
    {
        var library = NewLibrary();
        var project = library.OpenOrCreate(WriteBook("type.txt", "Текст."));

        project.Typography.FontSize = 900;
        project.Typography.LineSpacing = 0.1;
        project.Typography.ContentWidth = 40_000;
        library.Save(project);

        var reopened = NewLibrary().Load(project.Id)!;

        Assert.AreEqual(32, reopened.Typography.FontSize, 0.0001);
        Assert.AreEqual(1.2, reopened.Typography.LineSpacing, 0.0001);
        Assert.AreEqual(1400, reopened.Typography.ContentWidth, 0.0001);
    }
}
