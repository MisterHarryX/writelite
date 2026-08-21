using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WriteLite.AI.Local;
using WriteLite.Language.Russian;
using WriteLite.Harness;
using WriteLite.Services.Spelling;

namespace RerankDiag;

/// <summary>
/// Explains why the shipped 29 M contextual reranker is <em>worse</em> than lexical ranking
/// at choosing a candidate — 0.938 against 0.962 on identical lists.
/// </summary>
/// <remarks>
/// <para>The Phase 6 brief makes retraining this model conditional on understanding that
/// first, and it is right to: the reranker is not a model that fails to help, it is a model
/// that actively flips correct answers into wrong ones, and retraining a component whose
/// failure mode is unknown is the expensive way to discover it.</para>
///
/// <para>Every candidate list here comes from the product's own generator, so the comparison
/// is decision-against-decision. What the tool adds over <c>judgebench</c>'s two accuracy
/// numbers is the shape of the disagreements: which subclass they fall in, how big the score
/// margin was when the model overruled the lexical order, and whether the target was inside
/// the model's 64-token window at all.</para>
///
/// <para>Runs on the non-golden development corpus by default. The golden set is the
/// acceptance gate and cannot also be the thing a diagnosis iterates against.</para>
///
///   rerankdiag [--corpus path] [--split name] [--limit n] [--out path]
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var root = FindRepoRoot();
        var corpusPath = Path.Combine(root, "ai", "data", "training", "candidate_ranking.jsonl");
        var outputPath = Path.Combine(root, "benchmarks", "results", "P6-rerank-diagnosis.json");
        var split = "validation";
        var limit = int.MaxValue;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--corpus" when i + 1 < args.Length: corpusPath = Path.GetFullPath(args[++i]); break;
                case "--out" when i + 1 < args.Length: outputPath = Path.GetFullPath(args[++i]); break;
                case "--split" when i + 1 < args.Length: split = args[++i]; break;
                case "--limit" when i + 1 < args.Length: limit = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default:
                    Console.Error.WriteLine("usage: rerankdiag [--corpus path] [--split name] [--limit n] [--out path]");
                    return 2;
            }
        }

        using var lexicon = RussianFormIndexLexicon.TryLoad();
        using var reranker = RussianContextualReranker.TryLoad();
        if (lexicon is null || reranker is null)
        {
            Console.Error.WriteLine("form index or reranker is not deployed; nothing to diagnose.");
            return 1;
        }

        var examples = Load(corpusPath, split, limit);
        Console.WriteLine($"corpus   : {corpusPath} [{split}] — {examples.Count} examples");
        Console.WriteLine($"reranker : {reranker.ModelVersion}");
        Console.WriteLine();

        var report = Diagnose(examples, lexicon, reranker, corpusPath, split);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }),
            new UTF8Encoding(false));

        Print(report, outputPath);
        return 0;
    }

    private static Report Diagnose(
        IReadOnlyList<Example> examples,
        RussianFormIndexLexicon lexicon,
        RussianContextualReranker reranker,
        string corpusPath,
        string split)
    {
        var index = lexicon.Index;
        var perClass = new Dictionary<string, Cell>(StringComparer.Ordinal);
        var flips = new List<Flip>();
        var rescues = new List<Flip>();
        var flipMargins = new List<double>();
        var rescueMargins = new List<double>();

        int scored = 0, detWin = 0, rerWin = 0, bothRight = 0, bothWrong = 0;
        int flipped = 0, rescued = 0, truncated = 0, truncatedAndFlipped = 0;

        // Every decision, kept so accuracy can be recomputed at any margin without re-running
        // the model. The margin is the whole question: the shipping refiner already suppresses
        // the reranker below 0.05, so "is the reranker net-negative" is really "is there any
        // threshold at which its opinions are worth taking".
        var decisions = new List<(bool DeterministicRight, bool RerankerRight, double Margin)>();

        foreach (var example in examples)
        {
            // Only reachable targets are a decision question. An unreachable one is a
            // generation failure and scoring it here would blame the decider for it.
            if (example.CandidateGenerationFailure) continue;

            var candidates = example.Candidates
                .Where(c => !string.Equals(c, "NO_CHANGE", StringComparison.Ordinal))
                .ToList();
            if (candidates.Count < 2) continue;

            scored++;

            var deterministicChoice = candidates[0];
            var reranked = reranker.Rerank(example.Sentence, example.TargetStart, example.Target.Length, candidates);
            if (reranked.Count == 0) continue;

            var rerankerChoice = reranked[0].Word;
            var margin = reranked.Count > 1 ? reranked[0].Acceptability - reranked[1].Acceptability : 0;

            var deterministicRight = Matches(deterministicChoice, example.Correct);
            var rerankerRight = Matches(rerankerChoice, example.Correct);

            // Is the target even inside the model's window? A 64-token cap on a long sentence
            // means the substitution the model is being asked about never reaches it, and the
            // two variants it scores are then character-identical.
            var isTruncated = ApproximateTokenIndex(example.Sentence, example.TargetStart) >= reranker.MaxLength - 2;
            if (isTruncated) truncated++;

            decisions.Add((deterministicRight, rerankerRight, margin));

            var klass = Classify.Of(index, example.Target, example.Correct);
            var cell = Cell.For(perClass, klass);
            cell.Scored++;
            if (deterministicRight) cell.DeterministicCorrect++;
            if (rerankerRight) cell.RerankerCorrect++;

            if (deterministicRight && rerankerRight) bothRight++;
            else if (!deterministicRight && !rerankerRight) bothWrong++;
            else if (deterministicRight)
            {
                detWin++;
                flipped++;
                cell.Flips++;
                flipMargins.Add(margin);
                if (isTruncated) truncatedAndFlipped++;
                if (flips.Count < 60)
                {
                    flips.Add(new Flip(example.Id, klass, example.Target, example.Correct,
                        deterministicChoice, rerankerChoice, Math.Round(margin, 4), isTruncated,
                        Truncate(example.Sentence)));
                }
            }
            else
            {
                rerWin++;
                rescued++;
                cell.Rescues++;
                rescueMargins.Add(margin);
                if (rescues.Count < 60)
                {
                    rescues.Add(new Flip(example.Id, klass, example.Target, example.Correct,
                        deterministicChoice, rerankerChoice, Math.Round(margin, 4), isTruncated,
                        Truncate(example.Sentence)));
                }
            }
        }

        return new Report
        {
            RunUtc = DateTime.UtcNow.ToString("O"),
            Corpus = corpusPath,
            Split = split,
            RerankerVersion = reranker.ModelVersion,
            RerankerMaxLength = reranker.MaxLength,
            Scored = scored,
            DeterministicAccuracy = Rate(perClass.Values.Sum(c => c.DeterministicCorrect), scored),
            RerankerAccuracy = Rate(perClass.Values.Sum(c => c.RerankerCorrect), scored),
            BothRight = bothRight,
            BothWrong = bothWrong,
            RerankerFlippedCorrectToWrong = flipped,
            RerankerRescuedWrongToCorrect = rescued,
            NetEffect = rescued - flipped,
            TargetsBeyondModelWindow = truncated,
            TargetsBeyondWindowAndFlipped = truncatedAndFlipped,
            MeanFlipMargin = flipMargins.Count == 0 ? 0 : Math.Round(flipMargins.Average(), 4),
            MeanRescueMargin = rescueMargins.Count == 0 ? 0 : Math.Round(rescueMargins.Average(), 4),
            PerClass = perClass.OrderByDescending(kv => kv.Value.Scored)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            MarginSweep = SweepMargin(decisions),
            FlipExamples = flips,
            RescueExamples = rescues,
        };
    }

    /// <summary>
    /// Accuracy of "take the model's answer only when it wins by at least <c>m</c>, otherwise
    /// keep the lexical answer" — which is exactly what the shipping refiner does at 0.05.
    /// </summary>
    /// <remarks>
    /// The sweep is the go/no-go evidence in one table. If the curve rises monotonically
    /// toward the deterministic figure as the margin grows and never crosses it, then the
    /// model's opinions are not worth taking at any confidence, and its apparent near-parity
    /// in the shipping configuration is the guard doing the work rather than the model.
    /// </remarks>
    private static List<MarginRow> SweepMargin(
        List<(bool DeterministicRight, bool RerankerRight, double Margin)> decisions)
    {
        var rows = new List<MarginRow>();
        foreach (var margin in new[] { 0.0, 0.01, 0.02, 0.05, 0.10, 0.20, 0.30, 0.50, 1.01 })
        {
            var correct = 0;
            var consulted = 0;
            foreach (var (deterministicRight, rerankerRight, actual) in decisions)
            {
                var takeModel = actual >= margin;
                if (takeModel) consulted++;
                correct += (takeModel ? rerankerRight : deterministicRight) ? 1 : 0;
            }

            rows.Add(new MarginRow(
                margin,
                consulted,
                decisions.Count == 0 ? 0 : Math.Round((double)consulted / decisions.Count, 4),
                decisions.Count == 0 ? 0 : Math.Round((double)correct / decisions.Count, 4)));
        }

        return rows;
    }

    /// <summary>
    /// Roughly how many WordPiece tokens precede an offset.
    /// </summary>
    /// <remarks>
    /// Approximate on purpose — the exact answer would need the tokenizer, and the question
    /// being asked is "is this target near or past the window edge", which three characters
    /// per token answers well enough for Russian. Reported as an approximation in the field
    /// name so nobody quotes it as exact.
    /// </remarks>
    private static int ApproximateTokenIndex(string sentence, int offset)
        => Math.Min(offset, sentence.Length) / 3;

    private static string Truncate(string sentence)
        => sentence.Length <= 110 ? sentence : sentence[..110] + "…";

    private static bool Matches(string? candidate, string expected)
        => candidate is not null && string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase);

    private static double Rate(int hit, int total) => total == 0 ? 0 : Math.Round((double)hit / total, 4);

    private static IReadOnlyList<Example> Load(string path, string split, int limit)
    {
        var examples = new List<Example>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var example = JsonSerializer.Deserialize<Example>(line);
            if (example is null || example.Split != split) continue;
            examples.Add(example);
            if (examples.Count >= limit) break;
        }

        return examples;
    }

    private static void Print(Report r, string outputPath)
    {
        Console.WriteLine($"scored decisions          : {r.Scored}");
        Console.WriteLine($"deterministic accuracy    : {r.DeterministicAccuracy:F4}");
        Console.WriteLine($"reranker accuracy         : {r.RerankerAccuracy:F4}");
        Console.WriteLine();
        Console.WriteLine($"both right                : {r.BothRight}");
        Console.WriteLine($"both wrong                : {r.BothWrong}");
        Console.WriteLine($"reranker BROKE a correct  : {r.RerankerFlippedCorrectToWrong}  (mean winning margin {r.MeanFlipMargin:F4})");
        Console.WriteLine($"reranker FIXED a wrong    : {r.RerankerRescuedWrongToCorrect}  (mean winning margin {r.MeanRescueMargin:F4})");
        Console.WriteLine($"net effect                : {r.NetEffect:+#;-#;0} decisions");
        Console.WriteLine();
        Console.WriteLine($"targets past the {r.RerankerMaxLength}-token window : {r.TargetsBeyondModelWindow} (of which flipped: {r.TargetsBeyondWindowAndFlipped})");
        Console.WriteLine();
        Console.WriteLine($"{"class",-14} {"n",5} {"det",7} {"rerank",7} {"broke",6} {"fixed",6}");
        foreach (var (name, c) in r.PerClass)
        {
            Console.WriteLine($"{name,-14} {c.Scored,5} {c.DeterministicAccuracy,7:F3} {c.RerankerAccuracy,7:F3} {c.Flips,6} {c.Rescues,6}");
        }

        Console.WriteLine();
        Console.WriteLine("consult the model only when it wins by at least M, else keep lexical:");
        Console.WriteLine($"  {"M",6} {"consulted",10} {"share",7} {"accuracy",9}");
        foreach (var row in r.MarginSweep)
        {
            var note = Math.Abs(row.Margin - 0.05) < 1e-9 ? "  <- shipping RerankMargin"
                : row.Margin >= 1.0 ? "  <- lexical only"
                : row.Margin == 0.0 ? "  <- model only" : "";
            Console.WriteLine($"  {row.Margin,6:F2} {row.Consulted,10} {row.ConsultedShare,7:P1} {row.Accuracy,9:F4}{note}");
        }

        Console.WriteLine();
        Console.WriteLine("— a sample of decisions the reranker broke —");
        foreach (var f in r.FlipExamples.Take(12))
        {
            Console.WriteLine($"  [{f.Class}] '{f.Target}' should be '{f.Expected}': lexical said '{f.Deterministic}', model said '{f.Reranker}' (margin {f.Margin:F3})");
        }

        Console.WriteLine();
        Console.WriteLine($"report : {outputPath}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WriteLite.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}

internal sealed class Example
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("split")] public string Split { get; set; } = "";
    [JsonPropertyName("sentence")] public string Sentence { get; set; } = "";
    [JsonPropertyName("target")] public string Target { get; set; } = "";
    [JsonPropertyName("target_start")] public int TargetStart { get; set; }
    [JsonPropertyName("candidates")] public List<string> Candidates { get; set; } = [];
    [JsonPropertyName("correct")] public string Correct { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("error_type")] public string ErrorType { get; set; } = "";
    [JsonPropertyName("candidate_generation_failure")] public bool CandidateGenerationFailure { get; set; }
}

