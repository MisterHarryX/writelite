using System.Diagnostics;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests;

[TestClass]
public sealed class SpellCheckerPerformanceTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string Sentence = "пливет Пливет helllo привте";

    /// <summary>
    /// The lexical layer — the one that runs against every keystroke burst.
    /// </summary>
    /// <remarks>
    /// Contextual refinement is switched off here deliberately, and this is a change of
    /// scope rather than a relaxed budget. Since the reranker was wired in, a bare
    /// <see cref="SpellTextAnalyzer"/> runs an ONNX acceptability model over every
    /// candidate list, which took this sentence from ~8 ms to ~30 ms. That cost is real
    /// and is now guarded separately by <see cref="ContextualPassStaysWithinItsBudget"/>.
    ///
    /// What this test is for is the path that must stay fast: <c>App.OnStartup</c> and
    /// <c>EditorPage</c> both construct their as-you-type analyzers with refinement off,
    /// so this measures what typing actually costs.
    /// </remarks>
    [TestMethod]
    public async Task ShortSentenceCheckStaysFast()
    {
        var loadStopwatch = Stopwatch.StartNew();
        var spellChecker = new LocalSpellChecker();
        loadStopwatch.Stop();

        var analyzer = new SpellTextAnalyzer(spellChecker) { ContextualRefinementEnabled = false };
        const string sentence = Sentence;

        for (var i = 0; i < 10; i++)
        {
            await analyzer.AnalyzeAsync(sentence);
        }

        var checkStopwatch = Stopwatch.StartNew();
        const int iterations = 100;

        for (var i = 0; i < iterations; i++)
        {
            await analyzer.AnalyzeAsync(sentence);
        }

        checkStopwatch.Stop();

        var averageMs = checkStopwatch.Elapsed.TotalMilliseconds / iterations;
        TestContext.WriteLine($"dictionary-load-ms={loadStopwatch.Elapsed.TotalMilliseconds:F3}");
        TestContext.WriteLine($"short-sentence-average-ms={averageMs:F3}");
        TestContext.WriteLine($"ru-words={spellChecker.Stats.RussianWordCount}");
        TestContext.WriteLine($"en-words={spellChecker.Stats.EnglishWordCount}");

        Assert.IsLessThan(25, averageMs, $"Short sentence check took {averageMs:F3} ms on average.");
    }

    /// <summary>
    /// The contextual layer, which is slower on purpose and must stay bounded.
    /// </summary>
    /// <remarks>
    /// This is the pathological case for it: four unknown words in one short sentence, so
    /// the reranker is asked to score a candidate list for every one of them. Real prose
    /// has far fewer, and the benchmark's p50 over 841 mixed sentences is 5.8 ms.
    ///
    /// The budget exists so that wiring a neural model into the analysis path cannot drift
    /// into something that stalls the orchestrated pass. It runs behind a debounce, off the
    /// UI thread, so tens of milliseconds are affordable — hundreds are not.
    /// </remarks>
    [TestMethod]
    public async Task ContextualPassStaysWithinItsBudget()
    {
        using var spellChecker = new LocalSpellChecker();
        var analyzer = new SpellTextAnalyzer(spellChecker) { ContextualRefinementEnabled = true };

        if (!analyzer.ContextualRefinementEnabled)
        {
            Assert.Inconclusive("reranker not deployed next to the test binaries");
            return;
        }

        for (var i = 0; i < 5; i++)
        {
            await analyzer.AnalyzeAsync(Sentence);
        }

        var stopwatch = Stopwatch.StartNew();
        const int iterations = 40;
        for (var i = 0; i < iterations; i++)
        {
            await analyzer.AnalyzeAsync(Sentence);
        }

        stopwatch.Stop();
        var averageMs = stopwatch.Elapsed.TotalMilliseconds / iterations;
        TestContext.WriteLine($"contextual-short-sentence-average-ms={averageMs:F3}");

        Assert.IsLessThan(
            120,
            averageMs,
            $"Contextual pass took {averageMs:F3} ms on a four-error sentence. It runs behind "
            + "a debounce and off the UI thread, but this is the budget beyond which the "
            + "orchestrated pass stops feeling immediate.");
    }
}
