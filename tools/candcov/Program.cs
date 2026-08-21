using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WriteLite.Language.Russian;
using WriteLite.Harness;
using WriteLite.Services.Spelling;

namespace CandCov;

/// <summary>
/// Measures candidate <em>generation</em>, separately from candidate ranking.
/// </summary>
/// <remarks>
/// <para>Phase 5 established that 35.1 % of single-token targets never have the right answer
/// on the menu, and that no selector — deterministic, reranker or model — can reach them.
/// <c>judgebench</c> found that number as a by-product of comparing selectors; this tool
/// exists to measure it directly, on any corpus, before and after a change to the generator,
/// which is what §5 asks for.</para>
///
/// <para>Coverage on its own is a number anyone can raise by offering more candidates, so it
/// is never reported alone. Three counters travel with it and are the reason the tool is
/// worth having:</para>
/// <list type="bullet">
/// <item><b>candidates per target</b> (mean and p95) — the cost side of coverage.</item>
/// <item><b>offer rate on correct tokens</b> — how often the generator produces anything at
/// all for a word that is already right. This is the false-candidate explosion §5 names, and
/// it is the metric that a naive "suggest alternatives for every valid word" change
/// destroys.</item>
/// <item><b>per-class coverage</b> — an aggregate hides exactly the classes that need work.</item>
/// </list>
///
///   candcov [--corpus path] [--split name] [--limit n] [--out path] [--label name]
///           [--layers spell|spell+morph] [--dump-misses path]
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var root = FindRepoRoot();

        var corpusPath = Path.Combine(root, "ai", "data", "errors", "validation.jsonl");
        var outputPath = Path.Combine(root, "benchmarks", "results", "candcov.json");
        string? dumpPath = null;
        var label = "candcov";
        var limit = int.MaxValue;
        var withMorphology = false;
        var withParadigm = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--corpus" when i + 1 < args.Length: corpusPath = Path.GetFullPath(args[++i]); break;
                case "--out" when i + 1 < args.Length: outputPath = Path.GetFullPath(args[++i]); break;
                case "--dump-misses" when i + 1 < args.Length: dumpPath = Path.GetFullPath(args[++i]); break;
                case "--label" when i + 1 < args.Length: label = args[++i]; break;
                case "--limit" when i + 1 < args.Length: limit = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--layers" when i + 1 < args.Length:
                {
                    var layers = args[++i];
                    withMorphology = layers.Contains("morph", StringComparison.Ordinal);
                    withParadigm = layers.Contains("paradigm", StringComparison.Ordinal);
                    break;
                }

                default:
                    Console.Error.WriteLine(
                        "usage: candcov [--corpus path] [--limit n] [--out path] [--label name] "
                        + "[--layers spell|spell+morph|spell+morph+paradigm] [--dump-misses path]");
                    return 2;
            }
        }

        if (!File.Exists(corpusPath))
        {
            Console.Error.WriteLine($"corpus not found: {corpusPath}");
            return 1;
        }

        using var lexicon = RussianFormIndexLexicon.TryLoad();
        if (lexicon is null)
        {
            Console.Error.WriteLine("Russian form index is not deployed; nothing to measure.");
            return 1;
        }

        var items = Corpus.Load(corpusPath);
        var layerName = "spell" + (withMorphology ? "+morph" : "") + (withParadigm ? "+paradigm" : "");
        Console.WriteLine($"corpus : {corpusPath} ({items.Count} items)");
        Console.WriteLine($"layers : {layerName}");
        Console.WriteLine();

        var report = Measure(items, limit, lexicon, withMorphology, withParadigm, layerName, label, corpusPath, dumpPath);

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

        Print(report, outputPath);
        return 0;
    }

    private static CoverageReport Measure(
        IReadOnlyList<CovItem> items,
        int limit,
        RussianFormIndexLexicon lexicon,
        bool withMorphology,
        bool withParadigm,
        string layerName,
        string label,
        string corpusPath,
        string? dumpPath)
    {
        var index = lexicon.Index;
        var perClass = new Dictionary<string, ClassStats>(StringComparer.Ordinal);
        var perCorpusCategory = new Dictionary<string, ClassStats>(StringComparer.Ordinal);
        var counts = new List<int>();
        var misses = new List<string>();

        var scored = 0;
        var covered = 0;
        var casingOnly = 0;

        // Correct-token probe. Every word of every sentence that carries no error span, run
        // through the same generator, so the cost of widening is measured on the text a user
        // actually writes rather than only on the errors.
        var cleanTokens = 0;
        var cleanTokensOffered = 0;
        var cleanOfferCounts = new List<int>();

        foreach (var item in items.Take(limit))
        {
            foreach (var error in item.Errors)
            {
                var target = error.Original;
                var expected = error.Expected;
                if (target.Contains(' ') || !target.Any(char.IsLetter)) continue;
                if (expected.Contains(' ')) continue;

                // A word that differs from its answer only in capitalisation is not a
                // candidate-generation question at all — no list of alternative *words* can
                // contain it, and the pipeline fixes it in a separate casing pass. Counted so
                // the exclusion is visible, and kept out of the coverage denominator so it
                // cannot depress a number about a different mechanism.
                if (string.Equals(target, expected, StringComparison.OrdinalIgnoreCase))
                {
                    casingOnly++;
                    continue;
                }

                scored++;
                var candidates = Candidates(lexicon, target, withMorphology, withParadigm);
                counts.Add(candidates.Count);

                var hit = candidates.Contains(expected, StringComparer.OrdinalIgnoreCase);
                if (hit) covered++;

                var klass = Classify.Of(index, target, expected);
                Bump(perClass, klass, hit, candidates.Count);
                Bump(perCorpusCategory, CorpusLabel(item, error), hit, candidates.Count);

                if (!hit && misses.Count < 400)
                {
                    misses.Add(
                        $"{item.Id}\t{klass}\t{CorpusLabel(item, error)}\t'{target}' -> '{expected}'"
                        + $"\toffered: {(candidates.Count == 0 ? "nothing" : string.Join(", ", candidates))}");
                }
            }

            if (item.Errors.Count != 0) continue;

            foreach (var token in Words(item.Source))
            {
                cleanTokens++;
                var offered = Candidates(lexicon, token, withMorphology, withParadigm);
                cleanOfferCounts.Add(offered.Count);
                if (offered.Count > 0) cleanTokensOffered++;
            }
        }

        if (dumpPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dumpPath)!);
            File.WriteAllLines(dumpPath, misses, new UTF8Encoding(false));
        }

        return new CoverageReport
        {
            RunUtc = DateTime.UtcNow.ToString("O"),
            Label = label,
            Corpus = corpusPath,
            Layers = layerName,
            ScoredTargets = scored,
            CoveredTargets = covered,
            Coverage = scored == 0 ? 0 : Math.Round((double)covered / scored, 4),
            CasingOnlyTargetsExcluded = casingOnly,
            MeanCandidatesPerTarget = counts.Count == 0 ? 0 : Math.Round(counts.Average(), 2),
            P95CandidatesPerTarget = Percentile(counts, 0.95),
            MaxCandidatesPerTarget = counts.Count == 0 ? 0 : counts.Max(),
            CleanTokensProbed = cleanTokens,
            CleanTokensOffered = cleanTokensOffered,
            CleanTokenOfferRate = cleanTokens == 0 ? 0 : Math.Round((double)cleanTokensOffered / cleanTokens, 5),
            MeanCandidatesPerCleanToken = cleanOfferCounts.Count == 0 ? 0 : Math.Round(cleanOfferCounts.Average(), 3),
            PerClass = perClass.OrderByDescending(kv => kv.Value.Targets)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            PerCorpusCategory = perCorpusCategory.OrderByDescending(kv => kv.Value.Targets)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// Exactly what the product offers for this token, as the ranking path sees it.
    /// </summary>
    /// <remarks>
    /// A word the index knows is not a spelling error, so the spelling generator is never
    /// asked about it and returns nothing — which is how the pipeline gets its 0.994
    /// no-change accuracy for free, and simultaneously why тся/ться is unreachable. The
    /// <c>morph</c> layer is the only thing allowed to answer for a valid word.
    /// </remarks>
    private static IReadOnlyList<string> Candidates(
        RussianFormIndexLexicon lexicon,
        string target,
        bool withMorphology,
        bool withParadigm)
    {
        var known = lexicon.ContainsExact(target);
        IReadOnlyList<string> spelling = known ? [] : lexicon.Suggest(target, 6);
        var merged = new List<string>(spelling);

        void Add(IEnumerable<string> more)
        {
            foreach (var item in more)
            {
                if (!merged.Contains(item, StringComparer.OrdinalIgnoreCase)) merged.Add(item);
            }
        }

        if (withMorphology) Add(lexicon.ContextualAlternatives(target));
        if (withParadigm && known) Add(ParadigmNeighbours(lexicon.Index, target));
        return merged;
    }

    /// <summary>
    /// Every other form of the same lemma within two edits — the unbounded widening, measured
    /// so that declining it is a result rather than an opinion.
    /// </summary>
    /// <remarks>
    /// <para>This is the only way to reach the case/agreement/verb-form misses: «языками» and
    /// «язык» are both correct Russian and differ by more than orthography, so the sentence is
    /// the only thing that separates them. Reaching them means questioning every inflected
    /// content word in every sentence a user writes, which is why the tool measures the offer
    /// rate on correct tokens in the same pass.</para>
    ///
    /// <para>Lives in the harness rather than in the product deliberately. Phase 5's real-word
    /// gate already recorded a similar widening as "6× the candidates, more false positives,
    /// no extra recall"; shipping a second one on the strength of a coverage number alone
    /// would repeat that. If the numbers said otherwise it belongs in
    /// <c>RussianMorphologyAlternatives</c>, not here.</para>
    /// </remarks>
    private static IEnumerable<string> ParadigmNeighbours(RussianFormIndex index, string word)
    {
        var lower = word.ToLowerInvariant();
        var info = index.GetInfo(lower);
        if (info.Lemma.Length == 0) yield break;

        foreach (var match in index.FindWithin(lower, 2, 4096))
        {
            if (string.Equals(match.Word, lower, StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = index.GetInfo(match.Word);
            if (!string.Equals(candidate.Lemma, info.Lemma, StringComparison.OrdinalIgnoreCase)) continue;
            yield return match.Word;
        }
    }

    private static string CorpusLabel(CovItem item, CovError error)
        => item.Category != "unknown" ? item.Category : error.Type;

    private static void Bump(Dictionary<string, ClassStats> map, string key, bool hit, int candidates)
    {
        if (!map.TryGetValue(key, out var stats))
        {
            stats = new ClassStats();
            map[key] = stats;
        }

        stats.Targets++;
        if (hit) stats.Covered++;
        stats.CandidateTotal += candidates;
    }

    private static IEnumerable<string> Words(string sentence)
    {
        var i = 0;
        while (i < sentence.Length)
        {
            if (!char.IsLetter(sentence[i])) { i++; continue; }
            var start = i;
            while (i < sentence.Length && (char.IsLetter(sentence[i]) || sentence[i] == '-')) i++;
            if (i - start >= 2) yield return sentence[start..i];
        }
    }

    private static int Percentile(List<int> values, double q)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToArray();
        var idx = (int)Math.Ceiling(q * sorted.Length) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }

    private static void Print(CoverageReport r, string outputPath)
    {
        Console.WriteLine($"targets scored          : {r.ScoredTargets}  (casing-only excluded: {r.CasingOnlyTargetsExcluded})");
        Console.WriteLine($"gold answer generated   : {r.CoveredTargets} ({r.Coverage:P1})");
        Console.WriteLine($"candidates / target     : mean {r.MeanCandidatesPerTarget:F2}  p95 {r.P95CandidatesPerTarget}  max {r.MaxCandidatesPerTarget}");
        Console.WriteLine($"correct tokens probed   : {r.CleanTokensProbed}");
        Console.WriteLine($"  offered anything      : {r.CleanTokensOffered} ({r.CleanTokenOfferRate:P3})");
        Console.WriteLine($"  candidates / token    : {r.MeanCandidatesPerCleanToken:F3}");
        Console.WriteLine();
        Console.WriteLine($"{"class",-14} {"n",5} {"covered",8} {"coverage",9} {"cand/tgt",9}");
        foreach (var (name, s) in r.PerClass)
        {
            Console.WriteLine($"{name,-14} {s.Targets,5} {s.Covered,8} {s.Coverage,9:F3} {s.MeanCandidates,9:F2}");
        }

        Console.WriteLine();
        Console.WriteLine($"{"corpus label",-20} {"n",5} {"covered",8} {"coverage",9}");
        foreach (var (name, s) in r.PerCorpusCategory)
        {
            Console.WriteLine($"{name,-20} {s.Targets,5} {s.Covered,8} {s.Coverage,9:F3}");
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

internal sealed class ClassStats
{
    public int Targets { get; set; }
    public int Covered { get; set; }
    public int CandidateTotal { get; set; }

    public double Coverage => Targets == 0 ? 0 : Math.Round((double)Covered / Targets, 4);
    public double MeanCandidates => Targets == 0 ? 0 : Math.Round((double)CandidateTotal / Targets, 2);
}

internal sealed class CoverageReport
{
    public required string RunUtc { get; init; }
    public required string Label { get; init; }
    public required string Corpus { get; init; }
    public required string Layers { get; init; }
    public int ScoredTargets { get; init; }
    public int CoveredTargets { get; init; }
    public double Coverage { get; init; }
    public int CasingOnlyTargetsExcluded { get; init; }
    public double MeanCandidatesPerTarget { get; init; }
    public int P95CandidatesPerTarget { get; init; }
    public int MaxCandidatesPerTarget { get; init; }
    public int CleanTokensProbed { get; init; }
    public int CleanTokensOffered { get; init; }
    public double CleanTokenOfferRate { get; init; }
    public double MeanCandidatesPerCleanToken { get; init; }
    public required Dictionary<string, ClassStats> PerClass { get; init; }
    public required Dictionary<string, ClassStats> PerCorpusCategory { get; init; }
}
