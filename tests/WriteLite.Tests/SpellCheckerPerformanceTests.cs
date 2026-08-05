using System.Diagnostics;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests;

[TestClass]
public sealed class SpellCheckerPerformanceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ShortSentenceCheckStaysFast()
    {
        var loadStopwatch = Stopwatch.StartNew();
        var spellChecker = new LocalSpellChecker();
        loadStopwatch.Stop();

        var analyzer = new SpellTextAnalyzer(spellChecker);
        const string sentence = "пливет Пливет helllo привте";

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
}
