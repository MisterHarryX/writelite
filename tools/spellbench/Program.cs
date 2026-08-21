using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WriteLite.Language.Core;
using WriteLite.Models;
using WriteLite.Services.Spelling;

namespace SpellBench;

/// <summary>
/// Scores the shipped spelling pipeline (<see cref="LocalSpellChecker"/> behind
/// <see cref="SpellTextAnalyzer"/>) against the frozen Russian benchmark.
///
/// Deterministic and fully offline: the corpus is read in file order, every item
/// is analysed exactly once on a single thread, and nothing is sampled.
///
///   spellbench [benchmark.jsonl] [output.json]
///
/// Defaults: ai/data/benchmark/ru_frozen_v1.jsonl and
/// ai/outputs/benchmark/&lt;timestamp&gt;.json, both resolved against the repo root.
/// </summary>
internal static partial class Program
{
    /// <summary>Same token shape the analyzer uses, so token counts line up with what it saw.</summary>
    [GeneratedRegex(@"[\p{L}_][\p{L}\p{N}_]*", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    /// <summary>Categories whose items must come back completely untouched.</summary>
    private static readonly string[] NoChangeCategories = ["clean", "slang", "profanity"];

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var root = FindRepoRoot();

        var corpusPath = args.Length > 0 && args[0].Length > 0
            ? Path.GetFullPath(args[0])
            : Path.Combine(root, "ai", "data", "benchmark", "ru_frozen_v1.jsonl");

        if (!File.Exists(corpusPath))
        {
            Console.Error.WriteLine($"benchmark not found: {corpusPath}");
            return 1;
        }

        var outputPath = args.Length > 1 && args[1].Length > 0
            ? Path.GetFullPath(args[1])
            : Path.Combine(
                root, "ai", "outputs", "benchmark",
                DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture) + ".json");

        var items = LoadCorpus(corpusPath);
        Console.WriteLine($"corpus: {corpusPath} ({items.Count} items)");

        using var checker = new LocalSpellChecker();
        var stats = checker.Stats;
        var analyzer = new SpellTextAnalyzer(checker);

        // One warm-up so JIT and the first dictionary page fault do not land on item 1.
        analyzer.Analyze("Проверка орфографии.");

        var report = Run(items, checker, analyzer, stats, corpusPath);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }),
            new UTF8Encoding(false));

        Console.WriteLine($"configuration : {report.Configuration}");
        Console.WriteLine($"dictionary    : {stats.Source}");
        var o = report.Overall;
        Console.WriteLine($"detection     : P={o.Precision:F3} R={o.Recall:F3} F1={o.F1:F3}");
        Console.WriteLine($"correction    : {o.CorrectionAccuracy:F3}  top1={o.Top1Accuracy:F3} top3={o.Top3Recall:F3}");
        Console.WriteLine($"clean FP      : {report.FalsePositives.PerHundredTokens:F3}/100 tokens, "
                          + $"{report.FalsePositives.PerSentenceRate:F3}/sentence");
        Console.WriteLine($"no-change acc : {report.NoChangeAccuracy.Accuracy:F3}");
        Console.WriteLine($"report        : {outputPath}");
        return 0;
    }

    private static Report Run(
        IReadOnlyList<Item> items,
        ISpellChecker checker,
        SpellTextAnalyzer analyzer,
        SpellDictionaryStats stats,
        string corpusPath)
    {
        var perItem = new List<ItemOutcome>(items.Count);
        var durations = new List<double>(items.Count);

        var swTotal = Stopwatch.StartNew();
        foreach (var item in items)
        {
            var sw = Stopwatch.StartNew();
            var issues = analyzer.Analyze(item.Source);
            sw.Stop();
            durations.Add(sw.Elapsed.TotalMilliseconds);
            perItem.Add(Score(item, issues, checker));
        }

        swTotal.Stop();
        var totalMs = swTotal.Elapsed.TotalMilliseconds;

        var process = Process.GetCurrentProcess();
        process.Refresh();

        var categories = perItem
            .Select(x => x.Item.Category)
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToDictionary(c => c, c => Aggregate(perItem.Where(x => x.Item.Category == c).ToList()), StringComparer.Ordinal);

        var cleanOnly = perItem.Where(x => x.Item.Category == "clean").ToList();
        var noChangeSet = perItem.Where(x => NoChangeCategories.Contains(x.Item.Category)).ToList();
        var slang = perItem.Where(x => x.Item.Category == "slang").ToList();
        var profanity = perItem.Where(x => x.Item.Category == "profanity").ToList();
        var realWord = perItem.Where(x => x.Item.Category == "real_word").ToList();

        var sorted = durations.OrderBy(x => x).ToArray();

        return new Report
        {
            Corpus = corpusPath,
            CorpusSha256 = Sha256(corpusPath),
            ItemCount = items.Count,
            RunUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Configuration = stats.Source.Contains("form index", StringComparison.OrdinalIgnoreCase)
                ? "with-form-index"
                : "hunspell-baseline",
            DictionarySource = stats.Source,
            DictionaryWordCount = stats.RussianWordCount,
            FullDictionaryLoaded = stats.IsFullDictionaryLoaded,
            Overall = Aggregate(perItem),
            PerCategory = categories,
            FalsePositives = new FalsePositiveBlock
            {
                Sentences = cleanOnly.Count,
                Tokens = cleanOnly.Sum(x => x.TokenCount),
                FalsePositiveIssues = cleanOnly.Sum(x => x.FalsePositives),
                SentencesWithAnyIssue = cleanOnly.Count(x => x.FalsePositives > 0),
                PerHundredTokens = Ratio(cleanOnly.Sum(x => x.FalsePositives) * 100.0, cleanOnly.Sum(x => x.TokenCount)),
                PerSentenceRate = Ratio(cleanOnly.Count(x => x.FalsePositives > 0), cleanOnly.Count),
            },
            NoChangeAccuracy = new RateBlock
            {
                Scope = string.Join("+", NoChangeCategories),
                Total = noChangeSet.Count,
                Correct = noChangeSet.Count(x => x.PredictedCount == 0),
                Accuracy = Ratio(noChangeSet.Count(x => x.PredictedCount == 0), noChangeSet.Count),
            },
            RealWordAccuracy = new RateBlock
            {
                Scope = "real_word",
                Total = realWord.Sum(x => x.GoldCount),
                Correct = realWord.Sum(x => x.CorrectedExactly),
                Accuracy = Ratio(realWord.Sum(x => x.CorrectedExactly), realWord.Sum(x => x.GoldCount)),
            },
            RealWordDetection = new RateBlock
            {
                Scope = "real_word",
                Total = realWord.Sum(x => x.GoldCount),
                Correct = realWord.Sum(x => x.TruePositives),
                Accuracy = Ratio(realWord.Sum(x => x.TruePositives), realWord.Sum(x => x.GoldCount)),
            },
            SlangPreservation = Preservation("slang", slang),
            ProfanityPreservation = Preservation("profanity", profanity),
            Timings = new TimingBlock
            {
                TotalMs = Math.Round(totalMs, 3),
                MsPerSentenceMean = Math.Round(Ratio(totalMs, items.Count), 4),
                MsPerSentenceP50 = Math.Round(Percentile(sorted, 0.50), 4),
                MsPerSentenceP95 = Math.Round(Percentile(sorted, 0.95), 4),
                MsPerSentenceMax = Math.Round(sorted.Length == 0 ? 0 : sorted[^1], 4),
                DictionaryLoadMs = Math.Round(stats.InitialLoadTime.TotalMilliseconds, 3),
                PeakWorkingSetMb = Math.Round(process.PeakWorkingSet64 / 1024.0 / 1024.0, 2),
                WorkingSetMb = Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 2),
                ManagedHeapMb = Math.Round(GC.GetTotalMemory(false) / 1024.0 / 1024.0, 2),
            },
        };
    }

    private static RateBlock Preservation(string scope, IReadOnlyList<ItemOutcome> outcomes)
    {
        var total = outcomes.Sum(x => x.Item.MustNotChange.Count);
        var kept = outcomes.Sum(x => x.PreservedTokens);
        return new RateBlock
        {
            Scope = scope + " must_not_change tokens",
            Total = total,
            Correct = kept,
            Accuracy = Ratio(kept, total),
        };
    }

    /// <summary>
    /// Scores one item. A prediction counts as detecting a gold error when the two
    /// character spans overlap; each prediction is consumed by at most one gold
    /// error (greedy, left to right), so extra underlines stay false positives.
    /// </summary>
    private static ItemOutcome Score(Item item, IReadOnlyList<TextIssue> issues, ISpellChecker checker)
    {
        var used = new bool[issues.Count];
        var truePositives = 0;
        var correctedExactly = 0;
        var top1 = 0;
        var top3 = 0;
        var detectedForSuggestionScoring = 0;

        foreach (var gold in item.Errors)
        {
            var matched = -1;
            for (var i = 0; i < issues.Count; i++)
            {
                if (used[i]) continue;
                var start = issues[i].Start;
                var end = issues[i].Start + issues[i].Length;
                if (start < gold.End && gold.Start < end)
                {
                    matched = i;
                    break;
                }
            }

            if (matched < 0) continue;

            used[matched] = true;
            truePositives++;

            var issue = issues[matched];
            var exactSpan = issue.Start == gold.Start && issue.Start + issue.Length == gold.End;
            if (exactSpan && string.Equals(issue.Replacement, gold.Replacement, StringComparison.Ordinal))
            {
                correctedExactly++;
            }

            // Full ranked list for this token, filtered exactly as the analyzer filters it.
            var ranked = CorrectionCandidateValidityPolicy.FilterSuggestions(
                issue.Original,
                checker.CheckWord(issue.Original, SpellingLanguage.Russian).Suggestions,
                max: 5);

            detectedForSuggestionScoring++;
            if (exactSpan)
            {
                if (ranked.Count > 0 && string.Equals(ranked[0], gold.Replacement, StringComparison.Ordinal)) top1++;
                if (ranked.Take(3).Any(s => string.Equals(s, gold.Replacement, StringComparison.Ordinal))) top3++;
            }
        }

        var falsePositives = used.Count(u => !u);

        var preserved = 0;
        foreach (var token in item.MustNotChange)
        {
            var index = item.Source.IndexOf(token, StringComparison.Ordinal);
            if (index < 0) continue;
            var end = index + token.Length;
            var touched = issues.Any(i => i.Start < end && index < i.Start + i.Length);
            if (!touched) preserved++;
        }

        return new ItemOutcome
        {
            Item = item,
            TokenCount = WordRegex().Matches(item.Source).Count,
            PredictedCount = issues.Count,
            GoldCount = item.Errors.Count,
            TruePositives = truePositives,
            FalsePositives = falsePositives,
            CorrectedExactly = correctedExactly,
            Top1 = top1,
            Top3 = top3,
            DetectedForSuggestionScoring = detectedForSuggestionScoring,
            PreservedTokens = preserved,
        };
    }

    private static MetricBlock Aggregate(IReadOnlyList<ItemOutcome> outcomes)
    {
        var tp = outcomes.Sum(x => x.TruePositives);
        var fp = outcomes.Sum(x => x.FalsePositives);
        var gold = outcomes.Sum(x => x.GoldCount);
        var fn = gold - tp;
        var precision = Ratio(tp, tp + fp);
        var recall = Ratio(tp, gold);
        var detected = outcomes.Sum(x => x.DetectedForSuggestionScoring);
        var noChangeItems = outcomes.Where(x => x.GoldCount == 0).ToList();

        return new MetricBlock
        {
            Items = outcomes.Count,
            Tokens = outcomes.Sum(x => x.TokenCount),
            GoldErrors = gold,
            Predicted = outcomes.Sum(x => x.PredictedCount),
            TruePositives = tp,
            FalsePositives = fp,
            FalseNegatives = fn,
            Precision = Round(precision),
            Recall = Round(recall),
            F1 = Round(precision + recall <= 0 ? 0 : 2 * precision * recall / (precision + recall)),
            CorrectionAccuracy = Round(Ratio(outcomes.Sum(x => x.CorrectedExactly), gold)),
            Top1Accuracy = Round(Ratio(outcomes.Sum(x => x.Top1), detected)),
            Top3Recall = Round(Ratio(outcomes.Sum(x => x.Top3), detected)),
            CleanItems = noChangeItems.Count,
            CleanItemsUntouched = noChangeItems.Count(x => x.PredictedCount == 0),
            NoChangeAccuracy = noChangeItems.Count == 0
                ? null
                : Round(Ratio(noChangeItems.Count(x => x.PredictedCount == 0), noChangeItems.Count)),
        };
    }

    private static double Ratio(double numerator, double denominator)
        => denominator <= 0 ? 0 : numerator / denominator;

    private static double Round(double value) => Math.Round(value, 4);

    private static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 0) return 0;
        var rank = q * (sorted.Length - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        return low == high ? sorted[low] : sorted[low] + (rank - low) * (sorted[high] - sorted[low]);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static List<Item> LoadCorpus(string path)
    {
        var items = new List<Item>();
        foreach (var line in File.ReadLines(path, new UTF8Encoding(false)))
        {
            if (line.Length == 0) continue;
            using var document = JsonDocument.Parse(line);
            var element = document.RootElement;
            var errors = new List<GoldError>();
            if (element.TryGetProperty("errors", out var errorArray))
            {
                foreach (var error in errorArray.EnumerateArray())
                {
                    errors.Add(new GoldError(
                        error.GetProperty("start").GetInt32(),
                        error.GetProperty("end").GetInt32(),
                        error.GetProperty("original").GetString() ?? string.Empty,
                        error.GetProperty("replacement").GetString() ?? string.Empty,
                        error.GetProperty("type").GetString() ?? string.Empty));
                }
            }

            var mustNotChange = new List<string>();
            if (element.TryGetProperty("must_not_change", out var keep))
            {
                mustNotChange.AddRange(keep.EnumerateArray().Select(x => x.GetString() ?? string.Empty));
            }

            var source = element.GetProperty("source").GetString() ?? string.Empty;
            foreach (var error in errors)
            {
                if (source[error.Start..error.End] != error.Original)
                {
                    throw new InvalidDataException(
                        $"corpus span mismatch in {element.GetProperty("id").GetString()}: "
                        + $"'{source[error.Start..error.End]}' != '{error.Original}'");
                }
            }

            items.Add(new Item(
                element.GetProperty("id").GetString() ?? string.Empty,
                element.GetProperty("category").GetString() ?? string.Empty,
                source,
                element.GetProperty("target").GetString() ?? string.Empty,
                errors,
                mustNotChange));
        }

        return items;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WriteLite.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    // ---- data ----------------------------------------------------------

    private sealed record GoldError(int Start, int End, string Original, string Replacement, string Type);

    private sealed record Item(
        string Id,
        string Category,
        string Source,
        string Target,
        IReadOnlyList<GoldError> Errors,
        IReadOnlyList<string> MustNotChange);

    private sealed class ItemOutcome
    {
        public required Item Item { get; init; }
        public int TokenCount { get; init; }
        public int PredictedCount { get; init; }
        public int GoldCount { get; init; }
        public int TruePositives { get; init; }
        public int FalsePositives { get; init; }
        public int CorrectedExactly { get; init; }
        public int Top1 { get; init; }
        public int Top3 { get; init; }
        public int DetectedForSuggestionScoring { get; init; }
        public int PreservedTokens { get; init; }
    }

    private sealed class MetricBlock
    {
        public int Items { get; init; }
        public int Tokens { get; init; }
        public int GoldErrors { get; init; }
        public int Predicted { get; init; }
        public int TruePositives { get; init; }
        public int FalsePositives { get; init; }
        public int FalseNegatives { get; init; }
        public double Precision { get; init; }
        public double Recall { get; init; }
        public double F1 { get; init; }
        public double CorrectionAccuracy { get; init; }
        public double Top1Accuracy { get; init; }
        public double Top3Recall { get; init; }
        public int CleanItems { get; init; }
        public int CleanItemsUntouched { get; init; }
        public double? NoChangeAccuracy { get; init; }
    }

    private sealed class FalsePositiveBlock
    {
        public int Sentences { get; init; }
        public int Tokens { get; init; }
        public int FalsePositiveIssues { get; init; }
        public int SentencesWithAnyIssue { get; init; }
        public double PerHundredTokens { get; init; }
        public double PerSentenceRate { get; init; }
    }

    private sealed class RateBlock
    {
        public required string Scope { get; init; }
        public int Total { get; init; }
        public int Correct { get; init; }
        public double Accuracy { get; init; }
    }

    private sealed class TimingBlock
    {
        public double TotalMs { get; init; }
        public double MsPerSentenceMean { get; init; }
        public double MsPerSentenceP50 { get; init; }
        public double MsPerSentenceP95 { get; init; }
        public double MsPerSentenceMax { get; init; }
        public double DictionaryLoadMs { get; init; }
        public double PeakWorkingSetMb { get; init; }
        public double WorkingSetMb { get; init; }
        public double ManagedHeapMb { get; init; }
    }

    private sealed class Report
    {
        public required string Corpus { get; init; }
        public required string CorpusSha256 { get; init; }
        public int ItemCount { get; init; }
        public required string RunUtc { get; init; }
        public required string Configuration { get; init; }
        public required string DictionarySource { get; init; }
        public int DictionaryWordCount { get; init; }
        public bool FullDictionaryLoaded { get; init; }
        public required MetricBlock Overall { get; init; }
        public required Dictionary<string, MetricBlock> PerCategory { get; init; }
        public required FalsePositiveBlock FalsePositives { get; init; }
        public required RateBlock NoChangeAccuracy { get; init; }
        public required RateBlock RealWordAccuracy { get; init; }
        public required RateBlock RealWordDetection { get; init; }
        public required RateBlock SlangPreservation { get; init; }
        public required RateBlock ProfanityPreservation { get; init; }
        public required TimingBlock Timings { get; init; }
    }
}
