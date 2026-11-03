using System.Text.Json.Serialization;

namespace WriteLite.Services.Notes;

/// <summary>What a card is for. The type decides which affordances the card shows.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NoteKind>))]
public enum NoteKind
{
    /// <summary>Free-form text.</summary>
    Note,

    /// <summary>One thing to do, which can be finished.</summary>
    Task,

    /// <summary>Several independently completable items.</summary>
    Checklist,

    /// <summary>A larger objective, optionally broken into steps.</summary>
    Goal,

    /// <summary>A calendar event: <see cref="Note.DueDate"/> is when it starts.</summary>
    Event
}

/// <summary>One line of a checklist, or one step of a goal.</summary>
public sealed class NoteChecklistItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Text { get; set; } = string.Empty;

    public bool IsDone { get; set; }

    public NoteChecklistItem Copy() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Text = Text,
        IsDone = IsDone
    };
}

/// <summary>
/// One card on the notes board.
/// </summary>
/// <remarks>
/// A single mutable type rather than four, because the four kinds differ in what
/// they display, not in what they store: a goal is a task with steps, a checklist is
/// a note whose body happens to be a list. Keeping them one shape means a card can
/// change kind without losing anything the user typed, and means the store has one
/// schema to migrate rather than four.
/// </remarks>
public sealed class Note
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public NoteKind Kind { get; set; } = NoteKind.Note;

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The body as plain text, kept in step with <see cref="Content"/> on every save.
    /// </summary>
    /// <remarks>
    /// Still stored, not derived: search, the card and the model read it without parsing
    /// blocks, and a version of WriteLite that predates rich notes still finds the words.
    /// </remarks>
    public string Text { get; set; } = string.Empty;

    /// <summary>The rich body. Null for a note written before notes had formatting.</summary>
    public List<NoteBlock>? Content { get; set; }

    /// <summary>The project the note belongs to; null for one that belongs to none.</summary>
    public string? ProjectId { get; set; }

    public List<NoteChecklistItem> Items { get; set; } = [];

    public string? Category { get; set; }

    public bool IsPinned { get; set; }

    public bool IsCompleted { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? DueDate { get; set; }

    /// <summary>For events and tasks on the calendar: the date matters, the time does not.</summary>
    public bool IsAllDay { get; set; }

    /// <summary>When the note was moved to «Недавно удалённые»; null for a live note.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    [JsonIgnore]
    public bool IsDeleted => DeletedAt is not null;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Manual position on the board. Lower is earlier; ties fall back to modification time.</summary>
    public int Order { get; set; }

    /// <summary>True for the kinds where "done" is a meaningful state.</summary>
    [JsonIgnore]
    public bool IsCompletable => Kind is NoteKind.Task or NoteKind.Goal or NoteKind.Checklist;

    [JsonIgnore]
    public int DoneCount => Items.Count(item => item.IsDone);

    /// <summary>
    /// True when a checklist has items and every one of them is ticked.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="IsCompleted"/>: ticking the last box offers to
    /// finish the card, it does not silently finish it. A checklist someone keeps
    /// re-running should not disappear into "Выполненные" behind their back.
    /// </remarks>
    [JsonIgnore]
    public bool AllItemsDone => Items.Count > 0 && Items.All(item => item.IsDone);

    /// <summary>The heading shown on the card when the note was never given a title.</summary>
    [JsonIgnore]
    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title))
            {
                return Title.Trim();
            }

            var firstLine = Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))
                ?.Trim();

            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                return firstLine.Length <= 60 ? firstLine : firstLine[..60].TrimEnd() + "…";
            }

            return Items.FirstOrDefault()?.Text.Trim() is { Length: > 0 } item
                ? item
                : "Без названия";
        }
    }

    public Note Duplicate() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Kind = Kind,
        Title = string.IsNullOrWhiteSpace(Title) ? Title : Title + " (копия)",
        Text = Text,
        Content = Content?.Select(block => block.Copy()).ToList(),
        ProjectId = ProjectId,
        Items = Items.Select(item => item.Copy()).ToList(),
        Category = Category,
        // A duplicate is a fresh start: it is neither pinned nor already finished,
        // because the reason to copy a card is almost always to do it again.
        IsPinned = false,
        IsCompleted = false,
        CompletedAt = null,
        DueDate = DueDate,
        IsAllDay = IsAllDay,
        CreatedAt = DateTimeOffset.Now,
        ModifiedAt = DateTimeOffset.Now,
        Order = Order
    };

    public void Touch() => ModifiedAt = DateTimeOffset.Now;

    /// <summary>A deep copy with the same identity, for editors that must be able to cancel.</summary>
    public Note Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Title = Title,
        Text = Text,
        Content = Content?.Select(block => block.Copy()).ToList(),
        ProjectId = ProjectId,
        Items = Items.Select(item => new NoteChecklistItem { Id = item.Id, Text = item.Text, IsDone = item.IsDone }).ToList(),
        Category = Category,
        IsPinned = IsPinned,
        IsCompleted = IsCompleted,
        CompletedAt = CompletedAt,
        DueDate = DueDate,
        IsAllDay = IsAllDay,
        DeletedAt = DeletedAt,
        CreatedAt = CreatedAt,
        ModifiedAt = ModifiedAt,
        Order = Order
    };
}

