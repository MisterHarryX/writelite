using System.IO;
using WriteLite.AI.Contracts;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Ai;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Lexical;
using WriteLite.Services.Settings;
using WriteLite.Language.Core;
using WriteLite.Services.Grammar;
using WriteLite.Services.Rules;
using WriteLite.Services.Spelling;

namespace LangBench;

/// <summary>
/// Builds the same analyzer graph <c>App.OnStartup</c> builds, with each layer switchable.
/// </summary>
/// <remarks>
/// Two deliberate departures from the app's wiring, both for determinism:
///
/// The personal dictionary and the ignore list are created empty rather than loaded from
/// the user profile. Loading them would make the score depend on whoever last used this
/// machine, and a benchmark that moves when you add a word to your dictionary cannot be
/// used to compare two models.
///
/// The AI layer's debounce is dropped to its floor. The product waits ~1.5 s after typing
/// stops before asking the model, which is right for an editor and would add forty minutes
/// to an 841-item run for no change in what is measured.
/// </remarks>
internal sealed class Harness : IAsyncDisposable
{
    private readonly HybridTextAnalysisService _hybrid;
    private readonly WriteLiteLanguageEngine _engine;
    private readonly LocalAiTextProvider? _ai;
    private readonly LocalSpellChecker _spellChecker;
    private readonly CliOptions _options;
    private readonly string _engineState;
    private readonly PunctuationModelAnalyzer? _punctuation;

    private Harness(
        CliOptions options,
        HybridTextAnalysisService hybrid,
        LocalSpellChecker spellChecker,
        WriteLiteLanguageEngine engine,
        LocalAiTextProvider? ai,
        PunctuationModelAnalyzer? punctuation,
        string engineState)
    {
        _options = options;
        _hybrid = hybrid;
        _spellChecker = spellChecker;
        _engine = engine;
        _ai = ai;
        _punctuation = punctuation;
        _engineState = engineState;
    }

