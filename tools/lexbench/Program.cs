using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using WriteLite.Services.Lexical;

namespace LexBench;

/// <summary>
/// Measures what the lexical layer actually costs at runtime, so the choice of
/// storage is made from numbers rather than assumption.
///
/// Reports, for the JSON path currently shipped:
///   * cold load time (this process has never touched the file)
///   * managed heap and process working set after load
///   * lookup latency over a sample of real lemmas (hit and miss)
///
/// Run one measurement per process; the caller repeats the process to get a cold
/// figure that is not polluted by a warm OS file cache or an already-built index.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var mode = args.Length > 0 ? args[0] : "json";
        var directory = args.Length > 1
            ? args[1]
            : Path.Combine(AppContext.BaseDirectory, "resources", "lexical");

        return mode switch
        {
            "json" => RunJson(directory),
            "sqlite" => RunSqlite(directory),
            _ => Fail($"unknown mode '{mode}'")
        };
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static int RunJson(string directory)
    {
        if (!Directory.Exists(directory))
            return Fail($"lexical directory not found: {directory}");

        var files = new DirectoryInfo(directory)
            .GetFiles("*.json")
            .Sum(file => file.Length);

        // Baseline memory before anything is loaded.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var managedBefore = GC.GetTotalMemory(true);
        var workingSetBefore = Process.GetCurrentProcess().WorkingSet64;

        var service = new OfflineLexicalKnowledgeService();
        var stopwatch = Stopwatch.StartNew();
        service.LoadFromDirectory(directory);
        stopwatch.Stop();
        var loadMs = stopwatch.Elapsed.TotalMilliseconds;

        if (!service.IsPackLoaded)
            return Fail($"pack failed to load: {service.LoadError}");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var managedAfter = GC.GetTotalMemory(true);
        var workingSetAfter = Process.GetCurrentProcess().WorkingSet64;

        var (hitWords, missWords) = SampleWords(directory);

        var report = new Dictionary<string, object?>
        {
            ["mode"] = "json",
            ["packVersion"] = service.PackVersion,
            ["databaseBacked"] = service.IsDatabaseBacked,
            ["englishPackLoaded"] = service.IsEnglishPackLoaded,
            ["jsonBytesOnDisk"] = files,
            ["loadMs"] = Math.Round(loadMs, 1),
            ["managedHeapBytes"] = managedAfter - managedBefore,
            ["workingSetBytes"] = workingSetAfter - workingSetBefore,
            ["workingSetTotalBytes"] = workingSetAfter,
            ["lookupHit"] = MeasureLookups(service, hitWords),
            ["lookupMiss"] = MeasureLookups(service, missWords)
        };

        Console.WriteLine(JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static int RunSqlite(string directory)
    {
        var path = Path.Combine(directory, "writelight-lexical.db");
        if (!File.Exists(path))
            return Fail($"lexical database not found: {path}");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var managedBefore = GC.GetTotalMemory(true);
        var workingSetBefore = Process.GetCurrentProcess().WorkingSet64;

        var stopwatch = Stopwatch.StartNew();
        using var store = SqliteLexicalStore.TryOpen(path);
        if (store is null) return Fail($"lexical database could not be opened: {path}");
        // Equivalent of "the pack is ready to answer": one real query.
        store.Lookup("дом", LexicalLanguage.Russian);
        stopwatch.Stop();
        var loadMs = stopwatch.Elapsed.TotalMilliseconds;

        var (hitWords, missWords) = SampleWords(directory);

        var hit = MeasureStore(store, hitWords, full: true);
        var miss = MeasureStore(store, missWords, full: true);
        var resolveOnly = MeasureStoreResolve(store, hitWords);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var managedAfter = GC.GetTotalMemory(true);
        var workingSetAfter = Process.GetCurrentProcess().WorkingSet64;

        var ftsWatch = Stopwatch.StartNew();
        var ftsHits = store.SearchDefinitions("город", LexicalLanguage.Russian).Count;
        ftsWatch.Stop();

        var report = new Dictionary<string, object?>
        {
            ["mode"] = "sqlite",
            ["dbBytesOnDisk"] = new FileInfo(path).Length,
            ["loadMs"] = Math.Round(loadMs, 1),
            ["managedHeapBytes"] = managedAfter - managedBefore,
            ["workingSetBytes"] = workingSetAfter - workingSetBefore,
            ["workingSetTotalBytes"] = workingSetAfter,
            ["cachedEntries"] = store.CachedEntries,
            ["lookupHit"] = hit,
            ["lookupMiss"] = miss,
            ["resolveFormOnly"] = resolveOnly,
            ["ftsSearchMs"] = Math.Round(ftsWatch.Elapsed.TotalMilliseconds, 2),
            ["ftsHits"] = ftsHits
        };

        Console.WriteLine(JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static Dictionary<string, object> MeasureStore(
        SqliteLexicalStore store, string[] words, bool full)
    {
        for (var i = 0; i < Math.Min(200, words.Length); i++)
            store.Lookup(words[i], LexicalLanguage.Russian);

        var timings = new double[words.Length];
        var found = 0;
        var stopwatch = new Stopwatch();
        for (var i = 0; i < words.Length; i++)
        {
            stopwatch.Restart();
            var entry = store.Lookup(words[i], LexicalLanguage.Russian);
            stopwatch.Stop();
            timings[i] = stopwatch.Elapsed.TotalMilliseconds * 1000.0;
            if (entry is not null) found++;
        }

        Array.Sort(timings);
        return new Dictionary<string, object>
        {
            ["count"] = words.Length,
            ["found"] = found,
            ["medianUs"] = Math.Round(timings[timings.Length / 2], 3),
            ["p95Us"] = Math.Round(timings[(int)(timings.Length * 0.95)], 3),
            ["maxUs"] = Math.Round(timings[^1], 3)
        };
    }

    private static Dictionary<string, object> MeasureStoreResolve(
        SqliteLexicalStore store, string[] words)
    {
        for (var i = 0; i < Math.Min(200, words.Length); i++)
            store.ResolveForm(words[i], LexicalLanguage.Russian);

        var timings = new double[words.Length];
        var stopwatch = new Stopwatch();
        for (var i = 0; i < words.Length; i++)
        {
            stopwatch.Restart();
            store.ResolveForm(words[i], LexicalLanguage.Russian);
            stopwatch.Stop();
            timings[i] = stopwatch.Elapsed.TotalMilliseconds * 1000.0;
        }

        Array.Sort(timings);
        return new Dictionary<string, object>
        {
            ["count"] = words.Length,
            ["medianUs"] = Math.Round(timings[timings.Length / 2], 3),
            ["p95Us"] = Math.Round(timings[(int)(timings.Length * 0.95)], 3)
        };
    }

    /// <summary>Real lemmas from the pack, plus words guaranteed to be absent.</summary>
    private static (string[] Hits, string[] Misses) SampleWords(string directory)
    {
        var path = Path.Combine(directory, "writelight-lexical-open.json");
        if (!File.Exists(path))
            path = Path.Combine(directory, "writelight-lexical-core.json");

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var entries = document.RootElement.GetProperty("entries");

        var random = new Random(20260808);
        var hits = new List<string>();
        var total = entries.GetArrayLength();
        for (var i = 0; i < 2000; i++)
        {
            var entry = entries[random.Next(total)];
            hits.Add(entry.GetProperty("lemma").GetString()!);
        }

        var misses = Enumerable.Range(0, 2000)
            .Select(i => $"яжюъщэё{i}")
            .ToArray();

        return (hits.ToArray(), misses);
    }

    private static Dictionary<string, object> MeasureLookups(
        OfflineLexicalKnowledgeService service, string[] words)
    {
        // Warm up so the figure reflects steady state, not first-call JIT.
        for (var i = 0; i < Math.Min(200, words.Length); i++)
            service.LookupEntry(words[i], LexicalLanguage.Russian);

        var timings = new double[words.Length];
        var found = 0;
        var stopwatch = new Stopwatch();
        for (var i = 0; i < words.Length; i++)
        {
            stopwatch.Restart();
            var entry = service.LookupEntry(words[i], LexicalLanguage.Russian);
            stopwatch.Stop();
            timings[i] = stopwatch.Elapsed.TotalMilliseconds * 1000.0; // microseconds
            if (entry is not null) found++;
        }

        Array.Sort(timings);
        return new Dictionary<string, object>
        {
            ["count"] = words.Length,
            ["found"] = found,
            ["medianUs"] = Math.Round(timings[timings.Length / 2], 3),
            ["p95Us"] = Math.Round(timings[(int)(timings.Length * 0.95)], 3),
            ["maxUs"] = Math.Round(timings[^1], 3)
        };
    }
}
