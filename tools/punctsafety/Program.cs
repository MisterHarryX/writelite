using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using WriteLite.AI.Local;
using WriteLite.Models;
using WriteLite.Services.Grammar;

namespace PunctSafety;

/// <summary>
/// Asks the punctuation model to look at correctly punctuated text and counts how often it
/// wants to change it.
/// </summary>
/// <remarks>
/// <para>A comma model can reach a good F1 by inserting commas liberally, and the frozen
/// benchmark's clean half — 277 items of ordinary prose — will not necessarily catch it,
/// because ordinary prose is the easy case. The shapes below are the hard ones, and each is
/// here because it is a plausible way for a Russian comma classifier to be wrong on text a
/// real user writes.</para>
///
/// <para>The measurement is one number per category: <b>how many boundaries in already-correct
/// text the model proposes a comma at</b>. The right answer for every sentence here is zero,
/// so this is a false-positive probe with no true positives to trade against — which is the
/// point. Recall is measured elsewhere; this measures only what it costs.</para>
///
/// <para>Runs through <see cref="PunctuationModelAnalyzer"/> and therefore through the ONNX
/// artefact, the C# tokenizer and the frozen threshold — the deployed path, not the training
/// one. A discrepancy between the two would show up here as a category that misbehaves for no
/// linguistic reason.</para>
///
///   punctsafety [--threshold t] [--model dir] [--out path]
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var root = FindRepoRoot();
        var outputPath = Path.Combine(root, "benchmarks", "results", "P6-punct-safety.json");
        string? modelDir = null;
        double? threshold = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model" when i + 1 < args.Length: modelDir = Path.GetFullPath(args[++i]); break;
                case "--out" when i + 1 < args.Length: outputPath = Path.GetFullPath(args[++i]); break;
                case "--threshold" when i + 1 < args.Length:
                    threshold = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                default:
                    Console.Error.WriteLine("usage: punctsafety [--threshold t] [--model dir] [--out path]");
                    return 2;
            }
        }

        using var analyzer = PunctuationModelAnalyzer.TryLoad(modelDir, threshold);
        if (analyzer is null)
        {
            Console.Error.WriteLine("no punctuation model deployed; nothing to probe.");
            return 1;
        }

        Console.WriteLine($"model     : {analyzer.ModelVersion}");
        Console.WriteLine($"threshold : {analyzer.Threshold:F4}");
        Console.WriteLine($"load      : {analyzer.LoadTime.TotalMilliseconds:F0} ms");
        Console.WriteLine();

        var results = new List<CategoryResult>();
        var offenders = new List<string>();
        var totalBoundaries = 0;
        var totalProposals = 0;

        foreach (var (category, sentences) in Probe.Corpus)
        {
            var boundaries = 0;
            var proposals = 0;
            foreach (var sentence in sentences)
            {
                boundaries += PunctuationDecisionModel.CandidateBoundaries(sentence).Count;
                var findings = analyzer.Analyze(sentence, [], []);
                proposals += findings.Count;
                foreach (var finding in findings)
                {
                    offenders.Add($"[{category}] {sentence}  ==>  «{finding.Original}» + ',' "
                                  + $"(p={finding.Confidence:F3})");
                }
            }

            totalBoundaries += boundaries;
            totalProposals += proposals;
            results.Add(new CategoryResult(category, sentences.Length, boundaries, proposals,
                boundaries == 0 ? 0 : Math.Round((double)proposals / boundaries, 4)));
        }

        Console.WriteLine($"{"category",-22} {"sent",5} {"bounds",7} {"proposed",9} {"rate",7}");
        foreach (var r in results)
        {
            Console.WriteLine($"{r.Category,-22} {r.Sentences,5} {r.Boundaries,7} {r.Proposals,9} {r.Rate,7:F4}");
        }

        Console.WriteLine(new string('-', 55));
        Console.WriteLine($"{"TOTAL",-22} {Probe.Corpus.Sum(c => c.Sentences.Length),5} "
                          + $"{totalBoundaries,7} {totalProposals,9} "
                          + $"{(totalBoundaries == 0 ? 0 : (double)totalProposals / totalBoundaries),7:F4}");

        if (offenders.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("— every comma proposed into correct text —");
            foreach (var line in offenders.Take(40)) Console.WriteLine("  " + line);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(new
            {
                RunUtc = DateTime.UtcNow.ToString("O"),
                Model = analyzer.ModelVersion,
                analyzer.Threshold,
                TotalBoundaries = totalBoundaries,
                TotalProposals = totalProposals,
                OverallRate = totalBoundaries == 0 ? 0 : Math.Round((double)totalProposals / totalBoundaries, 4),
                PerCategory = results,
                Offenders = offenders,
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }),
            new UTF8Encoding(false));

        Console.WriteLine();
        Console.WriteLine($"report : {outputPath}");
        return 0;
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

internal sealed record CategoryResult(
    string Category,
    int Sentences,
    int Boundaries,
    int Proposals,
    double Rate);
