using System.IO;
using System.Text.Json;

namespace WriteLite.Services.Documents;

public sealed record RecentDocument(string Path, string Name, DateTimeOffset OpenedAt)
{
    /// <summary>False once the file has been moved or deleted behind WriteLite's back.</summary>
    public bool Exists => File.Exists(Path);
}

/// <summary>
/// The recently-opened list.
/// </summary>
/// <remarks>
/// Kept in its own file rather than in application settings on purpose: settings are
/// documented as carrying no absolute paths, and a recent list is nothing but absolute
/// paths. Separating them also means clearing the list never risks the user's
/// preferences.
///
/// Only paths are stored. No document content ever reaches this file.
/// </remarks>
public sealed class RecentDocumentsStore
{
    private const int MaxEntries = 12;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly object _gate = new();
    private List<RecentDocument>? _cache;

    public RecentDocumentsStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WriteLite",
        "recent-documents.json");

    public IReadOnlyList<RecentDocument> Load()
    {
        lock (_gate)
        {
            if (_cache is not null)
            {
                return _cache;
            }

            try
            {
                if (!File.Exists(_path))
                {
                    _cache = [];
                    return _cache;
                }

                var json = File.ReadAllText(_path);
                _cache = JsonSerializer.Deserialize<List<RecentDocument>>(json, JsonOptions) ?? [];
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                CompatibilityLogger.Technical("recent-documents-load-failed", exception);
                _cache = [];
            }

            return _cache;
        }
    }

    public void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (_gate)
        {
            var entries = Load().ToList();
            entries.RemoveAll(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
            entries.Insert(0, new RecentDocument(path, Path.GetFileName(path), DateTimeOffset.Now));

            if (entries.Count > MaxEntries)
            {
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            }

            _cache = entries;
            Save(entries);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _cache = [];
            Save([]);
        }
    }

    private void Save(List<RecentDocument> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(entries, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CompatibilityLogger.Technical("recent-documents-save-failed", exception);
        }
    }
}
