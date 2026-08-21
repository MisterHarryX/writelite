using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;

namespace WriteLite.Services.Lexical;

/// <summary>
/// Russian ↔ English translation lookup, backed by SQLite.
/// </summary>
/// <remarks>
/// The lexical packs describe one language each: the Russian pack knows what
/// «красивый» means, the English pack knows what "beautiful" means, and until this
/// index existed nothing in the product knew they were the same idea.
///
/// The data comes from OpenRussian's <c>translations_en</c> column — the same pinned
/// source the Russian pack is built from — via
/// <c>ai/scripts/build_ru_en_translations.py</c>. It is never generated, inferred or
/// asked of a model: a fabricated translation looks exactly like a real one right up
/// until someone relies on it.
///
/// Absence is normal and is not an error. Roughly a quarter of the Russian lemmas in
/// the source carry no English gloss, and when the file is missing entirely the
/// dictionary simply shows no English section.
/// </remarks>
public sealed class TranslationIndex : IDisposable
{
    private const int MaxCacheEntries = 256;

    private readonly SqliteConnection _connection;
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _cache = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private TranslationIndex(SqliteConnection connection) => _connection = connection;

    /// <summary>Opens the index, or returns null when it is absent or unusable.</summary>
    /// <remarks>
    /// Never throws. A damaged translation file must cost the user their English
    /// column, not their dictionary.
    /// </remarks>
    public static TranslationIndex? TryOpen(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "resources", "lexical", "ru-en-translations.db");

        SqliteConnection? connection = null;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                return null;
            }

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            };
            connection = new SqliteConnection(builder.ToString());
            connection.Open();

            var index = new TranslationIndex(connection);
            if (index.PairCount <= 0)
            {
                index.Dispose();
                return null;
            }

            CompatibilityLogger.Technical("translations-loaded", $"pairs={index.PairCount}");
            return index;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            CompatibilityLogger.Technical("translations-open-failed", $"reason={ex.GetType().Name}");
            connection?.Dispose();
            return null;
        }
    }

    public int PairCount
    {
        get
        {
            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = "SELECT count(*) FROM translation";
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
            catch (SqliteException)
            {
                return 0;
            }
        }
    }

    /// <summary>Licence and provenance, for the dictionary's source line.</summary>
    public string? License => ReadManifest("license");

    public string? Source => ReadManifest("source");

    /// <summary>
    /// Translations of <paramref name="word"/>, which is assumed to be in
    /// <paramref name="from"/>. Returns an empty list when there are none.
    /// </summary>
    public IReadOnlyList<string> Translate(string? word, LexicalLanguage from)
    {
        if (string.IsNullOrWhiteSpace(word) || from == LexicalLanguage.Unknown)
        {
            return [];
        }

        var source = from == LexicalLanguage.English ? "en" : "ru";
        var normalized = Normalize(word, from);
        var key = source + "|" + normalized;

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var results = new List<string>();
        try
        {
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    "SELECT value FROM translation WHERE source = $s AND normalized = $n ORDER BY ordinal";
                command.Parameters.AddWithValue("$s", source);
                command.Parameters.AddWithValue("$n", normalized);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(reader.GetString(0));
                }
            }
        }
        catch (SqliteException ex)
        {
            CompatibilityLogger.Technical("translations-query-failed", $"reason={ex.SqliteErrorCode}");
            return [];
        }

        IReadOnlyList<string> value = results;
        Remember(key, value);
        return value;
    }

    /// <summary>
    /// True when the index has any entry for a headword — used to tell a chip that
    /// leads somewhere from one that would open an empty article.
    /// </summary>
    public bool HasEntry(string? word, LexicalLanguage language) => Translate(word, language).Count > 0;

    private string? ReadManifest(string key)
    {
        try
        {
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "SELECT value FROM manifest WHERE key = $k";
                command.Parameters.AddWithValue("$k", key);
                return command.ExecuteScalar() as string;
            }
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private void Remember(string key, IReadOnlyList<string> value)
    {
        if (_cache.Count >= MaxCacheEntries)
        {
            foreach (var stale in _cache.Keys.Take(MaxCacheEntries / 2).ToArray())
            {
                _cache.TryRemove(stale, out _);
            }
        }

        _cache[key] = value;
    }

    /// <summary>
    /// Folds a headword to the form the index is keyed by. English glosses are
    /// stored bare, so "to read" and "the book" have to lose the particle they were
    /// written with before they will match.
    /// </summary>
    public static string Normalize(string word, LexicalLanguage language)
    {
        var value = word.Trim().ToLowerInvariant();

        if (language != LexicalLanguage.English)
        {
            return value.Replace('ё', 'е');
        }

        foreach (var prefix in new[] { "to ", "the ", "a ", "an " })
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal) && value.Length > prefix.Length)
            {
                value = value[prefix.Length..];
                break;
            }
        }

        return value;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }
}