internal sealed class Cell
{
    public int Scored { get; set; }
    public int DeterministicCorrect { get; set; }
    public int RerankerCorrect { get; set; }
    public int Flips { get; set; }
    public int Rescues { get; set; }

    public double DeterministicAccuracy => Scored == 0 ? 0 : Math.Round((double)DeterministicCorrect / Scored, 4);
    public double RerankerAccuracy => Scored == 0 ? 0 : Math.Round((double)RerankerCorrect / Scored, 4);

    public static Cell For(Dictionary<string, Cell> map, string key)
    {
        if (!map.TryGetValue(key, out var cell))
        {
            cell = new Cell();
            map[key] = cell;
        }

        return cell;
    }
}

/// <summary>One row of the "consult the model only above this margin" sweep.</summary>
internal sealed record MarginRow(
    double Margin,
    int Consulted,
    double ConsultedShare,
    double Accuracy);

internal sealed record Flip(
    string Id,
    string Class,
    string Target,
    string Expected,
    string Deterministic,
    string Reranker,
    double Margin,
    bool BeyondWindow,
    string Sentence);

internal sealed class Report
{
    public required string RunUtc { get; init; }
    public required string Corpus { get; init; }
    public required string Split { get; init; }
    public required string RerankerVersion { get; init; }
    public required int RerankerMaxLength { get; init; }
    public int Scored { get; init; }
    public double DeterministicAccuracy { get; init; }
    public double RerankerAccuracy { get; init; }
    public int BothRight { get; init; }
    public int BothWrong { get; init; }
    public int RerankerFlippedCorrectToWrong { get; init; }
    public int RerankerRescuedWrongToCorrect { get; init; }
    public int NetEffect { get; init; }
    public int TargetsBeyondModelWindow { get; init; }
    public int TargetsBeyondWindowAndFlipped { get; init; }
    public double MeanFlipMargin { get; init; }
    public double MeanRescueMargin { get; init; }
    public required Dictionary<string, Cell> PerClass { get; init; }
    public required List<MarginRow> MarginSweep { get; init; }
    public required List<Flip> FlipExamples { get; init; }
    public required List<Flip> RescueExamples { get; init; }
}