    public static async Task<Harness> CreateAsync(CliOptions options)
    {
        var spellChecker = new LocalSpellChecker();

        var settings = new WriteLiteAppSettings
        {
            CheckingEnabled = true,
            ExtendedChecking = options.UseLanguageTool,

            // AiCheckingEnabled is an alias for this property, so setting one is setting both.
            LocalAiEnabled = options.UseAi,
            AiDebounceMs = 200,
            AiMinTextLength = 1,
            AiMaxTextLength = 12_000,
        };

        // The orchestrator always wants an engine; when the layer is off it gets a disabled
        // one rather than null, which is also how the app models "extended checking off".
        var engine = new WriteLiteLanguageEngine(new WriteLiteLanguageOptions
        {
            EnableEngine = options.UseLanguageTool,
            HostAddress = WriteLiteLanguageOptions.DefaultHostAddress,
            PreferJavaw = true,
        });

        // Start it and wait: an engine that is still booting would score as absent on the
        // first few hundred items and make the run unrepeatable.
        var engineState = options.UseLanguageTool ? await StartEngineAsync(engine) : "off";

        // A layer that is switched off is replaced by an analyzer that finds nothing,
        // rather than by restructuring the graph — so every run exercises the same
        // orchestration, merge and filter code, and the only variable is the evidence
        // flowing into it. That is what makes the differences between runs attributable.
        var orchestrator = new WriteLiteOrchestratingAnalyzer(
            // The form index is passed in exactly as App.OnStartup passes it. Without it the
            // vocative and case-government rules construct as null and silently do nothing —
            // which is what happened for the whole of Phase 7, and is why its benchmark was
            // byte-identical to Phase 6 on rules that had just been added.
            options.UseRules
                ? new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), spellChecker.RussianFormIndex)
                : new SilentAnalyzer(),
            options.UseSpell
                ? new SpellTextAnalyzer(spellChecker) { ContextualRefinementEnabled = options.UseReranker }
                : new SilentAnalyzer(),
            engine,
            UserDictionaryService.CreateEmpty(),
            new WriteLiteIgnoreService(),
            isKnownWord: options.UseLexiconVeto
                ? word => spellChecker.CheckWord(word, SpellingLanguage.Russian).IsKnown
                : null,
            lexicalSignals: options.UseLexicalSignals
                ? LexicalSignalService.TryCreate(spellChecker)
                : null);
        orchestrator.ApplySettings(settings);

        LocalAiTextProvider? ai = null;
        AiTextAnalysisService? aiService = null;
        if (options.UseAi)
        {
            ai = new LocalAiTextProvider(new LocalAiOptions
            {
                Enabled = true,
                Profile = AiModelProfile.Standard,
                PreferQwen = true,
                MinInputChars = 1,
                MaxInputChars = 12_000,
            });

            await ai.WarmupAsync();
            aiService = new AiTextAnalysisService(ai);
        }

        // The routing policy is a layer of its own so "unrestricted Qwen" and "routed Qwen"
        // differ in exactly one variable and stay directly comparable.
        ILocalAiRoutingPolicy? routing = null;
        if (options.UseRouting)
        {
            var routingOptions = new LocalAiRoutingOptions();
            if (!options.UseConfidenceFloor)
            {
                // Category routing without the acceptance bar: everything the model returns
                // for a routed sentence is kept, which isolates what routing alone buys.
                routingOptions.ConfirmThreshold = 0;
                routingOptions.RerankThreshold = 0;
                routingOptions.RuleSupportedThreshold = 0;
                routingOptions.UnsupportedThreshold = 0;
            }

            if (options.UseStrictAcceptance)
            {
                // Configuration E: the model may only confirm or re-rank what a
                // deterministic layer already found. It may never introduce a span of its
                // own. Thresholds above 1.0 are unreachable by construction.
                routingOptions.RuleSupportedThreshold = 2.0;
                routingOptions.UnsupportedThreshold = 2.0;
            }

            routing = new LocalAiRoutingPolicy(
                routingOptions,
                options.UseLexicalSignals ? LexicalSignalService.TryCreate(spellChecker) : null);
        }

        var hybrid = new HybridTextAnalysisService(orchestrator, aiService, routing);
        hybrid.ApplySettings(settings);

        var punctuation = options.UsePunctuationModel ? PunctuationModelAnalyzer.TryLoad() : null;
        if (options.UsePunctuationModel && punctuation is null)
        {
            // Loudly, rather than scoring a run as "the model did not help". An absent
            // artefact and a useless model produce identical numbers otherwise.
            throw new InvalidOperationException(
                "layer 'punct' was requested but no punctuation model is deployed at "
                + "models/writelite-punctuation. Export one with "
                + "ai/scripts/export_punctuation_onnx.py first.");
        }

        return new Harness(options, hybrid, spellChecker, engine, ai, punctuation, engineState);
    }

    private static async Task<string> StartEngineAsync(WriteLiteLanguageEngine engine)
    {
        engine.StartInBackground();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (engine.EngineState is WriteLiteLanguageEngineState.Ready
                or WriteLiteLanguageEngineState.Busy)
            {
                return "ready";
            }

            if (engine.EngineState is WriteLiteLanguageEngineState.Failed
                or WriteLiteLanguageEngineState.Unavailable
                or WriteLiteLanguageEngineState.Disabled)
            {
                return $"failed({engine.EngineState})";
            }

            await Task.Delay(250);
        }

        return "timeout";
    }

    /// <summary>
    /// How long one item may take before the harness gives up on it.
    /// </summary>
    /// <remarks>
    /// Generous against the deterministic layers (p95 is single-digit milliseconds
    /// without LanguageTool, ~150 ms with it) and still generous against the local
    /// model, which answers a benchmark sentence in a few seconds.
    ///
    /// It exists because on 2026-08-12 a full-pipeline run wedged permanently: the
    /// llama.cpp server ships with one parallel slot, an abandoned request left the
    /// socket in CLOSE_WAIT holding that slot, and every later request queued behind
    /// a connection that would never complete. The server kept answering /health the
    /// whole time. A benchmark that can hang forever produces no number at all, so
    /// timeouts are counted and reported instead.
    /// </remarks>
    public static readonly TimeSpan PerItemTimeout = TimeSpan.FromSeconds(30);

    public int TimeoutCount { get; private set; }

    /// <summary>Routing counters, for the §18 metrics block.</summary>
    public AiRoutingMetrics RoutingMetrics => _hybrid.Metrics;

    /// <summary>Findings contributed by the punctuation model alone, for attribution.</summary>
    public int PunctuationModelFindings { get; private set; }

    public async Task<IReadOnlyList<TextIssue>> AnalyzeAsync(string text)
    {
        using var cts = new CancellationTokenSource(PerItemTimeout);
        IReadOnlyList<TextIssue> issues;
        try
        {
            issues = await _hybrid.AnalyzeAsync(text, cts.Token).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            TimeoutCount++;
            _hybrid.CancelPending();
            return [];
        }

        if (_punctuation is null) return issues;

        // Appended after the deterministic pass rather than merged into it, so the model can
        // see what the other layers already claimed and stand down where they did.
        var extra = _punctuation.Analyze(text, issues, [], cts.Token);
        if (extra.Count == 0) return issues;

        PunctuationModelFindings += extra.Count;
        return issues.Concat(extra).OrderBy(i => i.Start).ToList();
    }

    public string Describe()
    {
        var parts = new List<string>
        {
            $"rules={(_options.UseRules ? "on" : "off")}",
            $"spell={(_options.UseSpell ? _spellChecker.Stats.Source : "off")}",
            $"lt={(_options.UseLanguageTool ? _engineState : "off")}",
            $"rerank={(_options.UseReranker ? "on" : "off")}",
            $"lexveto={(_options.UseLexiconVeto ? "on" : "off")}",
            $"lexsignals={(_options.UseLexicalSignals ? "on" : "off")}",
            $"punct={(_punctuation is null ? "off" : _punctuation.ModelVersion + "@" + _punctuation.Threshold.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))}",
            $"route={(_options.UseRouting ? "on" : "off")}",
            $"confidence={(_options.UseConfidenceFloor ? "on" : "off")}",
            $"strict={(_options.UseStrictAcceptance ? "on" : "off")}",
        };

        if (_options.UseAi && _ai is not null)
        {
            parts.Add($"ai={_ai.ModelVersion}");
            parts.Add($"qwen={(_ai.QwenAvailable ? "available" : "unavailable")}");
        }
        else
        {
            parts.Add("ai=off");
        }

        return string.Join(" ", parts);
    }

    public async ValueTask DisposeAsync()
    {
        _hybrid.Dispose();
        if (_ai is not null)
        {
            await _ai.DisposeAsync();
        }

        _engine.Dispose();
        _spellChecker.Dispose();
    }
}

