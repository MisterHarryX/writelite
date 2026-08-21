using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WriteLite.AI.Contracts;
using WriteLite.AI.Local;
using WriteLite.Services.Spelling;

namespace JudgeBench;

internal sealed record CorpusError(int Start, int End, string Replacement, string Type);

internal sealed record CorpusItem(
    string Id,
    string Category,
    string Source,
    string Target,
    IReadOnlyList<CorpusError> Errors);

internal static class Corpus
{
    /// <summary>Categories whose items must come back untouched — the NO_CHANGE ground truth.</summary>
    internal static readonly string[] NoChangeCategories = ["clean", "slang", "profanity"];

    public static IReadOnlyList<CorpusItem> Load(string path)
    {
        var items = new List<CorpusItem>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var raw = JsonSerializer.Deserialize<RawItem>(line);
            if (raw?.Source is null) continue;

            items.Add(new CorpusItem(
                raw.Id ?? "",
                raw.Category ?? "unknown",
                raw.Source,
                raw.Target ?? raw.Source,
                (raw.Errors ?? [])
                    .Where(e => e.Start >= 0 && e.End > e.Start && e.End <= raw.Source.Length)
                    .Select(e => new CorpusError(e.Start, e.End, e.Replacement ?? "", e.Type ?? ""))
                    .ToList()));
        }

        return items;
    }

    private sealed class RawItem
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("target")] public string? Target { get; set; }
        [JsonPropertyName("errors")] public List<RawError>? Errors { get; set; }
    }

    private sealed class RawError
    {
        [JsonPropertyName("start")] public int Start { get; set; }
        [JsonPropertyName("end")] public int End { get; set; }
        [JsonPropertyName("replacement")] public string? Replacement { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }
}

internal sealed class SelectorStats
{
    public int SelectionAttempts { get; set; }
    public int SelectionCorrect { get; set; }
    public int NoChangeAttempts { get; set; }
    public int NoChangeCorrect { get; set; }
    public int ModelCalls { get; set; }
    public int RejectedAnswers { get; set; }
    public List<double> Latencies { get; } = [];

    public double SelectionAccuracy => SelectionAttempts == 0 ? 0 : (double)SelectionCorrect / SelectionAttempts;
    public double NoChangeAccuracy => NoChangeAttempts == 0 ? 0 : (double)NoChangeCorrect / NoChangeAttempts;
    public double LatencyP50Ms => Percentile(0.50);
    public double LatencyP95Ms => Percentile(0.95);

    private double Percentile(double q)
    {
        if (Latencies.Count == 0) return 0;
        var sorted = Latencies.OrderBy(x => x).ToArray();
        var index = (int)Math.Ceiling(q * sorted.Length) - 1;
        return Math.Round(sorted[Math.Clamp(index, 0, sorted.Length - 1)], 2);
    }
}

internal sealed class JudgeReport
{
    public required string RunUtc { get; init; }
    public int ScoredTargets { get; init; }
    public int ReachableTargets { get; init; }
    public int NoChangeTargets { get; init; }
    public int CandidateGenerationFailures { get; init; }
    public double CandidateGenerationFailureRate { get; init; }
    public required Dictionary<string, SelectorStats> Selectors { get; init; }
    public required Dictionary<string, int> RejectionReasons { get; init; }
    public required List<string> CandidateGenerationFailureExamples { get; init; }
    public required Dictionary<string, int> FailuresByCategory { get; init; }
}

