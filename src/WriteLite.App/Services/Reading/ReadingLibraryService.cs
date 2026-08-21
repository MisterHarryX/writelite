using System.IO;
using WriteLite.Services.Storage;

namespace WriteLite.Services.Reading;

/// <summary>
/// The shelf: which books have projects, and how to get one open.
/// </summary>
/// <remarks>
/// Two levels of storage on purpose. <c>library.json</c> holds one small row per
/// project so the reading home screen can be drawn without deserialising anyone's
/// annotations, and each project's marks live in their own file under
/// <c>projects/</c> — which also means a single corrupt project costs one book rather
/// than the shelf.
///
/// <see cref="OpenOrCreate"/> is the whole point of the type. It looks a file up by
/// content fingerprint, not by path, so the second time a book is opened — from a
/// different folder, under a new name, after being copied off a memory stick — it
/// lands in the project that already holds the highlights instead of starting an
/// empty one beside it.
/// </remarks>
public sealed class ReadingLibraryService : IDisposable
{
    /// <summary>Quiet period before a deferred change reaches the disk.</summary>
    private static readonly TimeSpan WriteDelay = TimeSpan.FromMilliseconds(400);

    private readonly string _libraryPath;
    private readonly string _projectsRoot;
    private readonly object _gate = new();

    // One writer, one lock, one pending snapshot per project. Two threads writing the
    // same project file is the only way this store can corrupt a book's marks, and a
    // per-id pending map is also what collapses a slider drag into one write.
    private readonly object _writeGate = new();
    private readonly Dictionary<string, ReadingProject> _pending = [];
    private ReadingLibraryDocument _library;
    private System.Threading.Timer? _writeTimer;
    private bool _indexDirty;
    private bool _disposed;

    public ReadingLibraryService(string? libraryPath = null, string? projectsRoot = null)
    {
        _libraryPath = libraryPath ?? WriteLiteDataPaths.ReadingLibraryFile;
        _projectsRoot = projectsRoot ?? WriteLiteDataPaths.ReadingProjectsRoot;
        _library = AtomicJsonFile.Read<ReadingLibraryDocument>(_libraryPath) ?? new ReadingLibraryDocument();
        _library.Projects.RemoveAll(project => project is null || string.IsNullOrEmpty(project.Id));
    }

    /// <summary>Raised when a project is added, removed or its summary changes.</summary>
    public event Action? Changed;

    public string LibraryPath => _libraryPath;

    public string ProjectsRoot => _projectsRoot;

    /// <summary>Every project, most recently opened first.</summary>
    public IReadOnlyList<ReadingProjectSummary> Projects
    {
        get
        {
            lock (_gate)
            {
                return _library.Projects
                    .OrderByDescending(project => project.LastOpenedAt)
                    .ToList();
            }
        }
    }

    /// <summary>The book to offer under «Продолжить чтение»: the last one opened that was actually started.</summary>
    public ReadingProjectSummary? ContinueReading =>
        Projects.FirstOrDefault(project => project.Progress is > 0.001 and < 0.999)
        ?? Projects.FirstOrDefault();

    /// <summary>True when the file behind a project can still be found.</summary>
    public static bool SourceExists(ReadingProjectSummary summary) =>
        !string.IsNullOrWhiteSpace(summary.SourcePath) && File.Exists(summary.SourcePath);

    /// <summary>
    /// Returns the project for a file, creating one only if this book has never been opened.
    /// </summary>
    /// <remarks>
    /// When the fingerprint matches an existing project the stored path is refreshed,
    /// so a book that was moved is found at its new location next time without the
    /// user re-importing it.
    /// </remarks>
    public ReadingProject OpenOrCreate(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Файл не найден.", fullPath);
        }

        var fingerprint = DocumentFingerprint.Compute(fullPath);
        var info = new FileInfo(fullPath);

