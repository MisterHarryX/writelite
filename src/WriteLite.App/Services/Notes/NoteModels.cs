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
    Goal
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

    public string Text { get; set; } = string.Empty;

    public List<NoteChecklistItem> Items { get; set; } = [];

    public string? Category { get; set; }

    public bool IsPinned { get; set; }

    public bool IsCompleted { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? DueDate { get; set; }

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
        Items = Items.Select(item => item.Copy()).ToList(),
        Category = Category,
        // A duplicate is a fresh start: it is neither pinned nor already finished,
        // because the reason to copy a card is almost always to do it again.
        IsPinned = false,
        IsCompleted = false,
        CompletedAt = null,
        DueDate = DueDate,
        CreatedAt = DateTimeOffset.Now,
        ModifiedAt = DateTimeOffset.Now,
        Order = Order
    };

    public void Touch() => ModifiedAt = DateTimeOffset.Now;
}

/// <summary>The whole board as it sits on disk.</summary>
public sealed class NotesDocument
{
    /// <summary>Bumped whenever the shape changes; <see cref="NotesService"/> migrates forward.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<Note> Notes { get; set; } = [];
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
