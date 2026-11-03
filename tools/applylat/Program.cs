using System.IO;
using System.Diagnostics;
using WriteLite.AI.Contracts;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Ai;
using WriteLite.Services.Grammar;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Lexical;
using WriteLite.Services.Rules;
using WriteLite.Services.Settings;
using WriteLite.Services.Spelling;

namespace ApplyLat;

/// <summary>
/// Stage timings for the production analysis stack on a real multi-paragraph text.
/// Mirrors the wiring in App.xaml.cs so the numbers describe what the app actually does.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var path = args.FirstOrDefault(a => !a.StartsWith("--"))
                   ?? Path.Combine("benchmarks", "realtext", "sample-463.txt");
        var noAi = args.Contains("--no-ai");
        var text = await File.ReadAllTextAsync(path);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"text: {path}  chars={text.Length} words={text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length}");

        var settings = new WriteLiteAppSettings();
        if (noAi) settings.WriteAiEnabled = false;

        var swBoot = Stopwatch.StartNew();
        var spell = await Task.Run(() => new LocalSpellChecker());
        Console.WriteLine($"[boot] spell checker         {swBoot.ElapsedMilliseconds,7} ms");

        var dictionary = UserDictionaryService.LoadDefault();
        var ignore = WriteLiteIgnoreService.LoadDefault();
        var signals = LexicalSignalService.TryCreate(spell, dictionary);
        var engine = new WriteLiteLanguageEngine(new WriteLiteLanguageOptions
        {
            EnableEngine = settings.ExtendedChecking,
            HostAddress = WriteLiteLanguageOptions.DefaultHostAddress,
            PreferJavaw = true,
            MaxTextLength = settings.MaxTextLength
        });
        if (settings.ExtendedChecking) engine.StartInBackground();

        var orchestrator = new WriteLiteOrchestratingAnalyzer(
            new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), spell.RussianFormIndex),
            new SpellTextAnalyzer(spell),
            engine,
            dictionary,
            ignore,
            isKnownWord: w => spell.CheckWord(w, SpellingLanguage.Russian).IsKnown,
            lexicalSignals: signals,
            punctuationModelFactory: () => PunctuationModelAnalyzer.TryLoad(formIndex: spell.RussianFormIndex));
        orchestrator.ApplySettings(settings);

        var provider = new LocalAiTextProvider(new LocalAiOptions
        {
            Enabled = settings.LocalAiEnabled,
            Profile = AiModelProfile.Standard,
            ModelDirectory = Path.Combine(AppContext.BaseDirectory, "models", "writelight-qwen"),
            QwenEndpoint = settings.QwenEndpoint,
            PreferQwen = settings.PreferQwen,
            TimeoutSeconds = 90,
            MaxInputChars = settings.AiMaxTextLength,
            MinInputChars = 1
        });
        Console.WriteLine($"[boot] qwen endpoint={provider.QwenEndpoint} available={provider.QwenAvailable} model={provider.ModelVersion}");

        var policy = new LocalAiRoutingPolicy(
            lexicalSignals: signals,
            morphologyGuard: MorphologicalAcceptanceGuard.TryCreate(spell.RussianFormIndex));
        var hybrid = new HybridTextAnalysisService(orchestrator, new AiTextAnalysisService(provider), policy);
        hybrid.ApplySettings(settings);

        // The editor's own "fast" composite — what App.xaml.cs gives the field monitor.
        var fast = new CompositeTextAnalyzer(
            new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), spell.RussianFormIndex),
            new SpellTextAnalyzer(spell) { ContextualRefinementEnabled = false });

        if (args.Contains("--lines") || args.Contains("--explain"))
        {
            // The engine starts in the background; measuring before it is up silently drops
            // every engine finding from the first lines.
            var wait = Stopwatch.StartNew();
            while (settings.ExtendedChecking && !engine.IsReady && wait.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(250);
            }

            Console.WriteLine($"[boot] engine ready={engine.IsReady} after {wait.ElapsedMilliseconds} ms");
        }

        if (args.Contains("--lines"))
        {
            // One sentence per line: "<text>	<expected fixed text or ->". Prints what each line got.
            var hit = 0; var total = 0; var falsePos = 0; var clean = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('#')) continue;
                var parts = raw.Split('\t');
                var line = parts[0];
                var expected = parts.Length > 1 ? parts[1] : null;
                var findings = await orchestrator.AnalyzeAsync(line);
                var fixedText = line;
                foreach (var issue in findings.Where(i => !string.IsNullOrEmpty(i.Replacement)
                             && !i.RuleId.Contains("terminal") && !i.RuleId.Contains("sentence-capital"))
                             .OrderByDescending(i => i.Start))
                {
                    if (issue.Start + issue.Length <= fixedText.Length)
                        fixedText = fixedText[..issue.Start] + issue.Replacement + fixedText[(issue.Start + issue.Length)..];
                }

                var verdict = "";
                if (expected is "-")
                {
                    clean++;
                    var fp = !string.Equals(fixedText, line, StringComparison.Ordinal);
                    if (fp) falsePos++;
                    verdict = fp ? "FALSE-POSITIVE" : "ok-clean";
                }
                else if (expected is not null)
                {
                    total++;
                    var ok = string.Equals(fixedText, expected, StringComparison.Ordinal);
                    if (ok) hit++;
                    verdict = ok ? "FIXED" : "MISSED";
                }

                Console.WriteLine($"{verdict,-15} {line}");
                if (verdict is "MISSED" or "FALSE-POSITIVE")
                {
                    Console.WriteLine($"{"",-15} got: {fixedText}");
                    foreach (var issue in findings) Console.WriteLine($"{"",-17}{issue.RuleId} {Quote(issue.Original)} -> {Quote(issue.Replacement)}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"fixed {hit}/{total}; false positives {falsePos}/{clean}");
            engine.Dispose();
            provider.Dispose();
            return 0;
        }

        if (args.Contains("--explain"))
        {
            var findings = await orchestrator.AnalyzeAsync(text);
            Console.WriteLine();
            Console.WriteLine($"{findings.Count} deterministic findings:");
            foreach (var issue in findings.OrderBy(i => i.Start))
            {
                var auto = issue.CanApplyAutomatically ? "AUTO" : "    ";
                Console.WriteLine(
                    $"  [{auto} c={issue.Confidence:F2}] {issue.RuleId,-42} "
                    + $"{Quote(issue.Original)} -> {Quote(issue.Replacement)}   ({issue.Category})");
            }

            engine.Dispose();
            provider.Dispose();
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("stage timings (cold, then warm):");
        for (var pass = 0; pass < 2; pass++)
        {
            var label = pass == 0 ? "cold" : "warm";

            var sw = Stopwatch.StartNew();
            var fastIssues = await fast.AnalyzeAsync(text);
            var fastMs = sw.ElapsedMilliseconds;

            sw.Restart();
            var localIssues = await orchestrator.AnalyzeAsync(text);
            var localMs = sw.ElapsedMilliseconds;

            sw.Restart();
            long detPublishedMs = -1;
            var detCount = 0;
            var hybridIssues = await hybrid.AnalyzeStagedAsync(text, (lane, issues) =>
            {
                if (lane != AnalysisLane.Deterministic) return;
                detPublishedMs = sw.ElapsedMilliseconds;
                detCount = issues.Count;
            });
            var hybridMs = sw.ElapsedMilliseconds;

            var report = hybrid.LastRoutingReport;
            Console.WriteLine(
                $"  [{label}] fast(rules+spell)={fastMs,6} ms n={fastIssues.Count,3} | " +
                $"deterministic(+LT+punct)={localMs,6} ms n={localIssues.Count,3} | " +
                $"staged-publish={detPublishedMs,6} ms n={detCount,3} | " +
                $"hybrid(+AI)={hybridMs,6} ms n={hybridIssues.Count,3} | " +
                $"ai: sentences={report.Sentences} consulted={report.Consulted} accepted={report.Accepted}");
        }

        engine.Dispose();
        provider.Dispose();
        return 0;
    }

    private static string Quote(string? value)
        => value is null ? "(none)" : "«" + value.ReplaceLineEndings(" ") + "»";
}
