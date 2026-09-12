using System.IO;
using WriteLite.Services.Storage;

namespace WriteLite.Services.Notes;

/// <summary>
/// The notes board: everything the page can do to a card, and the autosave behind it.
/// </summary>
/// <remarks>
/// The page never touches the file. Every mutation goes through a method here, every
/// method ends in <see cref="ScheduleSave"/>, and the debounce means typing in a card
/// costs one write when the typing stops rather than one per keystroke — while
/// <see cref="Flush"/> on shutdown guarantees the last edit is not the one that gets
/// lost.
///
/// Sorting and filtering live here too rather than in the view. They are the same
/// rules the tests exercise, and a pinned card outranking the sort is a property of
/// the board, not of how it happens to be drawn.
/// </remarks>
public sealed class NotesService : IDisposable
{
    /// <summary>Quiet period before an edit reaches the disk.</summary>
    private static readonly TimeSpan SaveDelay = WriteLiteDefaults.Debounce.NotesSaveDelay;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<Note> _notes;
    private System.Threading.Timer? _saveTimer;
    private bool _dirty;
    private bool _disposed;

    public NotesService(string? path = null)
    {
        _path = path ?? WriteLiteDataPaths.NotesFile;

        var document = AtomicJsonFile.Read<NotesDocument>(_path) ?? new NotesDocument();
        Migrate(document);
        _notes = document.Notes;
    }

    /// <summary>Raised after any change, so the board can redraw.</summary>
    public event Action? Changed;

    public string StorePath => _path;

    /// <summary>Every card, newest activity first, pinned cards ahead of the rest.</summary>
    public IReadOnlyList<Note> All
    {
        get
        {
            lock (_gate)
            {
                return _notes.ToList();
            }
        }
    }

    // ── Reading the board ────────────────────────────────────────────────────

    /// <summary>
    /// The cards a given filter, search and sort produce, in the order they are drawn.
    /// </summary>
    /// <remarks>
    /// Completed cards are hidden everywhere except in their own tab. A board whose
    /// finished work stays in place stops being a board of what is left to do, which
    /// is the whole reason to tick something off.
    /// </remarks>
    public IReadOnlyList<Note> Query(NotesFilter filter, string? search, NotesSort sort)
    {
        List<Note> snapshot;
        lock (_gate)
        {
            snapshot = _notes.ToList();
        }

        var matching = snapshot.Where(note => Matches(note, filter, search));

        var ordered = sort switch
        {
            NotesSort.Created => matching.OrderByDescending(note => note.CreatedAt),
            NotesSort.DueDate => matching
                .OrderBy(note => note.DueDate is null)
                .ThenBy(note => note.DueDate ?? DateTimeOffset.MaxValue),
            NotesSort.Title => matching.OrderBy(note => note.DisplayTitle, StringComparer.CurrentCultureIgnoreCase),
            NotesSort.Manual => matching.OrderBy(note => note.Order).ThenByDescending(note => note.ModifiedAt),
            _ => matching.OrderByDescending(note => note.ModifiedAt)
        };

        // Pinning outranks every sort. That is what pinning means.
        return ordered
            .OrderByDescending(note => note.IsPinned)
            .ToList();
    }

