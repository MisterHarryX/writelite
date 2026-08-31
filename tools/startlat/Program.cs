using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using WriteLite.Services.Rules;

namespace StartLat;

/// <summary>
/// Times the pieces of WriteLite's cold start that do measurable work before the
/// first window, so a claim about startup cost is a measurement rather than a guess.
/// </summary>
/// <remarks>
/// Each stage is run once cold and then repeated, because most of them are dominated
/// by one-time costs — JIT of the regex engine, the file cache warming — that a single
/// sample cannot separate from the steady-state cost. Both numbers are reported: the
/// cold one is what a user experiences on the first launch after a reboot, the warm one
/// is what a second launch costs.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        var repeats = 3;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--repeats" && int.TryParse(args[i + 1], out var parsed))
            {
                repeats = Math.Max(1, parsed);
            }
        }

        Console.WriteLine($"startlat · repeats={repeats} · {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();

        // The shipping path: cached, structural validation only.
        Measure("RuleCatalog.LoadDefault", repeats, () => _ = RuleCatalog.LoadDefault());

        // What every call used to cost, and what a validating load still costs: an
        // uncached build with all 265 embedded test cases executed.
        var root = Path.Combine(AppContext.BaseDirectory, "resources", "rules", "ru");
        Measure("RuleCatalog validating load", repeats, () => _ = RuleCatalog.LoadFromDirectory(root));

        // The same build without the embedded tests, which isolates how much of it is
        // pattern compilation rather than validation.
        Measure("RuleCatalog uncached, no tests", repeats,
            () => _ = RuleCatalog.LoadFromDirectory(root, validateEmbeddedTests: false));

        Console.WriteLine();
        MeasureFirstAnalysis();

        return 0;
    }

    /// <summary>
    /// What the first check costs once the catalog no longer runs its own tests at load.
    /// </summary>
    /// <remarks>
    /// Skipping the embedded tests also stops them being the thing that first exercises each
    /// <see cref="RegexOptions.Compiled"/> pattern, and a compiled pattern emits its IL on
    /// first use. So the question this answers is whether the startup saving is real or
    /// merely moved onto the first sentence the user types. Reported as first-call versus
    /// steady-state on the same analyzer: the gap between them is the deferred cost.
    /// </remarks>
    private static void MeasureFirstAnalysis()
    {
        const string sample =
            "Мы обсудили этот вопрос вчера, и он согласился что нужно продолжать работу " +
            "несмотря на то что сроки уже прошли.";

        var catalog = RuleCatalog.LoadDefault();
        var warm = Stopwatch.StartNew();
        catalog.Warm();
        warm.Stop();
        Console.WriteLine($"{"RuleCatalog.Warm",-32} {warm.Elapsed.TotalMilliseconds,8:F1} ms");

        var analyzer = new WriteLite.Services.RuleBasedAnalyzer(catalog);

        var first = Stopwatch.StartNew();
        _ = analyzer.Analyze(sample);
        first.Stop();

        var steady = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var sw = Stopwatch.StartNew();
            _ = analyzer.Analyze(sample);
            sw.Stop();
            steady.Add(sw.Elapsed.TotalMilliseconds);
        }

        steady.Sort();
        Console.WriteLine(
            $"{"RuleBasedAnalyzer.Analyze",-32} first={first.Elapsed.TotalMilliseconds,8:F1} ms   " +
            $"steady median={steady[steady.Count / 2],8:F1} ms   min={steady[0],8:F1} ms");
    }

    private static void Measure(string name, int repeats, Action action)
    {
        var cold = Stopwatch.StartNew();
        action();
        cold.Stop();

        var warm = new List<double>();
        for (var i = 0; i < repeats; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            warm.Add(sw.Elapsed.TotalMilliseconds);
        }

        warm.Sort();
        Console.WriteLine(
            $"{name,-32} cold={cold.Elapsed.TotalMilliseconds,8:F1} ms   " +
            $"warm median={warm[warm.Count / 2],8:F1} ms   min={warm[0],8:F1} ms");
    }
}
