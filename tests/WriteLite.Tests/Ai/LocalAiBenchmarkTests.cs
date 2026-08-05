using System.Diagnostics;
using WriteLite.AI.Contracts;
using WriteLite.AI.Local;

namespace WriteLite.Tests.Ai;

[TestClass]
public sealed class LocalAiBenchmarkTests
{
    [TestMethod]
    public async Task Lite_Latency_P95_UnderBudget_ForShortTexts()
    {
        await using var analyzer = new LocalAiTextAnalyzer();
        await analyzer.WarmupAsync();

        var samples = new[]
        {
            "привет как дела",
            "я сегодня небыл в школе",
            "Мы используем WriteLite 2.0 локально",
            "Когда я пришел домой мама уже приготовила ужин",
            "Открой https://example.com/test?id=1 в браузере"
        };

        var times = new List<double>();
        foreach (var text in samples)
        {
            for (var i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                _ = await analyzer.AnalyzeAsync(new AiTextAnalysisRequest(
                    text, null, AiAnalysisMode.Correct, 1, AiModelProfile.Lite));
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
        }

        times.Sort();
        var p95 = times[(int)(times.Count * 0.95) - 1];
        // Local Lite should be well under 200 ms on modern CPUs for short text.
        Assert.IsLessThan(500, p95, $"p95={p95:F1}ms times={string.Join(',', times.Select(t => t.ToString("F1")))}");
    }
}