internal static class Evaluate
{
    public static async Task<JudgeReport> RunAsync(
        IReadOnlyList<CorpusItem> items,
        int limit,
        RussianFormIndexLexicon lexicon,
        ContextualCorrectionRefiner? refiner,
        CandidateJudge? judge)
    {
        var deterministic = new SelectorStats();
        var reranked = new SelectorStats();
        var judged = new SelectorStats();
        var rejectionReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var generationFailures = new List<string>();
        var failuresByCategory = new Dictionary<string, int>(StringComparer.Ordinal);

        var scored = 0;
        var reachable = 0;
        var noChangeTargets = 0;

        foreach (var item in items.Take(limit))
        {
            // NO_CHANGE ground truth: a token from an item that must not be touched. One
            // token per item keeps the two accuracies on comparable sample sizes and keeps
            // the AI run inside a sane number of calls.
            if (Corpus.NoChangeCategories.Contains(item.Category))
            {
                var token = LongestWord(item.Source);
                if (token is null) continue;

                var (start, length) = token.Value;
                var target = item.Source.Substring(start, length);
                var candidates = CandidateJudge.BuildCandidates(target, Suggest(lexicon, target));
                if (candidates.Count <= 1)
                {
                    // Nothing was suggested, so every selector trivially leaves it alone.
                    // Counted as correct for all three rather than skipped: refusing to
                    // suggest is how the deterministic layer gets no-change right, and
                    // hiding that would flatter the judge.
                    noChangeTargets++;
                    foreach (var stats in new[] { deterministic, reranked, judged })
                    {
                        stats.NoChangeAttempts++;
                        stats.NoChangeCorrect++;
                    }

                    continue;
                }

                noChangeTargets++;
                ScoreNoChange(deterministic, changed: true);
                ScoreNoChange(
                    reranked,
                    changed: refiner is null
                             || Rerank(refiner, item.Source, start, length, candidates).Count > 0);

                if (judge is not null)
                {
                    var request = new CandidateJudgeRequest(item.Source, start, length, candidates);
                    var verdict = await TimedJudge(judge, request, judged, rejectionReasons);
                    ScoreNoChange(judged, changed: verdict.Outcome == CandidateJudgeOutcome.Selected);
                }

                continue;
            }

            foreach (var error in item.Errors)
            {
                var start = error.Start;
                var length = error.End - error.Start;
                var target = item.Source.Substring(start, length);

                // Only single-token targets are a candidate-ranking question. A gold span
                // covering two words ("пришёл было") is a punctuation decision, which is a
                // different task and is measured elsewhere.
                if (target.Contains(' ')) continue;

                var suggestions = Suggest(lexicon, target);
                var candidates = CandidateJudge.BuildCandidates(target, suggestions);
                scored++;

                var goldOffered = candidates.Any(c =>
                    string.Equals(c.Text, error.Replacement, StringComparison.OrdinalIgnoreCase));
                if (!goldOffered)
                {
                    // §24: no selector can win this. Recorded, not scored.
                    failuresByCategory[item.Category] = failuresByCategory.GetValueOrDefault(item.Category) + 1;
                    if (generationFailures.Count < 40)
                    {
                        generationFailures.Add(
                            $"{item.Id} '{target}' -> '{error.Replacement}' "
                            + $"(offered: {string.Join(", ", candidates.Where(c => c.Text.Length > 0).Select(c => c.Text))})");
                    }

                    continue;
                }

                reachable++;

                deterministic.SelectionAttempts++;
                if (Matches(candidates.FirstOrDefault()?.Text, error.Replacement))
                {
                    deterministic.SelectionCorrect++;
                }

                reranked.SelectionAttempts++;
                var rerankedOrder = refiner is null
                    ? candidates.Where(c => c.Text.Length > 0).Select(c => c.Text).ToList()
                    : Rerank(refiner, item.Source, start, length, candidates);
                if (Matches(rerankedOrder.FirstOrDefault(), error.Replacement))
                {
                    reranked.SelectionCorrect++;
                }

                if (judge is not null)
                {
                    judged.SelectionAttempts++;
                    var request = new CandidateJudgeRequest(item.Source, start, length, candidates);
                    var verdict = await TimedJudge(judge, request, judged, rejectionReasons);

                    // A rejected or unavailable answer falls back to deterministic ranking,
                    // which is what the product would do. Scoring it as a miss would measure
                    // the fallback rather than the judge.
                    var chosen = verdict.Outcome switch
                    {
                        CandidateJudgeOutcome.Selected => verdict.Text,
                        CandidateJudgeOutcome.NoChange => target,
                        _ => candidates.FirstOrDefault()?.Text,
                    };

                    if (Matches(chosen, error.Replacement))
                    {
                        judged.SelectionCorrect++;
                    }
                }
            }
        }

        return new JudgeReport
        {
            RunUtc = DateTime.UtcNow.ToString("O"),
            ScoredTargets = scored,
            ReachableTargets = reachable,
            NoChangeTargets = noChangeTargets,
            CandidateGenerationFailures = scored - reachable,
            CandidateGenerationFailureRate = scored == 0 ? 0 : Math.Round((double)(scored - reachable) / scored, 4),
            Selectors = new Dictionary<string, SelectorStats>(StringComparer.Ordinal)
            {
                ["deterministic"] = deterministic,
                ["reranker"] = reranked,
                ["candidate-judge"] = judged,
            },
            RejectionReasons = rejectionReasons,
            CandidateGenerationFailureExamples = generationFailures,
            FailuresByCategory = failuresByCategory,
        };
    }

