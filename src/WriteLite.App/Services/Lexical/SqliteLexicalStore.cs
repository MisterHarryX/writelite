using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;
using WriteLite.Language.Russian;

namespace WriteLite.Services.Lexical;

/// <summary>
/// Runtime lexical store backed by SQLite.
///
/// Replaces holding every pack in memory: entries are read on demand, so the
/// application starts without parsing ~108 MB of JSON. Measured against the JSON
/// path on the same data (143k entries): load 1990 ms -> 49 ms, working set
/// 406 MB -> 176 MB, at the cost of ~150 us per uncached full lookup, which is an
/// order of magnitude below a display frame. See docs/LEXICAL_STORAGE_BENCHMARK.md.
///
/// The store never throws on missing or damaged data: <see cref="TryOpen"/>
/// returns null and the caller falls back to the JSON packs.
/// </summary>
public sealed class SqliteLexicalStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ConcurrentDictionary<string, LexicalEntry?> _cache = new(StringComparer.Ordinal);
    private readonly int _maxCacheEntries;
    private readonly object _gate = new();

    private SqliteLexicalStore(SqliteConnection connection, int maxCacheEntries)
    {
        _connection = connection;
        _maxCacheEntries = maxCacheEntries;
    }

    /// <summary>Opens the database, or returns null if it is missing or unusable.</summary>
    public static SqliteLexicalStore? TryOpen(string path, int maxCacheEntries = 512)
    {
        SqliteConnection? connection = null;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return null;

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            };
            connection = new SqliteConnection(builder.ToString());
            connection.Open();

            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA temp_store = MEMORY; PRAGMA mmap_size = 268435456;";
                pragma.ExecuteNonQuery();
            }

            var store = new SqliteLexicalStore(connection, maxCacheEntries);
            if (store.EntryCount <= 0)
            {
                store.Dispose();
                return null;
            }

            return store;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            CompatibilityLogger.Technical("lexical-sqlite-open-failed", $"reason={ex.GetType().Name}");
            connection?.Dispose();
            return null;
        }
    }

    public int CachedEntries => _cache.Count;

    public int EntryCount
    {
        get
        {
            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = "SELECT count(*) FROM entry";
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
            catch (SqliteException)
            {
                return 0;
            }
        }
    }

    /// <summary>Manifest of the pack that supplied entries for a language.</summary>
    public LexicalPackManifest? GetManifest(LexicalLanguage language)
    {
        try
        {
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    SELECT pack_id, pack_version, display_name, license, license_note, source, entry_count
                    FROM pack WHERE language = $lang
                    ORDER BY entry_count DESC LIMIT 1
                    """;
                command.Parameters.AddWithValue("$lang", LanguageCode(language));
                using var reader = command.ExecuteReader();
                if (!reader.Read()) return null;

                return new LexicalPackManifest
                {
                    FormatVersion = LexicalPackLoader.CurrentFormatVersion,
                    PackId = reader.GetString(0),
                    PackVersion = reader.GetString(1),
                    DisplayName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    License = reader.GetString(3),
                    LicenseNote = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Source = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Language = LanguageCode(language),
                    EntryCount = reader.GetInt32(6)
                };
            }
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static string LanguageCode(LexicalLanguage language)
        => language == LexicalLanguage.English ? "en" : RussianLanguageProfile.IsoCode;

    private static string Normalize(string value, LexicalLanguage language)
    {
        var folded = value.Trim().ToLowerInvariant();
        return language == LexicalLanguage.English ? folded : folded.Replace('ё', 'е');
    }

    /// <summary>Resolve a surface form to its entry id and headword only.</summary>
    public (long EntryId, string Lemma, string Pos)? ResolveForm(string word, LexicalLanguage language)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        try
        {
            lock (_gate)
            {
                return ResolveFormCore(word, language);
            }
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private (long EntryId, string Lemma, string Pos)? ResolveFormCore(string word, LexicalLanguage language)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT e.entry_id, e.lemma, e.pos
            FROM form f
            JOIN entry e ON e.entry_id = f.entry_id
            WHERE f.normalized = $n AND e.language = $lang
            ORDER BY f.is_lemma DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$n", Normalize(word, language));
        command.Parameters.AddWithValue("$lang", LanguageCode(language));

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
    }

    /// <summary>Full entry, as the dictionary card needs it. Cached for hot words.</summary>
    public LexicalEntry? Lookup(string word, LexicalLanguage language)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;

        var key = LanguageCode(language) + "|" + Normalize(word, language);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        LexicalEntry? entry = null;
        try
        {
            // One lock for the whole read: SqliteConnection is not thread-safe and
            // a single entry is assembled from five related queries.
            lock (_gate)
            {
                var resolved = ResolveFormCore(word, language);
                if (resolved is not null)
                {
                    var (entryId, lemma, pos) = resolved.Value;
                    entry = new LexicalEntry
                    {
                        Lemma = lemma,
                        Language = language,
                        PartOfSpeech = LexicalPackLoader.ParsePos(pos),
                        Inflections = LoadForms(entryId),
                        Definitions = LoadDefinitions(entryId, pos),
                        Examples = LoadExamples(entryId, lemma),
                        Synonyms = LoadLinks(entryId, "synonym", pos),
                        Antonyms = LoadLinks(entryId, "antonym", pos)
                    };
                }
            }
        }
        catch (SqliteException ex)
        {
            CompatibilityLogger.Technical("lexical-sqlite-query-failed", $"reason={ex.SqliteErrorCode}");
            return null;
        }

        Remember(key, entry);
        return entry;
    }

    private void Remember(string key, LexicalEntry? entry)
    {
        if (_cache.Count >= _maxCacheEntries)
        {
            foreach (var stale in _cache.Keys.Take(_maxCacheEntries / 2).ToArray())
                _cache.TryRemove(stale, out _);
        }

        _cache[key] = entry;
    }

    private string[] LoadForms(long entryId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT surface FROM form WHERE entry_id = $id AND is_lemma = 0";
        command.Parameters.AddWithValue("$id", entryId);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private LexicalDefinition[] LoadDefinitions(long entryId, string entryPos)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT text, pos, label, sense_id, source_id
            FROM definition WHERE entry_id = $id ORDER BY ordinal
            """;
        command.Parameters.AddWithValue("$id", entryId);
        using var reader = command.ExecuteReader();
        var values = new List<LexicalDefinition>();
        while (reader.Read())
        {
            values.Add(new LexicalDefinition(
                reader.GetString(0),
                LexicalPackLoader.ParsePos(reader.IsDBNull(1) ? entryPos : reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4)));
        }

        return values.ToArray();
    }

    private LexicalExample[] LoadExamples(long entryId, string lemma)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT text, sense_id FROM example WHERE entry_id = $id";
        command.Parameters.AddWithValue("$id", entryId);
        using var reader = command.ExecuteReader();
        var values = new List<LexicalExample>();
        while (reader.Read())
        {
            values.Add(new LexicalExample(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), lemma));
        }

        return values.ToArray();
    }

    private LexicalSuggestion[] LoadLinks(long entryId, string kind, string entryPos)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT value, pos, relevance, source_id
            FROM sense_link WHERE entry_id = $id AND kind = $kind
            """;
        command.Parameters.AddWithValue("$id", entryId);
        command.Parameters.AddWithValue("$kind", kind);
        using var reader = command.ExecuteReader();
        var values = new List<LexicalSuggestion>();
        while (reader.Read())
        {
            var value = reader.GetString(0);
            values.Add(new LexicalSuggestion(
                value,
                value,
                LexicalPackLoader.ParsePos(reader.IsDBNull(1) ? entryPos : reader.GetString(1)),
                null,
                reader.IsDBNull(2) ? 0.5 : reader.GetDouble(2),
                true,
                SenseId: null,
                SourceId: reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return values.ToArray();
    }

    /// <summary>
    /// Full-text search over definitions, which the in-memory JSON index could not
    /// do at all. Returns the lemmas whose senses match the query.
    /// </summary>
    public IReadOnlyList<string> SearchDefinitions(string query, LexicalLanguage language, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        try
        {
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    SELECT DISTINCT e.lemma
                    FROM definition_fts
                    JOIN definition d ON d.definition_id = definition_fts.rowid
                    JOIN entry e ON e.entry_id = d.entry_id
                    WHERE definition_fts MATCH $q AND e.language = $lang
                    LIMIT $limit
                    """;
                command.Parameters.AddWithValue("$q", query);
                command.Parameters.AddWithValue("$lang", LanguageCode(language));
                command.Parameters.AddWithValue("$limit", limit);

                using var reader = command.ExecuteReader();
                var results = new List<string>();
                while (reader.Read()) results.Add(reader.GetString(0));
                return results;
            }
        }
        catch (SqliteException)
        {
            // A malformed FTS5 query is user input, not a failure worth surfacing.
            return [];
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }
}
