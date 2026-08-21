using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Ai;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Settings;
using WriteLite.Services.Spelling;

namespace LangBench;

/// <summary>
/// Scores the <em>assembled</em> WriteLite language pipeline against the frozen Russian
/// benchmark — rules, spelling, LanguageTool and the local model together, exactly as
/// <see cref="HybridTextAnalysisService"/> composes them for the editor.
///
/// This is the counterpart to <c>spellbench</c>, which measures the spelling layer alone.
/// spellbench reports 0.000 F1 for punctuation, morphology and real-word errors because a
/// spelling pass structurally cannot see them; those numbers are a property of that harness,
/// not of the product. This tool is what makes claims about those categories possible.
///
///   langbench [--layers rules,spell,lt,ai] [--corpus path] [--out path] [--label name]
///
/// Layers are opt-in so each one's contribution is attributable: run rules+spell first,
/// then add lt, then add ai, and the difference between the reports is what that layer
/// bought. Default is <c>rules,spell,lt</c> — the AI layer is excluded unless asked for,
/// because it needs a live loopback server and would otherwise silently score as absent.
/// </summary>
internal static partial class Program
{
    /// <summary>Same token shape spellbench uses, so token counts are comparable between the two.</summary>
    [GeneratedRegex(@"[\p{L}_][\p{L}\p{N}_]*", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    /// <summary>Categories whose items must come back completely untouched.</summary>
    private static readonly string[] NoChangeCategories = ["clean", "slang", "profanity"];

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var root = FindRepoRoot();

        var options = CliOptions.Parse(args, root);
        if (options is null)
        {
            Console.Error.WriteLine(
                "usage: langbench [--layers rules,spell,lt,ai] [--corpus path] [--out path] [--label name]");
            return 2;
        }

        if (!File.Exists(options.CorpusPath))
        {
            Console.Error.WriteLine($"benchmark not found: {options.CorpusPath}");
            return 1;
        }

        var items = LoadCorpus(options.CorpusPath);
        Console.WriteLine($"corpus : {options.CorpusPath} ({items.Count} items)");
        Console.WriteLine($"layers : {string.Join(",", options.Layers)}");

        await using var harness = await Harness.CreateAsync(options);
        Console.WriteLine($"engine : {harness.Describe()}");

        var report = await RunAsync(items, harness, options);

        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
        await File.WriteAllTextAsync(
            options.OutputPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }),
            new UTF8Encoding(false));

