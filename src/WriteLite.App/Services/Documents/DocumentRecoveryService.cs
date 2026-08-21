using System.IO;
using System.Text;
using System.Text.Json;
using WriteLite.Documents;
using WriteLite.Documents.Export;
using WriteLite.Documents.Model;

namespace WriteLite.Services.Documents;

public sealed record RecoverySnapshot(string OriginalPath, string SnapshotPath, DateTimeOffset SavedAt)
{
    public string DisplayName => string.IsNullOrEmpty(OriginalPath)
        ? "Без названия"
        : Path.GetFileName(OriginalPath);
}

/// <summary>
/// Keeps a recoverable copy of unsaved work.
/// </summary>
/// <remarks>
/// Autosave here deliberately does <em>not</em> write to the user's file. Silently
/// rewriting the document someone is editing is how an unwanted change becomes
/// permanent; instead the snapshot lands in the application's own data directory
/// and is offered back after a crash. The user's file only ever changes when they
/// ask it to.
///
/// Snapshots are written as DOCX because it is the format that preserves the most
/// of the model, and they are removed as soon as the document is saved for real —
/// leaving a copy of someone's document behind after they saved and closed it
/// would be a privacy problem, not a feature.
/// </remarks>
public sealed class DocumentRecoveryService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly DocxExporter _exporter = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..12];

    public DocumentRecoveryService(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WriteLite",
            "recovery");
    }

    /// <summary>How long after the last keystroke a snapshot is taken.</summary>
    public static TimeSpan Interval => TimeSpan.FromSeconds(20);

    /// <summary>
    /// Writes a snapshot of the current document.
    /// </summary>
    /// <remarks>
    /// Serialised through a semaphore rather than allowed to overlap: two exports
    /// racing onto one path is how a recovery file ends up truncated, which is
    /// precisely when it is needed.
    /// </remarks>
    public async Task SaveSnapshotAsync(
        WlDocument document,
        string? originalPath,
        CancellationToken cancellationToken = default)
    {
        if (!await _writeGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // A snapshot is already being written; the next tick covers this state.
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var snapshotPath = Path.Combine(_directory, $"session-{_sessionId}.docx");
            var temporary = snapshotPath + ".tmp";

            await using (var stream = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             bufferSize: 32 * 1024,
                             useAsync: true))
            {
                await _exporter.ExportAsync(document, stream, null, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, snapshotPath, overwrite: true);

            var metadata = new RecoverySnapshot(originalPath ?? string.Empty, snapshotPath, DateTimeOffset.Now);
            await File.WriteAllTextAsync(
                Path.Combine(_directory, $"session-{_sessionId}.json"),
                JsonSerializer.Serialize(metadata, JsonOptions),
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("document-autosave-failed", $"type={exception.GetType().Name}");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Snapshots left behind by earlier sessions, newest first.</summary>
    public IReadOnlyList<RecoverySnapshot> FindOrphaned()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return [];
            }

            return Directory.EnumerateFiles(_directory, "session-*.json")
                .Where(path => !path.Contains(_sessionId, StringComparison.Ordinal))
                .Select(TryRead)
                .OfType<RecoverySnapshot>()
                .Where(snapshot => File.Exists(snapshot.SnapshotPath))
                .OrderByDescending(snapshot => snapshot.SavedAt)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static RecoverySnapshot? TryRead(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<RecoverySnapshot>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Removes this session's snapshot once the work is safely on disk.</summary>
    public void Discard() => Discard(_sessionId);

    public void Discard(string sessionId)
    {
        foreach (var extension in new[] { ".docx", ".json" })
        {
            TryDelete(Path.Combine(_directory, $"session-{sessionId}{extension}"));
        }
    }

    public void DiscardSnapshot(RecoverySnapshot snapshot)
    {
        TryDelete(snapshot.SnapshotPath);
        TryDelete(Path.ChangeExtension(snapshot.SnapshotPath, ".json"));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Recovery files are disposable by nature.
        }
    }

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