        ReadingProjectSummary? existing;
        lock (_gate)
        {
            existing = _library.Projects.FirstOrDefault(project =>
                string.Equals(project.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        }

        if (existing is not null && Load(existing.Id) is { } reopened)
        {
            reopened.SourcePath = fullPath;
            reopened.SourceFileName = info.Name;
            reopened.SourceLength = info.Length;
            reopened.LastOpenedAt = DateTimeOffset.Now;
            Save(reopened);
            return reopened;
        }

        var project = new ReadingProject
        {
            Title = CleanTitle(info.Name),
            SourcePath = fullPath,
            SourceFileName = info.Name,
            SourceLength = info.Length,
            SourceFormat = info.Extension.TrimStart('.').ToUpperInvariant(),
            Fingerprint = fingerprint
        };

        Save(project);
        return project;
    }

    public ReadingProject? Load(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        var project = AtomicJsonFile.Read<ReadingProject>(ProjectPath(projectId));
        if (project is null)
        {
            return null;
        }

        Migrate(project);
        return project;
    }

    /// <summary>Writes a project now. Used on shutdown and wherever the caller must see the file.</summary>
    public void Save(ReadingProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        Normalize(project);
        UpdateIndex(ReadingProjectSummary.From(project));

        // Dropped from the pending set first: a deferred write scheduled a moment ago
        // must not land after this one and put an older snapshot back on the disk.
        lock (_writeGate)
        {
            _pending.Remove(project.Id);
        }

        WriteProject(project);
        WriteIndex();
    }

    /// <summary>
    /// Records a change and lets the disk catch up.
    /// </summary>
    /// <remarks>
    /// The in-memory index and <see cref="Changed"/> happen immediately, because those
    /// are what the UI reads; only the file write is deferred and coalesced. That is
    /// what makes dragging the leading slider cost one write rather than one per tick,
    /// and it keeps every write off the dispatcher — a project with a few hundred marks
    /// is a real serialisation, and it was previously happening synchronously on the UI
    /// thread on every scroll stop, highlight and double-clicked word.
    ///
    /// The pending snapshot is the live project object rather than a copy, so a change
    /// made between scheduling and writing is included rather than lost.
    /// <see cref="Flush"/> is called whenever the reader is suspended or closed, so the
    /// window in which a crash could cost anything is the quiet period, not the session.
    /// </remarks>
    public void SaveDeferred(ReadingProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        Normalize(project);
        UpdateIndex(ReadingProjectSummary.From(project));

        if (_disposed)
        {
            return;
        }

        lock (_writeGate)
        {
            _pending[project.Id] = project;
            _writeTimer ??= new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _writeTimer.Change(WriteDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes everything still pending. Called when the reader is suspended, closed or the app exits.</summary>
    public void Flush()
    {
        ReadingProject[] projects;
        bool writeIndex;

        lock (_writeGate)
        {
            if (_pending.Count == 0 && !_indexDirty)
            {
                return;
            }

            projects = [.. _pending.Values];
            _pending.Clear();
            writeIndex = _indexDirty;
            _indexDirty = false;
        }

        foreach (var project in projects)
        {
            WriteProject(project);
        }

        if (writeIndex || projects.Length > 0)
        {
            WriteIndex();
        }
    }

    private static void Normalize(ReadingProject project)
    {
        project.SchemaVersion = ReadingProject.CurrentSchemaVersion;
        project.Typography = project.Typography.Clamped();
    }

    /// <summary>The one place a project file is written. Serialised so two writers cannot interleave.</summary>
    private void WriteProject(ReadingProject project)
    {
        lock (_writeGate)
        {
            try
            {
                AtomicJsonFile.Write(ProjectPath(project.Id), project);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                CompatibilityLogger.Technical("reading-project-save-failed", $"type={exception.GetType().Name}");

                // Left pending on purpose: the data is still in memory and the next
                // flush retries. Dropping it would turn a transient lock into a
                // silently lost annotation.
                _pending.TryAdd(project.Id, project);
            }
        }
    }

    public void Delete(string projectId)
    {
        lock (_writeGate)
        {
            _pending.Remove(projectId);
        }

        AtomicJsonFile.Delete(ProjectPath(projectId));

        lock (_gate)
        {
            _library.Projects.RemoveAll(project => project.Id == projectId);
        }

        WriteIndex();
        Changed?.Invoke();
    }

    public void Rename(string projectId, string title)
    {
        if (Load(projectId) is not { } project)
        {
            return;
        }

        project.Title = string.IsNullOrWhiteSpace(title) ? project.SourceFileName : title.Trim();
        Save(project);
    }

    private void UpdateIndex(ReadingProjectSummary summary)
    {
        lock (_gate)
        {
            var index = _library.Projects.FindIndex(project => project.Id == summary.Id);
            if (index >= 0)
            {
                _library.Projects[index] = summary;
            }
            else
            {
                _library.Projects.Add(summary);
            }
        }

        lock (_writeGate)
        {
            _indexDirty = true;
        }

        Changed?.Invoke();
    }

    private void WriteIndex()
    {
        ReadingLibraryDocument snapshot;
        lock (_gate)
        {
            snapshot = new ReadingLibraryDocument
            {
                SchemaVersion = ReadingLibraryDocument.CurrentSchemaVersion,
                Projects = _library.Projects.ToList()
            };
        }

        lock (_writeGate)
        {
            try
            {
                AtomicJsonFile.Write(_libraryPath, snapshot);
                _indexDirty = false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                CompatibilityLogger.Technical("reading-library-save-failed", $"type={exception.GetType().Name}");
                _indexDirty = true;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Flush();
        _disposed = true;

        lock (_writeGate)
        {
            _writeTimer?.Dispose();
            _writeTimer = null;
        }
    }

    private string ProjectPath(string projectId) => Path.Combine(_projectsRoot, $"{projectId}.json");

    private static void Migrate(ReadingProject project)
    {
        project.Highlights.RemoveAll(item => item is null || item.Anchor is null);
        project.Annotations.RemoveAll(item => item is null || item.Anchor is null);
        project.Bookmarks.RemoveAll(item => item is null);
        project.Cards.RemoveAll(item => item is null);
        project.Words.RemoveAll(item => item is null || string.IsNullOrWhiteSpace(item.Word));

        project.Typography = (project.Typography ?? ReaderTypography.Default).Clamped();
        project.SchemaVersion = ReadingProject.CurrentSchemaVersion;
    }

    /// <summary>Turns "War_and_Peace (1).pdf" into something worth showing as a title.</summary>
    private static string CleanTitle(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Replace('-', ' ');
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s{2,}", " ").Trim();
        return name.Length == 0 ? fileName : name;
    }
}
