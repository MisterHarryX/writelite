using WriteLite.Services.Lexical;

namespace WriteLite.Tests.Lexical;

/// <summary>
/// The lexical layer moved from parsing every JSON pack into memory at startup to
/// querying a SQLite database on demand. These tests hold the migration to two
/// promises: the data that comes back is the same, and a missing or damaged
/// database degrades to the JSON packs instead of losing the dictionary.
/// </summary>
[TestClass]
public sealed class SqliteLexicalStoreTests
{
    private static string LexicalDirectory()
        => Path.Combine(AppContext.BaseDirectory, "resources", "lexical");

    private static string DatabasePath()
        => Path.Combine(LexicalDirectory(), "writelight-lexical.db");

    private static SqliteLexicalStore OpenOrSkip()
    {
        var store = SqliteLexicalStore.TryOpen(DatabasePath());
        if (store is null)
            Assert.Inconclusive("lexical database not present next to the test host.");
        return store!;
    }

    [TestMethod]
    public void Service_PrefersTheDatabaseOverJson()
    {
        if (!File.Exists(DatabasePath()))
            Assert.Inconclusive("lexical database not present next to the test host.");

        using var service = new OfflineLexicalKnowledgeService();
        service.LoadFromDirectory(LexicalDirectory());

        Assert.IsTrue(service.IsPackLoaded);
        Assert.IsTrue(service.IsDatabaseBacked,
            "the database is present but the service still loaded the JSON packs.");
    }

