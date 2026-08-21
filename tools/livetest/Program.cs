using System.IO;
using System.Text;
using System.Text.Json;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Grammar;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Lexical;
using WriteLite.Services.Rules;
using WriteLite.Services.Settings;
using WriteLite.Services.Spelling;
using WriteLite.Services.Stylistics;

// Runs the exact shipping analyzer graph over realistic paragraphs and prints what a user
// would actually see. Phase 7.5 §52.
//
// This exists because the frozen golden corpus is sentence-local and, as it turns out,
// contains zero vocative and zero dative-government items — so the aggregate benchmark
// stayed green while a realistic paragraph went mostly untouched. A number that cannot move
// when the product fails is not a measurement of the product.
namespace LiveTest;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var corpusPath = Arg(args, "--corpus")
            ?? Path.Combine(RepoRoot(), "benchmarks", "realworld-ru-v1", "paragraphs.jsonl");
        var outputPath = Arg(args, "--out");
        var useLanguageTool = !args.Contains("--no-lt");
        var usePunctuationModel = !args.Contains("--no-punct");

        if (!File.Exists(corpusPath))
        {
            Console.Error.WriteLine($"corpus not found: {corpusPath}");
            return 2;
        }

        var items = ReadItems(corpusPath).ToList();
        Console.WriteLine($"corpus : {corpusPath} ({items.Count} paragraphs)");

        await using var pipeline = await Pipeline.CreateAsync(useLanguageTool, usePunctuationModel);
        Console.WriteLine($"engine : {pipeline.Describe()}");
        Console.WriteLine();

        var report = new ScoreCard();
        foreach (var item in items)
        {
            var findings = await pipeline.AnalyzeAsync(item.Text);
            report.Add(item, findings);
        }

        report.Print();

        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            File.WriteAllText(
                outputPath,
                JsonSerializer.Serialize(report.ToJson(), new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
            Console.WriteLine($"report : {Path.GetFullPath(outputPath)}");
        }

        return report.HasDangerousChanges ? 1 : 0;
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WriteLite.sln"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private static IEnumerable<Item> ReadItems(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var item = JsonSerializer.Deserialize<Item>(line, JsonOptions);
            if (item is not null) yield return item;
        }
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

internal sealed record Item
{
    public string Id { get; init; } = "";
    public string Category { get; init; } = "";
    public string Text { get; init; } = "";
    public List<Expected> Expected { get; init; } = [];

    /// <summary>Corrections that must never be produced. §38 semantic safety.</summary>
    public List<Forbidden> Forbidden { get; init; } = [];
}

/// <summary>
/// One thing a competent Russian editor would flag, and what an acceptable fix looks like.
/// </summary>
/// <remarks>
/// <paramref name="Replacements"/> is a list because style has no single right answer (§35)
/// and because punctuation often has more than one defensible span shape. A finding counts as
/// matching when it overlaps <see cref="Original"/> and, for non-style categories, produces
/// one of the accepted replacements.
/// </remarks>
internal sealed record Expected
{
    public string Original { get; init; } = "";
    public List<string> Replacements { get; init; } = [];
    public string Category { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Why { get; init; } = "";
}

internal sealed record Forbidden
{
    public string Original { get; init; } = "";
    public string Replacement { get; init; } = "";
    public string Reason { get; init; } = "";
}

/// <summary>The shipping analyzer graph, wired exactly as <c>App.OnStartup</c> wires it.</summary>
internal sealed class Pipeline : IAsyncDisposable
{
    private readonly WriteLiteOrchestratingAnalyzer _orchestrator;
    private readonly WriteLiteLanguageEngine _engine;
    private readonly LocalSpellChecker _spellChecker;
    private readonly string _engineState;
    private readonly bool _punctuation;

    private Pipeline(
        WriteLiteOrchestratingAnalyzer orchestrator,
        WriteLiteLanguageEngine engine,
        LocalSpellChecker spellChecker,
        string engineState,
        bool punctuation)
    {
        _orchestrator = orchestrator;
        _engine = engine;
        _spellChecker = spellChecker;
        _engineState = engineState;
        _punctuation = punctuation;
    }

    public static async Task<Pipeline> CreateAsync(bool useLanguageTool, bool usePunctuationModel)
    {
        var spellChecker = new LocalSpellChecker();
        var settings = new WriteLiteAppSettings
        {
            CheckingEnabled = true,
            ExtendedChecking = useLanguageTool,
            PunctuationModelEnabled = usePunctuationModel,
            WriteAiEnabled = false,
        };

        var engine = new WriteLiteLanguageEngine(new WriteLiteLanguageOptions
        {
            EnableEngine = useLanguageTool,
            HostAddress = WriteLiteLanguageOptions.DefaultHostAddress,
            PreferJavaw = true,
        });

        var engineState = "off";
        if (useLanguageTool)
        {
            engine.StartInBackground();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                if (engine.EngineState is WriteLiteLanguageEngineState.Ready
                    or WriteLiteLanguageEngineState.Busy) { engineState = "ready"; break; }
                if (engine.EngineState is WriteLiteLanguageEngineState.Failed
                    or WriteLiteLanguageEngineState.Unavailable
                    or WriteLiteLanguageEngineState.Disabled)
                { engineState = $"failed({engine.EngineState})"; break; }
                await Task.Delay(250);
            }

            if (engineState == "off") engineState = "timeout";
        }

        var orchestrator = new WriteLiteOrchestratingAnalyzer(
            new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), spellChecker.RussianFormIndex),
            new SpellTextAnalyzer(spellChecker),
            engine,
            UserDictionaryService.CreateEmpty(),
            new WriteLiteIgnoreService(),
            isKnownWord: word => spellChecker.CheckWord(word, SpellingLanguage.Russian).IsKnown,
            lexicalSignals: LexicalSignalService.TryCreate(spellChecker),
            punctuationModelFactory: usePunctuationModel
                ? () => PunctuationModelAnalyzer.TryLoad()
                : null);
        orchestrator.ApplySettings(settings);

        return new Pipeline(orchestrator, engine, spellChecker, engineState, usePunctuationModel);
    }

    public string Describe()
        => $"rules=on spell=on lt={_engineState} punct={(_punctuation ? "PUNC-B@0.996" : "off")} style=on writeai=off";

    public async Task<IReadOnlyList<TextIssue>> AnalyzeAsync(string text)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await _orchestrator.AnalyzeAsync(text, cts.Token);
    }

    public ValueTask DisposeAsync()
    {
        _orchestrator.Dispose();
        _engine.Dispose();
        _spellChecker.Dispose();
        return ValueTask.CompletedTask;
    }
}
