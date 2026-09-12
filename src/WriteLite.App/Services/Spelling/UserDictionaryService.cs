using System.Text.Json;
using System.IO;
using WriteLite.Language.Russian;

namespace WriteLite.Services.Spelling;

public sealed class UserDictionaryService
{
    public const int CurrentSchemaVersion = 2;

    private readonly HashSet<string> _words;
    private readonly string? _path;
    private readonly object _gate = new();

    private UserDictionaryService(HashSet<string> words, string? path)
    {
        _words = words;
        _path = path;
    }

    /// <summary>In-memory dictionary for tests (no disk I/O).</summary>
    public static UserDictionaryService CreateEmpty()
        => new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), path: null);

    public static UserDictionaryService LoadDefault()
        => Load(GetDefaultPath());

    public static UserDictionaryService Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var created = new UserDictionaryService(new HashSet<string>(StringComparer.OrdinalIgnoreCase), path);
                created.PersistUnlocked();
                return created;
            }

            var json = ReadAllTextShared(path);
            string[] words;
            var migrated = false;
            using (var document = JsonDocument.Parse(json))
            {
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    // v1 was a bare string array. Read it once and immediately
                    // persist the versioned RU-only v2 document.
                    words = JsonSerializer.Deserialize<string[]>(json) ?? [];
                    migrated = true;
                }
                else
                {
                    var store = JsonSerializer.Deserialize<UserDictionaryDocument>(json)
                        ?? throw new InvalidDataException("User dictionary document is empty.");
                    if (store.SchemaVersion is < 1 or > CurrentSchemaVersion
                        || !string.Equals(store.Language, RussianLanguageProfile.IsoCode, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("Unsupported user dictionary schema or language.");
                    }

                    words = store.Words ?? [];
                    migrated = store.SchemaVersion != CurrentSchemaVersion;
                }
            }

            var service = new UserDictionaryService(
                words.Where(IsValidRussianWord).ToHashSet(StringComparer.OrdinalIgnoreCase),
                path);
            if (migrated)
            {
                service.PersistUnlocked();
            }

            return service;
        }
        catch
        {
            // Corrupted / locked store must not break the app.
            return new UserDictionaryService(new HashSet<string>(StringComparer.OrdinalIgnoreCase), path);
        }
    }

    public bool Contains(string word)
    {
        lock (_gate)
        {
            return _words.Contains(word);
        }
    }

    public void Add(string word)
    {
        if (!IsValidRussianWord(word)) return;
        lock (_gate)
        {
            if (!_words.Add(word.Trim())) return;
            PersistUnlocked();
        }
    }

    public bool Remove(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        lock (_gate)
        {
            if (!_words.Remove(word.Trim())) return false;
            PersistUnlocked();
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_words.Count == 0) return;
            _words.Clear();
            PersistUnlocked();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _words.Count;
            }
        }
    }

    public IReadOnlyCollection<string> Snapshot()
    {
        lock (_gate)
        {
            return _words.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    private void PersistUnlocked()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Store words only — never log dictionary contents. Atomic replace.
            var document = new UserDictionaryDocument(
                CurrentSchemaVersion,
                RussianLanguageProfile.IsoCode,
                _words.Order(StringComparer.OrdinalIgnoreCase).ToArray());
            var json = JsonSerializer.Serialize(document);
            AtomicWrite(_path, json);
        }
        catch
        {
            // non-fatal
        }
    }

    private static void AtomicWrite(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        if (File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool IsValidRussianWord(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        var normalized = word.Trim();
        return normalized.Length <= 128
            && normalized.Any(SpellTextAnalyzer.IsRussianLetter)
            && normalized.All(character => SpellTextAnalyzer.IsRussianLetter(character) || character is '-' or '‑');
    }

    public static string GetDefaultPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WriteLite",
            "user-dictionary.json");
    }

    private sealed record UserDictionaryDocument(int SchemaVersion, string Language, string[] Words);
}