    private static bool Matches(Note note, NotesFilter filter, string? search)
    {
        var passesFilter = filter switch
        {
            NotesFilter.Tasks => !note.IsCompleted && note.Kind is NoteKind.Task or NoteKind.Checklist,
            NotesFilter.Goals => !note.IsCompleted && note.Kind == NoteKind.Goal,
            NotesFilter.Completed => note.IsCompleted,
            _ => !note.IsCompleted
        };

        if (!passesFilter)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var needle = search.Trim();
        return Contains(note.Title, needle)
               || Contains(note.Text, needle)
               || Contains(note.Category, needle)
               || note.Items.Any(item => Contains(item.Text, needle));
    }

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack)
        && haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);

    public Note? Find(string id)
    {
        lock (_gate)
        {
            return _notes.FirstOrDefault(note => note.Id == id);
        }
    }

    /// <summary>Counts for the filter row, so a tab can say how much is behind it.</summary>
    public (int Active, int Tasks, int Goals, int Completed) Counts()
    {
        lock (_gate)
        {
            return (
                _notes.Count(note => !note.IsCompleted),
                _notes.Count(note => !note.IsCompleted && note.Kind is NoteKind.Task or NoteKind.Checklist),
                _notes.Count(note => !note.IsCompleted && note.Kind == NoteKind.Goal),
                _notes.Count(note => note.IsCompleted));
        }
    }

    // ── Changing the board ───────────────────────────────────────────────────

    public Note Create(NoteKind kind = NoteKind.Note, string? title = null)
    {
        var note = new Note
        {
            Kind = kind,
            Title = title ?? string.Empty
        };

        lock (_gate)
        {
            // New cards land at the front of a manual ordering rather than the back:
            // the thing just written down is the thing being thought about.
            note.Order = _notes.Count == 0 ? 0 : _notes.Min(existing => existing.Order) - 1;
            _notes.Add(note);
        }

        Commit();
        return note;
    }

    public void Update(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        note.Touch();
        Replace(note);
        Commit();
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            _notes.RemoveAll(note => note.Id == id);
        }

        Commit();
    }

    public Note? Duplicate(string id)
    {
        Note copy;
        lock (_gate)
        {
            var source = _notes.FirstOrDefault(note => note.Id == id);
            if (source is null)
            {
                return null;
            }

            copy = source.Duplicate();
            copy.Order = source.Order - 1;
            _notes.Add(copy);
        }

        Commit();
        return copy;
    }

    public void SetPinned(string id, bool pinned)
    {
        lock (_gate)
        {
            if (_notes.FirstOrDefault(note => note.Id == id) is not { } note || note.IsPinned == pinned)
            {
                return;
            }

            note.IsPinned = pinned;
            note.Touch();
        }

        Commit();
    }

    /// <summary>
    /// Marks a card finished, or brings it back.
    /// </summary>
    /// <remarks>
    /// Restoring clears the checklist as well. A task brought back from "Выполненные"
    /// with every box still ticked is finished-looking work presented as unfinished,
    /// and the first thing anyone does is untick them all by hand.
    /// </remarks>
    public void SetCompleted(string id, bool completed)
    {
        lock (_gate)
        {
            if (_notes.FirstOrDefault(note => note.Id == id) is not { } note || note.IsCompleted == completed)
            {
                return;
            }

            note.IsCompleted = completed;
            note.CompletedAt = completed ? DateTimeOffset.Now : null;

            if (completed)
            {
                foreach (var item in note.Items)
                {
                    item.IsDone = true;
                }
            }
            else
            {
                foreach (var item in note.Items)
                {
                    item.IsDone = false;
                }
            }

            note.Touch();
        }

        Commit();
    }

    public void SetItemDone(string noteId, string itemId, bool done)
    {
        lock (_gate)
        {
            var note = _notes.FirstOrDefault(candidate => candidate.Id == noteId);
            if (note?.Items.FirstOrDefault(item => item.Id == itemId) is not { } target || target.IsDone == done)
            {
                return;
            }

            target.IsDone = done;
            note.Touch();
        }

        Commit();
    }

    /// <summary>Moves a card to a new position in the manual ordering.</summary>
    public void Reorder(string id, int newIndex)
    {
        lock (_gate)
        {
            var ordered = _notes.OrderBy(note => note.Order).ThenByDescending(note => note.ModifiedAt).ToList();
            var moving = ordered.FirstOrDefault(note => note.Id == id);
            if (moving is null)
            {
                return;
            }

            ordered.Remove(moving);
            ordered.Insert(Math.Clamp(newIndex, 0, ordered.Count), moving);

            // Renumbered densely from zero so the ordering cannot drift apart after
            // many moves, and so the numbers mean the same thing on the next launch.
            for (var index = 0; index < ordered.Count; index++)
            {
                ordered[index].Order = index;
            }
        }

        Commit();
    }

    private void Replace(Note note)
    {
        lock (_gate)
        {
            var index = _notes.FindIndex(existing => existing.Id == note.Id);
            if (index >= 0)
            {
                _notes[index] = note;
            }
            else
            {
                _notes.Add(note);
            }
        }
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    private void Commit()
    {
        ScheduleSave();
        Changed?.Invoke();
    }

    private void ScheduleSave()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _dirty = true;
            _saveTimer ??= new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes pending changes now. Called on shutdown and by the tests.</summary>
    public void Flush()
    {
        NotesDocument document;
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            document = new NotesDocument
            {
                SchemaVersion = NotesDocument.CurrentSchemaVersion,
                Notes = _notes.ToList()
            };
        }

        try
        {
            AtomicJsonFile.Write(_path, document);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CompatibilityLogger.Technical("notes-save-failed", exception);

            // Left dirty on purpose: the next edit retries, and the data is still in
            // memory. Swallowing the flag would turn a transient lock into a silent
            // loss of everything typed since the last successful write.
            lock (_gate)
            {
                _dirty = true;
            }
        }
    }

    private static void Migrate(NotesDocument document)
    {
        if (document.SchemaVersion > NotesDocument.CurrentSchemaVersion)
        {
            // Written by a newer WriteLite. The fields this version knows still load;
            // anything else is preserved only if the file is not rewritten, so the
            // safest thing is to keep reading and let the user decide.
            CompatibilityLogger.Technical("notes-schema-ahead", $"version={document.SchemaVersion}");
        }

        document.Notes.RemoveAll(note => note is null);

        foreach (var note in document.Notes)
        {
            note.Items.RemoveAll(item => item is null);

            if (string.IsNullOrEmpty(note.Id))
            {
                note.Id = Guid.NewGuid().ToString("N");
            }
        }

        document.SchemaVersion = NotesDocument.CurrentSchemaVersion;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Flush();
        _disposed = true;
        _saveTimer?.Dispose();
        _saveTimer = null;
    }
}