    /// <summary>
    /// How long one judgement may take before the harness gives up on it.
    /// </summary>
    /// <remarks>
    /// Generous — the model answers a menu of this size in well under a second — and present
    /// for the same reason langbench has one: the llama.cpp server runs with a single
    /// parallel slot, and a request that never completes holds it forever. The first attempt
    /// at this run wedged on its first call and made no progress for ten minutes. A harness
    /// that can hang produces no number at all, so timeouts are counted and reported.
    /// </remarks>
    private static readonly TimeSpan PerJudgementTimeout = TimeSpan.FromSeconds(20);

    private static async Task<CandidateJudgeVerdict> TimedJudge(
        CandidateJudge judge,
        CandidateJudgeRequest request,
        SelectorStats stats,
        Dictionary<string, int> rejectionReasons)
    {
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(PerJudgementTimeout);
        CandidateJudgeVerdict verdict;
        try
        {
            verdict = await judge.JudgeAsync(request, cts.Token).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            verdict = CandidateJudgeVerdict.NotConsulted("timeout");
        }

        sw.Stop();

        stats.ModelCalls++;
        stats.Latencies.Add(sw.Elapsed.TotalMilliseconds);

        if (verdict.Outcome is CandidateJudgeOutcome.Rejected or CandidateJudgeOutcome.NotConsulted
            && verdict.RejectionReason is { } reason)
        {
            stats.RejectedAnswers++;
            rejectionReasons[reason] = rejectionReasons.GetValueOrDefault(reason) + 1;
        }

        return verdict;
    }

    private static void ScoreNoChange(SelectorStats stats, bool changed)
    {
        stats.NoChangeAttempts++;
        if (!changed)
        {
            stats.NoChangeCorrect++;
        }
    }

    private static List<string> Rerank(
        ContextualCorrectionRefiner refiner,
        string sentence,
        int start,
        int length,
        IReadOnlyList<JudgeCandidate> candidates)
    {
        var replacements = candidates.Where(c => c.Text.Length > 0).Select(c => c.Text).ToList();
        return replacements.Count < 2
            ? replacements
            : refiner.Rerank(sentence, start, length, replacements).ToList();
    }

    private static IReadOnlyList<string> Suggest(RussianFormIndexLexicon lexicon, string target)
        => lexicon.ContainsExact(target) ? [] : lexicon.Suggest(target, 6);

    private static bool Matches(string? candidate, string expected)
        => candidate is not null && string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The longest word in a sentence, as the NO_CHANGE probe target.
    /// </summary>
    /// <remarks>
    /// Longest rather than random: short words are in every lexicon and make the test
    /// trivially easy, while a long word is where a ranker is most tempted to produce
    /// something. Deterministic, so the run repeats.
    /// </remarks>
    private static (int Start, int Length)? LongestWord(string sentence)
    {
        (int Start, int Length)? best = null;
        var i = 0;
        while (i < sentence.Length)
        {
            if (!char.IsLetter(sentence[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < sentence.Length && (char.IsLetter(sentence[i]) || sentence[i] == '-')) i++;
            var length = i - start;
            if (length >= 4 && (best is null || length > best.Value.Length))
            {
                best = (start, length);
            }
        }

        return best;
    }
}