/// <summary>Stands in for a switched-off analyzer layer. Finds nothing, ever.</summary>
internal sealed class SilentAnalyzer : ITextAnalyzer
{
    public IReadOnlyList<TextIssue> Analyze(string text) => [];
}

/// <summary>Command line for the harness.</summary>
internal sealed class CliOptions
{
    public required string CorpusPath { get; init; }

    public required string OutputPath { get; init; }

    public required IReadOnlyList<string> Layers { get; init; }

    public required string Label { get; init; }

    public bool UseRules => Layers.Contains("rules");

    public bool UseSpell => Layers.Contains("spell");

    /// <summary>
    /// The contextual reranker. Opt-in so that "before wiring it in" stays reproducible
    /// with current binaries — otherwise a before/after table would have to compare two
    /// different builds, and any change to the harness itself would contaminate it.
    /// </summary>
    public bool UseReranker => Layers.Contains("rerank");

    /// <summary>
    /// Lets WriteLite's own lexicon overrule a LanguageTool spelling claim. Opt-in so the
    /// slang/profanity false-positive cost can be measured with and without it.
    /// </summary>
    public bool UseLexiconVeto => Layers.Contains("lexveto");

    /// <summary>
    /// Register-aware lexical signals — slang, obscenity, names, abbreviations, borrowings —
    /// participating in whether an engine finding survives. Opt-in so its effect is
    /// measurable separately from the plain existence veto.
    /// </summary>
    public bool UseLexicalSignals => Layers.Contains("lexsignals");

    /// <summary>
    /// The Phase 6 trained punctuation classifier, as an additional findings layer.
    /// </summary>
    /// <remarks>
    /// Opt-in like every other layer, so "deterministic" and "deterministic + punctuation
    /// model" differ in exactly one variable and the difference between the two reports is
    /// what the model bought. A model that cannot be switched off cannot be measured.
    /// </remarks>
    public bool UsePunctuationModel => Layers.Contains("punct");

    /// <summary>Selective category/uncertainty routing of AI consultation.</summary>
    public bool UseRouting => Layers.Contains("route");

    /// <summary>The acceptance confidence floor and support-class policy.</summary>
    public bool UseConfidenceFloor => Layers.Contains("confidence");

    /// <summary>Accept only AI findings that confirm or re-rank a deterministic finding.</summary>
    public bool UseStrictAcceptance => Layers.Contains("strict");

    public bool UseLanguageTool => Layers.Contains("lt");

    public bool UseAi => Layers.Contains("ai");

    public static CliOptions? Parse(string[] args, string root)
    {
        var layers = new List<string> { "rules", "spell", "lt" };
        string? corpus = null;
        string? output = null;
        string? label = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--layers" when i + 1 < args.Length:
                    layers = args[++i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(x => x.ToLowerInvariant())
                        .ToList();
                    break;
                case "--corpus" when i + 1 < args.Length:
                    corpus = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--label" when i + 1 < args.Length:
                    label = args[++i];
                    break;
                default:
                    return null;
            }
        }

        var unknown = layers.Where(l => l is not ("rules" or "spell" or "lt" or "rerank" or "lexveto" or "lexsignals" or "route" or "confidence" or "strict" or "ai" or "punct")).ToList();
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"unknown layer(s): {string.Join(",", unknown)}");
            return null;
        }

        if (layers.Count == 0)
        {
            Console.Error.WriteLine("at least one layer is required");
            return null;
        }

        label ??= string.Join("+", layers);

        return new CliOptions
        {
            CorpusPath = corpus is null
                ? Path.Combine(root, "ai", "data", "benchmark", "ru_frozen_v1.jsonl")
                : Path.GetFullPath(corpus),
            OutputPath = output is null
                ? Path.Combine(root, "benchmarks", "results", $"{Sanitise(label)}.json")
                : Path.GetFullPath(output),
            Layers = layers,
            Label = label,
        };
    }

    private static string Sanitise(string name)
        => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
}
