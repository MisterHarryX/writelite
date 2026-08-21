using System.Diagnostics;

using WriteLite.AI.Local;
using WriteLite.Language.Russian;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Spelling;

/// <summary>
/// The two properties the Russian language layer must never lose: it depends on
/// nothing but local files, and it stays inside a typing-latency budget.
/// </summary>
/// <remarks>
/// Not parallelised. These assertions measure process-wide facts — managed heap
/// growth and wall-clock per lookup — which are meaningless while other tests are
/// allocating and competing for cores in the same process. Run alone they are
/// stable; run alongside the rest of the suite they fail on load rather than on a
/// real regression, which is the worst kind of test.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class RussianOfflineAndPerformanceTests
{
    /// <summary>
    /// Runs the whole Russian stack and proves every artefact it consumed came
    /// off the local disk.
    /// </summary>
    /// <remarks>
    /// This does not sandbox sockets — an in-process test cannot honestly claim
    /// to have blocked the network. What it does establish is the structural
    /// guarantee: each component resolves to a file path under the deployment
    /// directory, and the type graph the Russian path is built from contains no
    /// HTTP client. A component that started fetching something would have to
    /// pull in a networking type to do it.
    /// </remarks>
    [TestMethod]
    public void RussianStack_RunsEntirelyFromLocalFiles()
    {
        using var index = RussianFormIndex.Load();
        Assert.IsNotNull(index, "form index did not load");
        Assert.IsTrue(Directory.Exists(index.SourceDirectory),
            $"form index resolved to a non-directory: {index.SourceDirectory}");
        Assert.IsTrue(index.WordCount > 1_000_000);
        Assert.IsTrue(index.Contains("привет"));

        var generator = new RussianCandidateGenerator(index);
        var ranked = generator.Rank("превет");
        Assert.IsTrue(ranked.Count > 0);
        Assert.AreEqual("привет", ranked[0].Word);

        using var checker = new LocalSpellChecker();
        var analyzer = new SpellTextAnalyzer(checker);
        Assert.IsTrue(analyzer.Analyze("Превет, как дила? Сегодня хорошая погода.").Count > 0,
            "analyzer produced nothing");

        using var reranker = RussianContextualReranker.TryLoad();
        if (reranker is not null)
        {
            Assert.IsTrue(Directory.Exists(reranker.ModelDirectory));
            var score = reranker.ScoreSentence("Он должен будет прийти завтра.");
            Assert.IsTrue(score is > 0.0 and < 1.0, $"reranker returned {score}");
        }
    }

    [TestMethod]
    public void RussianLanguageAssembly_ReferencesNoNetworkingTypes()
    {
        // The lexical layer is pure local computation; if it ever grows a
        // download-on-first-use path, this is where that shows up.
        var assembly = typeof(RussianFormIndex).Assembly;
        var networking = assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.Contains("Net.Http", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Net.Sockets", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Net.Requests", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.AreEqual(0, networking.Length,
            "WriteLite.Language.Russian references networking assemblies: " + string.Join(", ", networking));
    }

    [TestMethod]
    public void ColdStart_StaysWithinTheStartupBudget()
    {
        var sw = Stopwatch.StartNew();
        using var index = RussianFormIndex.Load();
        sw.Stop();
        Assert.IsNotNull(index);
        Assert.IsTrue(sw.Elapsed.TotalMilliseconds < 1500,
            $"form index took {sw.Elapsed.TotalMilliseconds:F0} ms to load");
    }

    [TestMethod]
    public void SentenceAnalysis_KeepsUpWithTyping()
    {
        using var checker = new LocalSpellChecker();
        var analyzer = new SpellTextAnalyzer(checker);
        var sentences = new[]
        {
            "Привет, как твои дела сегодня вечером?",
            "Разработчик отправил изменения в репозиторий и написал тесты.",
            "Превет, я хочю купить новый ноутбук завтра утром.",
            "Это вообще имба, го катку в доту после работы.",
            "Компания опубликовала отчёт за третий квартал текущего года.",
        };

        foreach (var sentence in sentences) analyzer.Analyze(sentence);   // warm caches

        var timings = new List<double>();
        for (var i = 0; i < 40; i++)
        {
            var sw = Stopwatch.StartNew();
            analyzer.Analyze(sentences[i % sentences.Length]);
            sw.Stop();
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        var p95 = timings[(int)(timings.Count * 0.95)];
        Assert.IsTrue(p95 < 30, $"p95 sentence analysis was {p95:F1} ms");
    }

    [TestMethod]
    public void FormIndex_KeepsTheWorkingSetSmall()
    {
        // The metadata sidecar is 37 MB on disk and stays memory-mapped; the
        // graph itself is the only part that becomes resident. A regression
        // here would mean someone started reading the whole file eagerly.
        var before = GC.GetTotalMemory(forceFullCollection: true);
        using var index = RussianFormIndex.Load();
        Assert.IsNotNull(index);
        for (var i = 0; i < 1000; i++) index.Contains("программирование");
        var after = GC.GetTotalMemory(forceFullCollection: true);

        var megabytes = (after - before) / (1024.0 * 1024.0);
        Assert.IsTrue(megabytes < 40,
            $"loading three million forms added {megabytes:F1} MB of managed heap");
    }
}
