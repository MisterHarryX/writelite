using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WriteLite.Services.Spelling;

namespace TrainExport;

/// <summary>
/// Builds the labelled datasets a future training run would need, from corpora that are not
/// the golden benchmark.
/// </summary>
/// <remarks>
/// <para><b>This trains nothing.</b> It is the §25 readiness step: the point is to find out
/// whether the data for each candidate task can actually be produced, and at what size and
/// quality, before anyone commits to a training run. Two of the answers are already
/// interesting — see the candidate-generation failure rate in the manifest.</para>
///
/// <para><b>Sources.</b> <c>ai/data/errors/rerank_{train,validation}.jsonl</c>, built from
/// Russian Wiktionary sentences and the project's own chat corpus, plus punctuation
/// decisions derived from the correct half of the same pairs. The golden benchmark is not a
/// source and cannot become one: every example passes through <see cref="GoldenGuard"/>
/// first, and the manifest records how many were blocked.</para>
///
/// <para><b>Candidate lists are real.</b> The candidate_ranking task uses the product's own
/// generator rather than a synthetic distractor set, so the exported label space is the one
/// a deployed ranker would face — including the cases where the correct answer is absent,
/// which are exported with <c>candidate_generation_failure</c> set rather than dropped.
/// Dropping them would train a ranker on an easier problem than it will meet.</para>
///
///   trainexport [--out dir] [--limit n]
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var root = FindRepoRoot();
        var outputDirectory = Path.Combine(root, "ai", "data", "training");
        var limit = int.MaxValue;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length: outputDirectory = Path.GetFullPath(args[++i]); break;
                case "--limit" when i + 1 < args.Length: limit = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default:
                    Console.Error.WriteLine("usage: trainexport [--out dir] [--limit n]");
                    return 2;
            }
        }

        var goldenPath = Path.Combine(root, "ai", "data", "benchmark", "ru_frozen_v1.jsonl");
        if (!File.Exists(goldenPath))
        {
            Console.Error.WriteLine($"golden corpus not found, refusing to export without a guard: {goldenPath}");
            return 1;
        }

        var guard = GoldenGuard.FromCorpus(goldenPath);
        Console.WriteLine($"golden guard : {guard.GoldenItemCount} ids, {guard.GoldenSentenceCount} sentence fingerprints");

        var lexicon = RussianFormIndexLexicon.TryLoad();
        if (lexicon is null)
        {
            Console.Error.WriteLine("Russian form index is not deployed; candidate lists cannot be built.");
            return 1;
        }

        Directory.CreateDirectory(outputDirectory);

        var ranking = new List<CandidateRankingExample>();
        var punctuation = new List<PunctuationDecisionExample>();

        // Three splits, not two. The first export stopped at validation, which left the golden
        // set as the only held-out data — and the golden set is the acceptance gate, so using
        // it to pick a model or a threshold would tune against the thing that is supposed to
        // judge the tuning. The error corpus has always carried a test split; it was simply
        // not being read.
        foreach (var split in new[] { "train", "validation", "test" })
        {
            var source = Path.Combine(root, "ai", "data", "errors", $"rerank_{split}.jsonl");
            if (!File.Exists(source))
            {
                Console.Error.WriteLine($"missing source corpus: {source}");
                continue;
            }

            var pairs = ErrorCorpus.LoadPairs(source);
            Console.WriteLine($"{split,-11}: {pairs.Count} pairs");

            foreach (var pair in pairs.Take(limit))
            {
                if (guard.IsGolden(pair.Correct) || guard.IsGolden(pair.Broken))
                {
                    continue;
                }

                var example = CandidateRankingBuilder.TryBuild(pair, split, lexicon);
                if (example is not null)
                {
                    ranking.Add(example);
                }

                punctuation.AddRange(PunctuationDecisionBuilder.Build(pair, split));
            }
        }

        lexicon.Dispose();

        Write(Path.Combine(outputDirectory, "candidate_ranking.jsonl"), ranking);
        Write(Path.Combine(outputDirectory, "punctuation_decision.jsonl"), punctuation);

        var manifest = Manifest.Build(guard, ranking, punctuation);
        File.WriteAllText(
            Path.Combine(outputDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions) + "\n",
            new UTF8Encoding(false));

        Console.WriteLine();
        Console.WriteLine($"candidate_ranking      : {ranking.Count} examples "
                          + $"({manifest.CandidateGenerationFailures} with the answer absent from the candidate list, "
                          + $"{manifest.CandidateGenerationFailureRate:P1})");
        Console.WriteLine($"punctuation_decision   : {punctuation.Count} examples "
                          + $"({manifest.PunctuationCommaExamples} COMMA / {manifest.PunctuationNoChangeExamples} NO_CHANGE)");
        Console.WriteLine($"blocked as golden      : {guard.BlockedCount}");
        Console.WriteLine($"output                 : {outputDirectory}");
        return 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static void Write<T>(string path, IReadOnlyList<T> examples)
    {
        // LF and binary mode, matching the benchmark generator's policy: a dataset whose
        // hash changes on checkout cannot be pinned.
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.NewLine = "\n";
        foreach (var example in examples)
        {
            writer.WriteLine(JsonSerializer.Serialize(example, LineOptions));
        }
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