        Print(report, options.OutputPath);
        return 0;
    }

    private static void Print(Report report, string outputPath)
    {
        var o = report.Overall;
        Console.WriteLine();
        Console.WriteLine($"detection     : P={o.Precision:F3} R={o.Recall:F3} F1={o.F1:F3}");
        Console.WriteLine($"correction    : strict={o.StrictExactCorrectionAccuracy:F3} "
                          + $"final-text={o.FinalTextCorrectionAccuracy:F3}");
        Console.WriteLine($"edit quality  : subword={report.EditQuality.SubWordFragmentRate:F3} "
                          + $"phrase={report.EditQuality.PhraseLevelEditRate:F3} "
                          + $"meaningful={report.EditQuality.MeaningfulEditRate:F3} "
                          + $"overwide={report.EditQuality.OverWideEditRate:F3} "
                          + $"zeroLenFrag={report.EditQuality.ZeroLengthNonPunctuationInsertions}");
        Console.WriteLine($"clean FP      : {report.FalsePositives.PerHundredTokens:F3}/100 tokens");
        Console.WriteLine($"no-change acc : {report.NoChangeAccuracy.Accuracy:F3}");
        Console.WriteLine($"semantic      : {report.SemanticCorruption.Corrupted} corrupted "
                          + $"({report.SemanticCorruption.Rate:P2} of {report.SemanticCorruption.Total} changed items)");
        Console.WriteLine($"latency       : p50={report.Timings.MsPerSentenceP50:F2} "
                          + $"p95={report.Timings.MsPerSentenceP95:F2} max={report.Timings.MsPerSentenceMax:F2} ms");
        Console.WriteLine($"peak RSS      : {report.Timings.PeakWorkingSetMb:F1} MB");
        if (report.TimedOutItems > 0)
        {
            Console.WriteLine($"TIMED OUT     : {report.TimedOutItems} items never analysed — "
                              + "recall below is a floor, not a measurement");
        }
        Console.WriteLine();
        Console.WriteLine($"{"category",-15} {"n",4} {"P",6} {"R",6} {"F1",6} {"strict",6} {"final",6} {"noChg",6}");
        foreach (var (name, m) in report.PerCategory)
        {
            var noChange = NoChangeCategories.Contains(name)
                ? m.NoChangeAccuracy.ToString("F3", CultureInfo.InvariantCulture)
                : "—";
            Console.WriteLine(
                $"{name,-15} {m.Items,4} {m.Precision,6:F3} {m.Recall,6:F3} {m.F1,6:F3} "
                + $"{m.StrictExactCorrectionAccuracy,6:F3} {m.FinalTextCorrectionAccuracy,6:F3} {noChange,6}");
        }

        if (report.Routing is { } routing)
        {
            Console.WriteLine();
            Console.WriteLine($"AI routing    : {routing.SentencesRouted}/{routing.SentencesSeen} sentences "
                              + $"({routing.RoutedFraction:P1}), {routing.AiCallsPerSentence:F3} calls/sentence");
            Console.WriteLine($"AI findings   : offered={routing.FindingsOffered} accepted={routing.FindingsAccepted} "
                              + $"({routing.AcceptedFraction:P1})");
        }

        Console.WriteLine();
        Console.WriteLine($"report        : {outputPath}");
    }

    private static async Task<Report> RunAsync(
        IReadOnlyList<Item> items,
        Harness harness,
        CliOptions options)
    {
        var perItem = new List<ItemOutcome>(items.Count);
        var durations = new List<double>(items.Count);

        // One warm-up so JIT and the first dictionary page fault do not land on item 1.
        _ = await harness.AnalyzeAsync("Проверка орфографии.");

        var swTotal = Stopwatch.StartNew();
        foreach (var item in items)
        {
            var sw = Stopwatch.StartNew();
            var issues = await harness.AnalyzeAsync(item.Source);
            sw.Stop();
            durations.Add(sw.Elapsed.TotalMilliseconds);
            perItem.Add(Score(item, issues));
        }

        swTotal.Stop();

        var process = Process.GetCurrentProcess();
        process.Refresh();

        var categories = perItem
            .Select(x => x.Item.Category)
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToDictionary(
                c => c,
                c => Aggregate(perItem.Where(x => x.Item.Category == c).ToList()),
                StringComparer.Ordinal);

        var cleanOnly = perItem.Where(x => x.Item.Category == "clean").ToList();
        var noChangeSet = perItem.Where(x => NoChangeCategories.Contains(x.Item.Category)).ToList();
        var changed = perItem.Where(x => x.TextChanged).ToList();
        var corrupted = changed.Where(x => x.Semantic.Corrupted).ToList();
        var sorted = durations.OrderBy(x => x).ToArray();

        return new Report
        {
            Corpus = options.CorpusPath,
            CorpusSha256 = Sha256(options.CorpusPath),
            CorpusSha256Lf = Sha256Lf(options.CorpusPath),
            ItemCount = items.Count,
            RunUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Label = options.Label,
            Layers = options.Layers,
            Configuration = harness.Describe(),
            TimedOutItems = harness.TimeoutCount,
            Overall = Aggregate(perItem),
            PerCategory = categories,
            FalsePositives_Detail = perItem.SelectMany(x => x.FalsePositiveDetails).ToList(),
            BySource = BuildSourceStats(perItem),
            EditQuality = AggregateEditQuality(
                perItem.SelectMany(x => x.Issues.Select(i => EditQuality.Classify(x.Item.Source, i)))),
            EditQualityBySource = perItem
                .SelectMany(x => x.Issues.Select(i => (Source: SourceOf(i.RuleId), Shape: EditQuality.Classify(x.Item.Source, i))))
                .GroupBy(x => x.Source, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => AggregateEditQuality(g.Select(x => x.Shape)), StringComparer.Ordinal),
            CorrectionTrace = perItem.SelectMany(x => x.CorrectionTrace).ToList(),
            CorrectionOutcomes = perItem
                .SelectMany(x => x.CorrectionTrace)
                .GroupBy(x => x.Outcome, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            Routing = options.UseAi ? BuildRoutingBlock(harness.RoutingMetrics, items.Count) : null,
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
            SemanticCorruption = new SemanticBlock
            {
                Scope = "items whose text changed after applying auto-appliable corrections",
                Total = changed.Count,
                Corrupted = corrupted.Count,
                Rate = Ratio(corrupted.Count, changed.Count),
                ByReason = corrupted
                    .SelectMany(x => x.Semantic.Reasons)
                    .GroupBy(r => r, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count())
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                Examples = corrupted
                    .Take(20)
                    .Select(x => new SemanticExample
                    {
                        Category = x.Item.Category,
                        Id = x.Item.Id,
                        Reasons = x.Semantic.Reasons,
                        Original = x.Item.Source,
                        Produced = x.ProducedText,
                    })
                    .ToList(),
            },
            Timings = new TimingBlock
            {
                TotalMs = Math.Round(swTotal.Elapsed.TotalMilliseconds, 3),
                MsPerSentenceMean = Math.Round(Ratio(swTotal.Elapsed.TotalMilliseconds, items.Count), 4),
                MsPerSentenceP50 = Math.Round(Percentile(sorted, 0.50), 4),
                MsPerSentenceP95 = Math.Round(Percentile(sorted, 0.95), 4),
                MsPerSentenceP99 = Math.Round(Percentile(sorted, 0.99), 4),
                MsPerSentenceMax = Math.Round(sorted.Length == 0 ? 0 : sorted[^1], 4),
                PeakWorkingSetMb = Math.Round(process.PeakWorkingSet64 / 1024.0 / 1024.0, 2),
                WorkingSetMb = Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 2),
                ManagedHeapMb = Math.Round(GC.GetTotalMemory(false) / 1024.0 / 1024.0, 2),
            },
        };
    }

    /// <summary>
    /// Scores one item using the same conventions as spellbench: a prediction detects a
    /// gold error when the character spans overlap, and each prediction is consumed by at
    /// most one gold error, so extra underlines remain false positives.
    /// </summary>
    private static ItemOutcome Score(Item item, IReadOnlyList<TextIssue> issues)
    {
        var used = new bool[issues.Count];
        var matchedGold = new string?[issues.Count];
        var correctionTrace = new List<CorrectionTraceEntry>();
        var truePositives = 0;
        var correctedExactly = 0;
        var correctedFinalText = 0;

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
            matchedGold[matched] = gold.Type;
            truePositives++;

            var issue = issues[matched];
            var exactSpan = issue.Start == gold.Start && issue.Start + issue.Length == gold.End;
            var counted = exactSpan && string.Equals(issue.Replacement, gold.Replacement, StringComparison.Ordinal);
            if (counted)
            {
                correctedExactly++;
            }

            var finalTextCorrect = counted || ProducesExpectedText(item.Source, gold, issue);
            if (finalTextCorrect)
            {
                correctedFinalText++;
            }

            correctionTrace.Add(new CorrectionTraceEntry
            {
                ItemId = item.Id,
                Category = item.Category,
                Source = SourceOf(issue.RuleId),
                ExactSpan = exactSpan,
                Original = issue.Original,
                Predicted = issue.Replacement,
                Expected = gold.Replacement,
                CountedCorrect = counted,
                FinalTextCorrect = finalTextCorrect,
                Outcome = counted ? "correct"
                    : finalTextCorrect ? "correct-text-wider-span"
                    : !exactSpan ? "span-mismatch"
                    : string.IsNullOrEmpty(issue.Replacement) ? "detection-only-no-replacement"
                    : "wrong-replacement",
            });
        }

        var preserved = 0;
        foreach (var token in item.MustNotChange)
        {
            var index = item.Source.IndexOf(token, StringComparison.Ordinal);
            if (index < 0) continue;
            var end = index + token.Length;
            var touched = issues.Any(i => i.Start < end && index < i.Start + i.Length);
            if (!touched) preserved++;
        }

        var produced = ApplyAutoCorrections(item.Source, issues);

        // Everything the pipeline flagged that no gold error claimed, with the layer that
        // produced it. This is the raw material for false-positive clustering.
        var falsePositiveDetails = new List<FalsePositiveDetail>();
        for (var i = 0; i < issues.Count; i++)
        {
            if (used[i]) continue;
            var issue = issues[i];
            falsePositiveDetails.Add(new FalsePositiveDetail
            {
                ItemId = item.Id,
                Category = item.Category,
                Source = SourceOf(issue.RuleId),
                RuleId = issue.RuleId,
                IssueCategory = issue.Category.ToString(),
                Original = issue.Original,
                Replacement = issue.Replacement,
                Confidence = issue.Confidence,
                Sentence = item.Source,
            });
        }

        return new ItemOutcome
        {
            Item = item,
            Issues = issues,
            CorrectionTrace = correctionTrace,
            FalsePositiveDetails = falsePositiveDetails,
            TokenCount = WordRegex().Matches(item.Source).Count,
            PredictedCount = issues.Count,
            GoldCount = item.Errors.Count,
            TruePositives = truePositives,
            FalsePositives = used.Count(u => !u),
            CorrectedExactly = correctedExactly,
            CorrectedFinalText = correctedFinalText,
            PreservedTokens = preserved,
            ProducedText = produced,
            TextChanged = !string.Equals(produced, item.Source, StringComparison.Ordinal),
            Semantic = SemanticGuard.Compare(item.Source, produced, item.Target),
        };
    }

    /// <summary>
    /// Whether applying just this prediction to the source produces the sentence the gold
    /// error asks for.
    /// </summary>
    /// <remarks>
    /// This is the §7 product-level correction test. The comparison is between two whole
    /// sentences — source with only the gold's correction applied, against source with only
    /// this prediction applied — which is what makes a wider-than-gold span acceptable and
    /// a wrong replacement still wrong. It cannot be gamed by a broad span: a phrase edit
    /// that repairs the error but also changes a neighbouring word produces a different
    /// sentence and fails here.
    /// </remarks>
    private static bool ProducesExpectedText(string source, GoldError gold, TextIssue issue)
    {
        if (issue.Replacement is null)
        {
            return false;
        }

        if (issue.Start < 0 || issue.Length < 0 || issue.Start + issue.Length > source.Length)
        {
            return false;
        }

        var expected = source[..gold.Start] + gold.Replacement + source[gold.End..];
        var produced = source[..issue.Start] + issue.Replacement + source[(issue.Start + issue.Length)..];
        return string.Equals(expected, produced, StringComparison.Ordinal);
    }

    /// <summary>
    /// Applies every suggested replacement, right to left so earlier offsets stay valid.
    /// Overlapping issues are skipped rather than stacked, matching the editor, which
    /// cannot apply two replacements to the same span.
    /// </summary>
    /// <remarks>
    /// Scope is deliberately "everything the user would get by accepting all suggestions",
    /// not just the <c>CanApplyAutomatically</c> subset. Restricting it to auto-appliable
    /// issues left only 4 of 841 items with any text change, which is far too small a
    /// denominator to say anything about semantic safety — and the user accepting a
    /// suggestion corrupts their text just as thoroughly as autocorrect doing it.
    /// </remarks>
    private static string ApplyAutoCorrections(string source, IReadOnlyList<TextIssue> issues)
    {
        // Zero-length insertions are applied too. They were excluded while the AI diff was
        // character-based, when an empty original meant a sliced word; a word-aware diff
        // emits them only for real insertions — a missing comma is the single largest
        // punctuation category in the corpus, and a scorer that cannot apply one cannot
        // measure whether punctuation improved.
        var applicable = issues
            .Where(i => !string.IsNullOrEmpty(i.Replacement)
                        && i.Start >= 0
                        && i.Length >= 0
                        && i.Start + i.Length <= source.Length)
            .OrderByDescending(i => i.Start)
            .ThenByDescending(i => i.Length)
            .ToList();

        var builder = new StringBuilder(source);
        var lastStart = int.MaxValue;
        foreach (var issue in applicable)
        {
            if (issue.Start + issue.Length > lastStart)
            {
                continue;
            }

            builder.Remove(issue.Start, issue.Length);
            builder.Insert(issue.Start, issue.Replacement!);
            lastStart = issue.Start;
        }

        return builder.ToString();
    }

    private static MetricBlock Aggregate(IReadOnlyList<ItemOutcome> outcomes)
    {
        var gold = outcomes.Sum(x => x.GoldCount);
        var truePositives = outcomes.Sum(x => x.TruePositives);
        var falsePositives = outcomes.Sum(x => x.FalsePositives);
        var predicted = truePositives + falsePositives;
        var precision = Ratio(truePositives, predicted);
        var recall = Ratio(truePositives, gold);
        var noChangeScope = outcomes.Where(x => NoChangeCategories.Contains(x.Item.Category)).ToList();

        return new MetricBlock
        {
            Items = outcomes.Count,
            Tokens = outcomes.Sum(x => x.TokenCount),
            GoldErrors = gold,
            Predicted = predicted,
            TruePositives = truePositives,
            FalsePositives = falsePositives,
            FalseNegatives = gold - truePositives,
            Precision = Round(precision),
            Recall = Round(recall),
            F1 = Round(precision + recall <= 0 ? 0 : 2 * precision * recall / (precision + recall)),
            CorrectionAccuracy = Round(Ratio(outcomes.Sum(x => x.CorrectedExactly), gold)),
            StrictExactCorrectionAccuracy = Round(Ratio(outcomes.Sum(x => x.CorrectedExactly), gold)),
            FinalTextCorrectionAccuracy = Round(Ratio(outcomes.Sum(x => x.CorrectedFinalText), gold)),
            NoChangeAccuracy = Round(Ratio(noChangeScope.Count(x => x.PredictedCount == 0), noChangeScope.Count)),
            SemanticCorruptions = outcomes.Count(x => x.TextChanged && x.Semantic.Corrupted),
        };
    }

    private static EditQualityBlock AggregateEditQuality(IEnumerable<EditShape> shapes)
    {
        var all = shapes.ToList();
        var total = all.Count;
        return new EditQualityBlock
        {
            Edits = total,
            SubWordFragments = all.Count(x => x.SubWordFragment),
            ZeroLengthNonPunctuationInsertions = all.Count(x => x.ZeroLengthNonPunctuationInsertion),
            PhraseLevelEdits = all.Count(x => x.PhraseLevel),
            OverWideEdits = all.Count(x => x.OverWide),
            MeaningfulEdits = all.Count(x => x.Meaningful),
            SubWordFragmentRate = Round(Ratio(all.Count(x => x.SubWordFragment), total)),
            PhraseLevelEditRate = Round(Ratio(all.Count(x => x.PhraseLevel), total)),
            MeaningfulEditRate = Round(Ratio(all.Count(x => x.Meaningful), total)),
            OverWideEditRate = Round(Ratio(all.Count(x => x.OverWide), total)),
        };
    }

    private static RoutingBlock BuildRoutingBlock(WriteLite.Services.Ai.AiRoutingMetrics metrics, int itemCount)
        => new()
        {
            SentencesSeen = metrics.SentencesSeen,
            SentencesRouted = metrics.SentencesRouted,
            RoutedFraction = Round(metrics.RoutedFraction),
            FindingsOffered = metrics.FindingsOffered,
            FindingsAccepted = metrics.FindingsAccepted,
            AcceptedFraction = Round(metrics.AcceptedFraction),
            AiCallsPerSentence = Round(Ratio(metrics.SentencesRouted, itemCount)),
            RoutingReasons = metrics.RoutingReasons.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            RejectionReasons = metrics.RejectionReasons.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            OfferedBySupportClass = metrics.OfferedByClass.ToDictionary(x => x.Key.ToString(), x => x.Value, StringComparer.Ordinal),
            AcceptedBySupportClass = metrics.AcceptedByClass.ToDictionary(x => x.Key.ToString(), x => x.Value, StringComparer.Ordinal),
        };

    /// <summary>
    /// Attributes findings and false positives to the layer that produced them.
    /// </summary>
    /// <remarks>
    /// True positives are counted by subtraction rather than tracked per issue: an issue
    /// that matched a gold error is a true positive for whichever layer emitted it, and the
    /// scorer already consumed each prediction at most once, so predicted minus false
    /// positive is exact.
    /// </remarks>
    private static Dictionary<string, SourceStats> BuildSourceStats(IReadOnlyList<ItemOutcome> outcomes)
    {
        var predicted = new Dictionary<string, int>(StringComparer.Ordinal);
        var falsePositives = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var outcome in outcomes)
        {
            foreach (var issue in outcome.Issues)
            {
                var source = SourceOf(issue.RuleId);
                predicted[source] = predicted.GetValueOrDefault(source) + 1;
            }

            foreach (var detail in outcome.FalsePositiveDetails)
            {
                falsePositives[detail.Source] = falsePositives.GetValueOrDefault(detail.Source) + 1;
            }
        }

        return predicted.Keys
            .Union(falsePositives.Keys, StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToDictionary(
                source => source,
                source =>
                {
                    var total = predicted.GetValueOrDefault(source);
                    var bad = falsePositives.GetValueOrDefault(source);
                    return new SourceStats
                    {
                        Predicted = total,
                        TruePositives = total - bad,
                        FalsePositives = bad,
                        Precision = Round(Ratio(total - bad, total)),
                    };
                },
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Which layer produced a finding, from its rule id. AI findings are prefixed
    /// <c>WL-AI-</c> by <c>AiTextAnalysisService</c>; the deterministic layers use their
    /// own stable prefixes.
    /// </summary>
    private static string SourceOf(string? ruleId)
    {
        var id = ruleId ?? "";
        if (id.StartsWith("WL-AI-", StringComparison.OrdinalIgnoreCase)) return "ai";
        if (id.StartsWith("ru.context.", StringComparison.OrdinalIgnoreCase)) return "reranker";
        if (id.StartsWith("ru.spelling.", StringComparison.OrdinalIgnoreCase)) return "spell";
        if (id.StartsWith("ru.", StringComparison.OrdinalIgnoreCase)) return "rules";
        if (id.Length == 0 || id == "unknown") return "unknown";
        return "languagetool";
    }

    private static double Ratio(double numerator, double denominator)
        => denominator <= 0 ? 0 : numerator / denominator;

    private static double Round(double value) => Math.Round(value, 4);

    private static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Ceiling(q * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static List<Item> LoadCorpus(string path)
    {
        var items = new List<Item>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var raw = JsonSerializer.Deserialize<RawItem>(line);
            if (raw is null || raw.Source is null) continue;

            items.Add(new Item
            {
                Id = raw.Id ?? "",
                Category = raw.Category ?? "unknown",
                Source = raw.Source,
                Target = raw.Target ?? raw.Source,
                Errors = (raw.Errors ?? [])
                    .Where(e => e.Start >= 0 && e.End > e.Start && e.End <= raw.Source.Length)
                    .Select(e => new GoldError(e.Start, e.End, e.Replacement ?? "", e.Type ?? ""))
                    .ToList(),
                MustNotChange = raw.MustNotChange ?? [],
            });
        }

        return items;
    }

    private static string Sha256(string path)
        => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>
    /// Hash over line-ending-normalised content. The frozen corpus manifest records its
    /// hash this way, while the file on a Windows working copy is CRLF — comparing raw
    /// bytes makes an unmodified corpus look tampered with.
    /// </summary>
    private static string Sha256Lf(string path)
    {
        var normalised = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(normalised).Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
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
