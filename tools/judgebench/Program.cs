using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WriteLite.AI.Contracts;
using WriteLite.AI.Local;
using WriteLite.Services.Spelling;

namespace JudgeBench;

/// <summary>
/// The §22 candidate-judge experiment: does asking the local model to <em>choose</em> beat
/// asking the deterministic ranker to choose?
/// </summary>
/// <remarks>
/// <para>Deliberately separate from <c>langbench</c>. langbench measures the shipping
/// pipeline; the judge is not in it and must not be, until there is a number saying it
/// should be. Mixing the two would make it impossible to say what the product does today.</para>
///
/// <para>Three selectors are compared on identical candidate lists, which is what makes the
/// comparison mean anything — each one is handed exactly what the deterministic generator
/// produced, and only the choosing differs:</para>
/// <list type="number">
/// <item><b>deterministic</b> — the existing lexical score, top candidate.</item>
/// <item><b>reranker</b> — the shipped 29 M contextual model reordering that list.</item>
/// <item><b>judge</b> — WriteLite-Qwen picking one id, NO_CHANGE always available.</item>
/// </list>
///
/// <para>Two accuracies are reported separately, because a selector that is good at one and
/// bad at the other is useless: <b>selection accuracy</b> over items that have a gold error,
/// and <b>NO_CHANGE accuracy</b> over items that must not be touched at all. A judge biased
/// toward changing something scores well on the first and destroys the second.</para>
///
/// <para><b>Candidate-generation failures are counted, never hidden.</b> When the gold answer
/// is not on the menu, no selector can win, and the run reports that separately so a
/// selector's accuracy is not confused with the generator's coverage (§24).</para>
///
///   judgebench [--corpus path] [--out path] [--limit n] [--no-ai]
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var root = FindRepoRoot();

        var corpusPath = Path.Combine(root, "ai", "data", "benchmark", "ru_frozen_v1.jsonl");
        var outputPath = Path.Combine(root, "benchmarks", "results", "P5-judge.json");
        var limit = int.MaxValue;
        var useAi = true;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--corpus" when i + 1 < args.Length: corpusPath = Path.GetFullPath(args[++i]); break;
                case "--out" when i + 1 < args.Length: outputPath = Path.GetFullPath(args[++i]); break;
                case "--limit" when i + 1 < args.Length: limit = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--no-ai": useAi = false; break;
                default:
                    Console.Error.WriteLine("usage: judgebench [--corpus path] [--out path] [--limit n] [--no-ai]");
                    return 2;
            }
        }

        if (!File.Exists(corpusPath))
        {
            Console.Error.WriteLine($"benchmark not found: {corpusPath}");
            return 1;
        }

        var items = Corpus.Load(corpusPath);
        Console.WriteLine($"corpus : {corpusPath} ({items.Count} items)");

        using var spellChecker = new LocalSpellChecker();
        using var refiner = ContextualCorrectionRefiner.TryLoad();
        var lexicon = RussianFormIndexLexicon.TryLoad();
        if (lexicon is null)
        {
            Console.Error.WriteLine("Russian form index is not deployed; nothing to rank.");
            return 1;
        }

        await using var backend = new QwenModelBackend();
        CandidateJudge? judge = null;
        if (useAi)
        {
            await backend.EnsureReadyAsync();
            judge = backend.IsAvailable ? new CandidateJudge(backend) : null;
            Console.WriteLine($"judge  : {(judge is null ? "unavailable" : backend.ActiveEndpoint)}");
        }
        else
        {
            Console.WriteLine("judge  : disabled (--no-ai)");
        }

        Console.WriteLine($"rerank : {(refiner is null ? "unavailable" : refiner.ModelVersion)}");
        Console.WriteLine();

        var report = await Evaluate.RunAsync(items, limit, lexicon, refiner, judge);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }),
            new UTF8Encoding(false));

        Print(report, outputPath);
        lexicon.Dispose();
        return 0;
    }

    private static void Print(JudgeReport report, string outputPath)
    {
        Console.WriteLine($"candidate sets scored     : {report.ScoredTargets}");
        Console.WriteLine($"gold answer not generated : {report.CandidateGenerationFailures} "
                          + $"({report.CandidateGenerationFailureRate:P1}) — unreachable by any selector");
        Console.WriteLine($"reachable targets         : {report.ReachableTargets}");
        Console.WriteLine($"no-change targets         : {report.NoChangeTargets}");
        Console.WriteLine();
        Console.WriteLine($"{"selector",-16} {"select",8} {"noChange",9} {"calls",7} {"p50 ms",9} {"p95 ms",9} {"rejected",9}");
        foreach (var (name, s) in report.Selectors)
        {
            Console.WriteLine(
                $"{name,-16} {s.SelectionAccuracy,8:F3} {s.NoChangeAccuracy,9:F3} "
                + $"{s.ModelCalls,7} {s.LatencyP50Ms,9:F1} {s.LatencyP95Ms,9:F1} {s.RejectedAnswers,9}");
        }

        if (report.RejectionReasons.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("judge contract violations:");
            foreach (var (reason, count) in report.RejectionReasons.OrderByDescending(x => x.Value))
            {
                Console.WriteLine($"  {count,5}  {reason}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"report : {outputPath}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WriteLite.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