/// <summary>A link kept with a project: a web page, a file, a folder.</summary>
public sealed class ProjectBookmark
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = string.Empty;

    /// <summary>An absolute URL or a path on this machine.</summary>
    public string Target { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// A named group of notes, tasks and links.
/// </summary>
/// <remarks>
/// Membership lives on the note (<see cref="Note.ProjectId"/>), not here. A note is in at
/// most one project, moving it is one field, and a project deleted by a version that did
/// not know about some note cannot leave that note pointing two ways at once.
/// </remarks>
public sealed class NoteProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<ProjectBookmark> Bookmarks { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Position in the project list; lower is earlier.</summary>
    public int Order { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Без названия" : Name.Trim();

    public NoteProject Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        Bookmarks = Bookmarks.Select(link => new ProjectBookmark
        {
            Id = link.Id,
            Title = link.Title,
            Target = link.Target,
            CreatedAt = link.CreatedAt
        }).ToList(),
        CreatedAt = CreatedAt,
        ModifiedAt = ModifiedAt,
        Order = Order
    };
}

/// <summary>Everything a project page shows, read in one consistent snapshot.</summary>
public sealed record ProjectOverview(
    NoteProject Project,
    IReadOnlyList<Note> Notes,
    IReadOnlyList<Note> Tasks,
    IReadOnlyList<NoteBookmark> NoteBookmarks)
{
    public int TasksDone => Tasks.Count(task => task.IsCompleted);

    /// <summary>Share of finished tasks, 0 to 1; zero when the project has none.</summary>
    public double Progress => Tasks.Count == 0 ? 0 : (double)TasksDone / Tasks.Count;
}

/// <summary>Which notes the board is drawing: everything, the ones in no project, or one project.</summary>
public readonly record struct NotesScope(NotesScopeKind Kind, string? ProjectId = null)
{
    public static NotesScope Everything => new(NotesScopeKind.All);

    public static NotesScope Unfiled => new(NotesScopeKind.Unfiled);

    public static NotesScope Project(string id) => new(NotesScopeKind.Project, id);
}

public enum NotesScopeKind
{
    All,
    Unfiled,
    Project
}

/// <summary>The whole board as it sits on disk.</summary>
public sealed class NotesDocument
{
    /// <summary>Bumped whenever the shape changes; <see cref="NotesService"/> migrates forward.</summary>
    /// <remarks>
    /// 2 — rich bodies (<see cref="Note.Content"/>), projects, and pictures stored beside
    /// the file. Version 1 files load unchanged: every new field is optional.
    /// </remarks>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<Note> Notes { get; set; } = [];

    public List<NoteProject> Projects { get; set; } = [];
}

/// <summary>Which cards the board is showing.</summary>
public enum NotesFilter
{
    All,
    Tasks,
    Goals,
    Completed
}

/// <summary>How the board is ordered.</summary>
public enum NotesSort
{
    Manual,
    Modified,
    Created,
    DueDate,
    Title
}
