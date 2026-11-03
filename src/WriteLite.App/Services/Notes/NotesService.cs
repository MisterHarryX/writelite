using System.IO;
using System.Text.Json;
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
///
/// Projects and the pictures inside notes belong to the same service, and so to the
/// same file and the same lock: a note moved into a project and the project deleted a
/// moment later are two writes to one document, never two documents that can disagree.
/// </remarks>
public sealed class NotesService : IDisposable
{
    /// <summary>Quiet period before an edit reaches the disk.</summary>
    private static readonly TimeSpan SaveDelay = WriteLiteDefaults.Debounce.NotesSaveDelay;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<Note> _notes;
    private readonly List<NoteProject> _projects;
    private readonly NoteAssetStore _assets;
    private System.Threading.Timer? _saveTimer;
    private bool _dirty;
    private bool _disposed;

    public NotesService(string? path = null)
    {
        _path = path ?? WriteLiteDataPaths.NotesFile;

        var loaded = AtomicJsonFile.Read<NotesDocument>(_path);
        var document = loaded ?? new NotesDocument();
        Migrate(document);
        _notes = document.Notes;
        _projects = document.Projects;

        _assets = new NoteAssetStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_path)) ?? ".", "notes-assets"));

        // Only against a file that was actually read. A notes file that failed to parse
        // looks exactly like an empty board, and sweeping against that would delete every
        // picture the user has — the one loss the backup file cannot undo.
        if (loaded is not null)
        {
            SweepAssets();
        }
    }

    /// <summary>Raised after any change, so the board can redraw.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised on the owning thread just before the final save, so an open editor can hand
    /// over the edit it is still holding back for its own debounce.
    /// </summary>
    public event Action? Closing;

    public string StorePath => _path;

    /// <summary>The pictures stored beside the notes. Reached only through this service.</summary>
    public NoteAssetStore Assets => _assets;

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
    public IReadOnlyList<Note> Query(NotesFilter filter, string? search, NotesSort sort) =>
        Query(filter, search, sort, NotesScope.Everything);

    /// <summary>The same query, limited to the notes of one scope.</summary>
    /// <remarks>
    /// Inside a project the plain tasks are left off the board on the "all" tab: the
    /// project page lists them as a checklist of its own above the board, and drawing each
    /// one twice — once as a row, once as a card — is how a list stops being glanceable.
    /// </remarks>
    public IReadOnlyList<Note> Query(NotesFilter filter, string? search, NotesSort sort, NotesScope scope)
    {
        List<Note> snapshot;
        lock (_gate)
        {
            snapshot = _notes.ToList();
        }

        var matching = snapshot.Where(note =>
            !note.IsDeleted
            && InScope(note, scope)
            && !(scope.Kind == NotesScopeKind.Project && filter == NotesFilter.All && note.Kind == NoteKind.Task)
            && Matches(note, filter, search));

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

    private static bool InScope(Note note, NotesScope scope) => scope.Kind switch
    {
        NotesScopeKind.Unfiled => note.ProjectId is null,
        NotesScopeKind.Project => note.ProjectId == scope.ProjectId,
        _ => true
    };

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
               || note.Items.Any(item => Contains(item.Text, needle))
               || (note.Content?.Any(block => Contains(block.BookmarkName, needle)) ?? false);
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
    public (int Active, int Tasks, int Goals, int Completed) Counts() => Counts(NotesScope.Everything);

    public (int Active, int Tasks, int Goals, int Completed) Counts(NotesScope scope)
    {
        lock (_gate)
        {
            var notes = _notes.Where(note => !note.IsDeleted && InScope(note, scope)).ToList();
            return (
                notes.Count(note => !note.IsCompleted),
                notes.Count(note => !note.IsCompleted && note.Kind is NoteKind.Task or NoteKind.Checklist),
                notes.Count(note => !note.IsCompleted && note.Kind == NoteKind.Goal),
                notes.Count(note => note.IsCompleted));
        }
    }

    /// <summary>How many notes a scope holds, for the project list.</summary>
    public int CountIn(NotesScope scope)
    {
        lock (_gate)
        {
            return _notes.Count(note => !note.IsDeleted && InScope(note, scope));
        }
    }

    // ── Changing the board ───────────────────────────────────────────────────

    public Note Create(NoteKind kind = NoteKind.Note, string? title = null, string? projectId = null)
    {
        var note = new Note
        {
            Kind = kind,
            Title = title ?? string.Empty
        };

        lock (_gate)
        {
            // A project that no longer exists is not a place to put a note.
            note.ProjectId = projectId is not null && _projects.Any(project => project.Id == projectId) ? projectId : null;

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
            if (_notes.RemoveAll(note => note.Id == id) == 0)
            {
                return;
            }
        }

        _assets.DeleteAll(id);
        Commit();
    }

    /// <summary>How long a note stays in «Недавно удалённые» before it is gone for good.</summary>
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);

    /// <summary>Moves a note to «Недавно удалённые»; it can be restored for 30 days.</summary>
    public void MoveToTrash(string id)
    {
        lock (_gate)
        {
            var note = _notes.FirstOrDefault(n => n.Id == id);
            if (note is null || note.IsDeleted) return;
            note.DeletedAt = DateTimeOffset.Now;
            note.IsPinned = false;
        }

        Commit();
    }

    /// <summary>Brings a note back from «Недавно удалённые».</summary>
    public void Restore(string id)
    {
        lock (_gate)
        {
            var note = _notes.FirstOrDefault(n => n.Id == id);
            if (note is null || !note.IsDeleted) return;
            note.DeletedAt = null;
            note.Touch();
        }

        Commit();
    }

    /// <summary>Deletes everything in «Недавно удалённые» for good.</summary>
    public void EmptyTrash()
    {
        List<string> ids;
        lock (_gate)
        {
            ids = _notes.Where(n => n.IsDeleted).Select(n => n.Id).ToList();
            _notes.RemoveAll(n => n.IsDeleted);
        }

        foreach (var id in ids) _assets.DeleteAll(id);
        if (ids.Count > 0) Commit();
    }

    /// <summary>Removes notes that have been in the trash longer than <see cref="TrashRetention"/>.</summary>
    public int PurgeExpiredTrash(DateTimeOffset? now = null)
    {
        var cutoff = (now ?? DateTimeOffset.Now) - TrashRetention;
        List<string> ids;
        lock (_gate)
        {
            ids = _notes.Where(n => n.DeletedAt is { } at && at < cutoff).Select(n => n.Id).ToList();
            _notes.RemoveAll(n => n.DeletedAt is { } at && at < cutoff);
        }

        foreach (var id in ids) _assets.DeleteAll(id);
        if (ids.Count > 0) Commit();
        return ids.Count;
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

        // Pictures are copied, not shared: deleting the original must not blank the copy.
        try
        {
            _assets.CopyAll(id, copy.Id);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            CompatibilityLogger.Technical("notes-assets-copy-failed", exception.GetType().Name);
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

    // ── Projects ─────────────────────────────────────────────────────────────

    /// <summary>Every project, in list order. Copies: editing one changes nothing until saved here.</summary>
    public IReadOnlyList<NoteProject> Projects
    {
        get
        {
            lock (_gate)
            {
                return _projects
                    .OrderBy(project => project.Order)
                    .ThenBy(project => project.CreatedAt)
                    .Select(project => project.Clone())
                    .ToList();
            }
        }
    }

    public NoteProject? FindProject(string? id)
    {
        if (id is null)
        {
            return null;
        }

        lock (_gate)
        {
            return _projects.FirstOrDefault(project => project.Id == id)?.Clone();
        }
    }

    public NoteProject CreateProject(string name)
    {
        var project = new NoteProject { Name = CleanName(name) };

        lock (_gate)
        {
            project.Order = _projects.Count == 0 ? 0 : _projects.Max(existing => existing.Order) + 1;
            _projects.Add(project);
        }

        Commit();
        return project.Clone();
    }

    public void RenameProject(string id, string name) =>
        MutateProject(id, project => project.Name = CleanName(name));

    public void SetProjectDescription(string id, string description) =>
        MutateProject(id, project => project.Description = (description ?? string.Empty).TrimEnd());

    /// <summary>
    /// Removes a project.
    /// </summary>
    /// <param name="deleteNotes">
    /// False keeps every note of the project and moves it to "без проекта" — the default,
    /// because a project is a folder, and deleting a folder by accident should not cost
    /// what was written in it.
    /// </param>
    public void DeleteProject(string id, bool deleteNotes = false)
    {
        List<string> removedNotes = [];
        lock (_gate)
        {
            if (_projects.RemoveAll(project => project.Id == id) == 0)
            {
                return;
            }

            foreach (var note in _notes.Where(note => note.ProjectId == id).ToList())
            {
                if (deleteNotes)
                {
                    _notes.Remove(note);
                    removedNotes.Add(note.Id);
                }
                else
                {
                    note.ProjectId = null;
                    note.Touch();
                }
            }
        }

        foreach (var noteId in removedNotes)
        {
            _assets.DeleteAll(noteId);
        }

        Commit();
    }

    /// <summary>Moves a note into a project, or out of every project when <paramref name="projectId"/> is null.</summary>
    public void MoveNote(string noteId, string? projectId)
    {
        lock (_gate)
        {
            if (projectId is not null && _projects.All(project => project.Id != projectId))
            {
                return;
            }

            if (_notes.FirstOrDefault(note => note.Id == noteId) is not { } note || note.ProjectId == projectId)
            {
                return;
            }

            note.ProjectId = projectId;
            note.Touch();
        }

        Commit();
    }

    public ProjectBookmark? AddProjectBookmark(string projectId, string title, string target)
    {
        var cleanTarget = (target ?? string.Empty).Trim();
        if (cleanTarget.Length == 0)
        {
            return null;
        }

        var bookmark = new ProjectBookmark
        {
            Title = string.IsNullOrWhiteSpace(title) ? cleanTarget : title.Trim(),
            Target = cleanTarget
        };

        var added = false;
        MutateProject(projectId, project =>
        {
            project.Bookmarks.Add(bookmark);
            added = true;
        });

        return added ? bookmark : null;
    }

    public void RemoveProjectBookmark(string projectId, string bookmarkId) =>
        MutateProject(projectId, project => project.Bookmarks.RemoveAll(link => link.Id == bookmarkId));

    /// <summary>The project page in one snapshot: its notes, its tasks and every bookmark inside them.</summary>
    public ProjectOverview? Overview(string projectId)
    {
        lock (_gate)
        {
            var project = _projects.FirstOrDefault(candidate => candidate.Id == projectId);
            if (project is null)
            {
                return null;
            }

            var members = _notes.Where(note => note.ProjectId == projectId).ToList();

            // Open tasks first, in the order they were written down; finished ones after
            // them, most recently finished first — the shape of a to-do list.
            var open = members
                .Where(note => note.Kind == NoteKind.Task && !note.IsCompleted)
                .OrderBy(note => note.CreatedAt);
            var done = members
                .Where(note => note.Kind == NoteKind.Task && note.IsCompleted)
                .OrderByDescending(note => note.CompletedAt ?? note.ModifiedAt);

            var notes = members
                .Where(note => note.Kind != NoteKind.Task)
                .OrderByDescending(note => note.IsPinned)
                .ThenByDescending(note => note.ModifiedAt)
                .ToList();

            var bookmarks = members
                .OrderByDescending(note => note.ModifiedAt)
                .SelectMany(NoteContent.Bookmarks)
                .ToList();

            return new ProjectOverview(project.Clone(), notes, open.Concat(done).ToList(), bookmarks);
        }
    }

    private void MutateProject(string id, Action<NoteProject> change)
    {
        lock (_gate)
        {
            if (_projects.FirstOrDefault(project => project.Id == id) is not { } project)
            {
                return;
            }

            change(project);
            project.ModifiedAt = DateTimeOffset.Now;
        }

        Commit();
    }

    private static string CleanName(string? name)
    {
        var clean = (name ?? string.Empty).Trim();
        return clean.Length <= 80 ? clean : clean[..80].TrimEnd();
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
        byte[] json;
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;

            // Serialized inside the lock: the notes are live objects the dispatcher keeps
            // changing, and the serializer walking a checklist while a tick lands on it is
            // a "collection was modified" that would lose the save. Only the disk write,
            // the slow part, runs outside.
            json = JsonSerializer.SerializeToUtf8Bytes(
                new NotesDocument
                {
                    SchemaVersion = NotesDocument.CurrentSchemaVersion,
                    Notes = _notes,
                    Projects = _projects
                },
                AtomicJsonFile.Options);
        }

        try
        {
            AtomicJsonFile.WriteSerialized(_path, json);
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

        document.Notes ??= [];
        document.Notes.RemoveAll(note => note is null);
        document.Projects ??= [];
        document.Projects.RemoveAll(project => project is null);

        foreach (var project in document.Projects)
        {
            project.Bookmarks ??= [];
            project.Bookmarks.RemoveAll(link => link is null);
            if (string.IsNullOrEmpty(project.Id))
            {
                project.Id = Guid.NewGuid().ToString("N");
            }
        }

        var projectIds = document.Projects.Select(project => project.Id).ToHashSet();

        foreach (var note in document.Notes)
        {
            note.Items ??= [];
            note.Items.RemoveAll(item => item is null);
            note.Content?.RemoveAll(block => block is null);
            foreach (var block in note.Content ?? [])
            {
                block.Spans ??= [];
                block.Spans.RemoveAll(span => span is null);
            }

            if (string.IsNullOrEmpty(note.Id))
            {
                note.Id = Guid.NewGuid().ToString("N");
            }

            // A note whose project was removed by hand, or by a version that did not
            // move its notes out first, belongs to no project rather than to a ghost.
            if (note.ProjectId is not null && !projectIds.Contains(note.ProjectId))
            {
                note.ProjectId = null;
            }
        }

        document.SchemaVersion = NotesDocument.CurrentSchemaVersion;
    }

    /// <summary>Removes picture files no note references; safe only before any editor opened.</summary>
    private void SweepAssets()
    {
        Dictionary<string, HashSet<string>> referenced;
        lock (_gate)
        {
            referenced = _notes
                .GroupBy(note => note.Id)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .SelectMany(note => NoteContent.ReferencedAssets(note.Content))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase));
        }

        var removed = _assets.Sweep(referenced);
        if (removed > 0)
        {
            CompatibilityLogger.Technical("notes-assets-swept", $"removed={removed}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Closing?.Invoke();
        }
        catch (Exception exception)
        {
            // An editor failing to hand over its last edit must not stop the save of
            // everything else.
            CompatibilityLogger.Technical("notes-closing-handler-failed", exception.GetType().Name);
        }

        Flush();
        _disposed = true;
        _saveTimer?.Dispose();
        _saveTimer = null;
    }
}