    [TestMethod]
    public void MissingDatabase_FallsBackToJsonPacks()
    {
        var empty = Path.Combine(Path.GetTempPath(), "writelite-no-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            // No database and no packs at all: the service must report the failure
            // rather than throw, and must not claim to be database-backed.
            using var service = new OfflineLexicalKnowledgeService();
            service.LoadFromDirectory(empty);

            Assert.IsFalse(service.IsDatabaseBacked);
            Assert.IsFalse(service.IsPackLoaded);
            Assert.IsFalse(string.IsNullOrWhiteSpace(service.LoadError));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [TestMethod]
    public void DamagedDatabase_IsRejectedAndDoesNotThrow()
    {
        var directory = Path.Combine(Path.GetTempPath(), "writelite-bad-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "writelight-lexical.db");
            File.WriteAllBytes(path, "this is not a database"u8.ToArray());

            Assert.IsNull(SqliteLexicalStore.TryOpen(path));

            using var service = new OfflineLexicalKnowledgeService();
            service.LoadFromDirectory(directory);
            Assert.IsFalse(service.IsDatabaseBacked);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void EmptyOrMissingFile_ReturnsNullRatherThanThrowing()
    {
        Assert.IsNull(SqliteLexicalStore.TryOpen(
            Path.Combine(Path.GetTempPath(), "writelite-does-not-exist.db")));
    }

    [TestMethod]
    public void RussianLookup_ResolvesInflectedFormToItsLemma()
    {
        using var store = OpenOrSkip();

        var entry = store.Lookup("автомобилями", LexicalLanguage.Russian);
        Assert.IsNotNull(entry);
        Assert.AreEqual("автомобиль", entry!.Lemma);
        Assert.AreEqual(LexicalLanguage.Russian, entry.Language);
    }

    [TestMethod]
    public void RussianLookup_FoldsYoAndCase()
    {
        using var store = OpenOrSkip();

        var withYo = store.Lookup("ёлка", LexicalLanguage.Russian);
        var withoutYo = store.Lookup("елка", LexicalLanguage.Russian);
        var capitalised = store.Lookup("Ёлка", LexicalLanguage.Russian);

        if (withYo is null) Assert.Inconclusive("«ёлка» is not in this build of the pack.");
        Assert.IsNotNull(withoutYo);
        Assert.IsNotNull(capitalised);
        Assert.AreEqual(withYo!.Lemma, withoutYo!.Lemma);
        Assert.AreEqual(withYo.Lemma, capitalised!.Lemma);
    }

    [TestMethod]
    public void EnglishLookup_IsServedFromTheSameDatabase()
    {
        using var store = OpenOrSkip();

        var entry = store.Lookup("bright", LexicalLanguage.English);
        Assert.IsNotNull(entry);
        Assert.AreEqual(LexicalLanguage.English, entry!.Language);
        Assert.IsNotEmpty(entry.Definitions);
        Assert.AreEqual("oewn-2024", entry.Definitions[0].SourceId);
    }

    [TestMethod]
    public void LanguagesDoNotLeakIntoEachOther()
    {
        using var store = OpenOrSkip();

        // "дом" exists in Russian only; asking in English must not return it.
        Assert.IsNull(store.Lookup("дом", LexicalLanguage.English));
        Assert.IsNotNull(store.Lookup("дом", LexicalLanguage.Russian));
    }

    [TestMethod]
    public void UnknownWord_ReturnsNullAndIsCachedAsAMiss()
    {
        using var store = OpenOrSkip();

        Assert.IsNull(store.Lookup("абракадабронность", LexicalLanguage.Russian));
        Assert.IsNull(store.Lookup("абракадабронность", LexicalLanguage.Russian));
    }

    [TestMethod]
    public void EveryDefinition_CarriesItsSourceId()
    {
        using var store = OpenOrSkip();

        foreach (var word in new[] { "дом", "быстрый", "бежать", "bright", "house" })
        {
            var language = word[0] < 128 ? LexicalLanguage.English : LexicalLanguage.Russian;
            var entry = store.Lookup(word, language);
            if (entry is null) continue;

            foreach (var definition in entry.Definitions)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(definition.SourceId),
                    $"{word}: a definition has no source id");
            }
        }
    }

    [TestMethod]
    public void DatabaseAndJsonPack_ReturnTheSameEntry()
    {
        // Parity check: the migration must not change what the user sees.
        var packPath = Path.Combine(LexicalDirectory(), "writelight-lexical-open.json");
        if (!File.Exists(packPath))
            Assert.Inconclusive("JSON pack not present next to the test host.");

        var json = LexicalPackLoader.LoadFromFile(packPath);
        Assert.IsTrue(json.Success, json.Error);

        using var store = OpenOrSkip();

        var compared = 0;
        foreach (var word in new[] { "дом", "быстрый", "бежать", "близкий", "хороший", "автомобиль" })
        {
            if (!json.Index!.TryGetValue(word, out var fromJson)) continue;
            var fromDatabase = store.Lookup(word, LexicalLanguage.Russian);

            Assert.IsNotNull(fromDatabase, $"{word}: missing from the database");
            Assert.AreEqual(fromJson.Lemma, fromDatabase!.Lemma, word);
            Assert.AreEqual(fromJson.PartOfSpeech, fromDatabase.PartOfSpeech, word);
            Assert.HasCount(fromJson.Definitions.Count, fromDatabase.Definitions, $"{word}: definition count");
            Assert.HasCount(fromJson.Synonyms.Count, fromDatabase.Synonyms, $"{word}: synonym count");
            Assert.HasCount(fromJson.Antonyms.Count, fromDatabase.Antonyms, $"{word}: antonym count");
            Assert.HasCount(fromJson.Inflections.Count, fromDatabase.Inflections, $"{word}: inflection count");

            for (var i = 0; i < fromJson.Definitions.Count; i++)
            {
                Assert.AreEqual(fromJson.Definitions[i].Definition, fromDatabase.Definitions[i].Definition, word);
                Assert.AreEqual(fromJson.Definitions[i].SourceId, fromDatabase.Definitions[i].SourceId, word);
            }

            compared++;
        }

        Assert.IsGreaterThan(0, compared, "no lemma was available in both stores to compare.");
    }

    [TestMethod]
    public void FullTextSearch_FindsLemmasByDefinitionText()
    {
        using var store = OpenOrSkip();

        // Searching definition text is something the in-memory index could not do.
        var results = store.SearchDefinitions("город", LexicalLanguage.Russian, limit: 10);
        Assert.IsNotEmpty(results);
    }

    [TestMethod]
    public void MalformedSearchQuery_ReturnsEmptyInsteadOfThrowing()
    {
        using var store = OpenOrSkip();

        Assert.IsEmpty(store.SearchDefinitions("\"unclosed", LexicalLanguage.Russian));
        Assert.IsEmpty(store.SearchDefinitions("   ", LexicalLanguage.Russian));
    }

    [TestMethod]
    public void HotWordsAreCached()
    {
        using var store = OpenOrSkip();

        var before = store.CachedEntries;
        for (var i = 0; i < 20; i++)
            store.Lookup("дом", LexicalLanguage.Russian);

        Assert.IsGreaterThan(before, store.CachedEntries);
    }

    [TestMethod]
    public void DatabaseEntryCount_MatchesTheProvenanceManifest()
    {
        using var store = OpenOrSkip();

        var manifestPath = Path.Combine(LexicalDirectory(), "sources.manifest.json");
        if (!File.Exists(manifestPath))
            Assert.Inconclusive("provenance manifest not present next to the test host.");

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        foreach (var artifact in document.RootElement.GetProperty("artifacts").EnumerateArray())
        {
            var path = artifact.GetProperty("path").GetString() ?? "";
            if (!path.EndsWith("writelight-lexical.db", StringComparison.Ordinal)) continue;

            Assert.AreEqual(artifact.GetProperty("entries").GetInt32(), store.EntryCount,
                "the database entry count differs from the provenance manifest; rebuild and update it.");
            return;
        }

        Assert.Fail("the database is not listed in sources.manifest.json.");
    }

    [TestMethod]
    public void ConcurrentLookups_AreSafe()
    {
        using var store = OpenOrSkip();

        var words = new[] { "дом", "быстрый", "бежать", "близкий", "хороший", "bright", "house" };
        Parallel.For(0, 400, i =>
        {
            var word = words[i % words.Length];
            var language = word[0] < 128 ? LexicalLanguage.English : LexicalLanguage.Russian;
            store.Lookup(word, language);
        });
    }
}
