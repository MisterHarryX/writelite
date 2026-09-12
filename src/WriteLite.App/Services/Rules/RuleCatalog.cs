using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WriteLite.Services.Rules;

public sealed class RuleCatalog
{
    private static readonly Regex RuleIdPattern = new(
        @"^ru\.[a-z0-9]+(?:[.-][a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// How long a match against a rule's own example may run before the pattern is called bad.
    /// Generous on purpose — see <see cref="ValidateRegexTests"/>.
    /// </summary>
    private static readonly TimeSpan ValidationMatchTimeout = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyDictionary<string, RuleDefinition> _rules;
    private readonly IReadOnlyList<CompiledRuleDefinition> _regexRules;

    private RuleCatalog(
        IReadOnlyDictionary<string, RuleDefinition> rules,
        IReadOnlyList<CompiledRuleDefinition> regexRules,
        string packVersion)
    {
        _rules = rules;
        _regexRules = regexRules;
        PackVersion = packVersion;
    }

    public string PackVersion { get; }
    public IReadOnlyCollection<RuleDefinition> Rules => _rules.Values.ToArray();
    public IReadOnlyList<CompiledRuleDefinition> RegexRules => _regexRules;

    /// <summary>
    /// The Russian rule pack that ships with the application, built once per process.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it is cached.</b> A catalog is immutable — a dictionary of records and a
    /// list of compiled patterns, none of which any caller can change — so two callers asking
    /// for the shipped pack want the same object. Startup asked twice: once for the
    /// orchestrator's rule analyzer and once for the monitor's fast analyzer. Measured with
    /// <c>tools/startlat</c>, one build costs 462 ms cold and 177 ms warm, almost all of it
    /// compiling 78 patterns with <see cref="RegexOptions.Compiled"/>. The second build was
    /// therefore ~180 ms of the path to the first window spent producing a value the process
    /// was already holding.</para>
    ///
    /// <para><b>Why the embedded tests do not run here.</b> They are how the pack proves
    /// itself, and that is a property of the pack rather than of this launch: the JSON ships
    /// inside the application and cannot differ between the run that proved it and the run
    /// that uses it. <c>RussianRulePackTests</c> calls
    /// <see cref="LoadFromDirectory(string)"/>, which still runs all 265 of them, so a pattern
    /// that stops matching its own example fails the build. Running them at startup also made
    /// a loaded machine able to fail the launch: the 100 ms match timeout exists to stop a
    /// pathological *user input* from hanging the analyzer, and on a busy machine it fired
    /// against a thirty-character test string instead. <see cref="RegexMatchTimeoutException"/>
    /// is neither an <see cref="ArgumentException"/> nor caught anywhere above this call, so
    /// that would have been an unhandled exception before the first window.</para>
    /// </remarks>
    private static readonly Lazy<RuleCatalog> ShippedPack = new(
        () => LoadFromDirectory(DefaultRoot, validateEmbeddedTests: false),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static string DefaultRoot =>
        Path.Combine(AppContext.BaseDirectory, "resources", "rules", "ru");

    public static RuleCatalog LoadDefault() => ShippedPack.Value;

    /// <summary>
    /// Loads a pack from disk and runs every rule's embedded tests against it.
    /// </summary>
    /// <remarks>
    /// The validating entry point, which is what a test, a tool or an author editing the pack
    /// wants. The application uses <see cref="LoadDefault"/>.
    /// </remarks>
    public static RuleCatalog LoadFromDirectory(string root) =>
        LoadFromDirectory(root, validateEmbeddedTests: true);

    /// <param name="validateEmbeddedTests">
    /// When true, every rule's embedded test cases are executed and a disagreement throws.
    /// Structural validation — schema version, rule identifiers, one pack version across
    /// fragments, patterns that compile — happens either way.
    /// </param>
    /// <inheritdoc cref="LoadFromDirectory(string)"/>
    public static RuleCatalog LoadFromDirectory(string root, bool validateEmbeddedTests)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Russian rule-pack directory was not found: {root}");
        }

        var files = Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidDataException($"Russian rule-pack contains no JSON files: {root}");
        }

        var rules = new Dictionary<string, RuleDefinition>(StringComparer.Ordinal);
        var regexRules = new List<CompiledRuleDefinition>();
        string? packVersion = null;

        foreach (var file in files)
        {
            RulePackDocument document;
            try
            {
                document = JsonSerializer.Deserialize<RulePackDocument>(File.ReadAllText(file), JsonOptions)
                    ?? throw new InvalidDataException("Document is empty.");
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new InvalidDataException($"Invalid rule-pack JSON: {file}", ex);
            }

            ValidateDocument(document, file);
            packVersion ??= document.PackVersion;
            if (!string.Equals(packVersion, document.PackVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"All rule-pack fragments must use one PackVersion. Expected {packVersion}, got {document.PackVersion} in {file}.");
            }

            foreach (var rule in document.Rules)
            {
                AddRule(rule, rules, regexRules, file, validateEmbeddedTests);
            }
        }

        // Declarative regex wordlists sit in the same directory as *.yaml. Compiled by
        // RuleRegexFactory (composed from bare word lists) and fed through the exact same
        // validation and compile path, so a YAML entry behaves like a hand-typed JSON rule.
        foreach (var yamlRule in RuleRegexFactory.CompileDirectory(root))
        {
            AddRule(yamlRule, rules, regexRules, "regex.yaml", validateEmbeddedTests: true);
        }

        return new RuleCatalog(rules, regexRules, packVersion!);
    }

    /// <summary>
    /// Forces every compiled pattern to emit its code, so the first real check does not.
    /// </summary>
    /// <remarks>
    /// <para><see cref="RegexOptions.Compiled"/> defers IL emission to a pattern's first
    /// match, not its construction. Measured with <c>tools/startlat</c>, that made the first
    /// <c>RuleBasedAnalyzer.Analyze</c> cost 176 ms against a steady-state 0.7 ms — the entire
    /// difference being 78 patterns emitting code one after another, on whatever thread
    /// happened to run the user's first sentence.</para>
    ///
    /// <para>Previously the load-time self-tests paid this by accident, which is why building
    /// the catalog appeared to cost 177 ms. Now that they no longer run, the cost is paid
    /// here instead: deliberately, once, on a background thread during startup, where nobody
    /// is waiting for it. Failures are swallowed because this is purely an optimisation —
    /// a pattern that cannot match this string will simply be compiled when it is really
    /// used, exactly as it would have been without this call.</para>
    /// </remarks>
    public void Warm()
    {
        // Ordinary Russian prose rather than a blank or a placeholder. A compiled pattern
        // only emits the code for the paths it actually walks, and a probe that its literal
        // prefix or minimum-length pre-check rejects outright leaves most of that code
        // unemitted — measured, two spaces warmed away 109 ms of the 176 ms and a sentence
        // warms away considerably more. Nothing here is a finding: the result is discarded.
        const string probe =
            "Мы обсудили этот вопрос вчера вечером, и он ответил, что работа будет " +
            "продолжена в течение недели, несмотря на то что сроки уже прошли.";

        foreach (var rule in _regexRules)
        {
            try
            {
                rule.Pattern.IsMatch(probe);
            }
            catch (RegexMatchTimeoutException)
            {
                // A pattern too slow for two spaces will be too slow for real text as
                // well, and the analyzer already handles that. Not this method's problem.
            }
        }
    }

    private static void AddRule(
        RuleDefinition rule,
        Dictionary<string, RuleDefinition> rules,
        List<CompiledRuleDefinition> regexRules,
        string file,
        bool validateEmbeddedTests)
    {
        ValidateRule(rule, file);
        if (!rules.TryAdd(rule.RuleId, rule))
        {
            throw new InvalidDataException($"Duplicate RuleId '{rule.RuleId}' in {file}.");
        }

        if (string.Equals(rule.Implementation.Engine, "regex", StringComparison.Ordinal))
        {
            var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (rule.Implementation.IgnoreCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            Regex pattern;
            try
            {
                pattern = new Regex(rule.Implementation.Pattern!, options, WriteLiteDefaults.Analysis.RegexMatchTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException($"Invalid regex in rule '{rule.RuleId}'.", ex);
            }

            if (validateEmbeddedTests)
            {
                ValidateRegexTests(rule, file);
            }

            regexRules.Add(new CompiledRuleDefinition(rule, pattern));
        }
    }

    public bool TryGet(string ruleId, out RuleDefinition rule) => _rules.TryGetValue(ruleId, out rule!);

    public RuleDefinition GetRequired(string ruleId)
        => _rules.TryGetValue(ruleId, out var rule)
            ? rule
            : throw new InvalidOperationException($"Runtime emitted undeclared RuleId '{ruleId}'.");

    private static void ValidateDocument(RulePackDocument document, string file)
    {
        if (document.SchemaVersion != RulePackSchema.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported rule-pack SchemaVersion {document.SchemaVersion} in {file}; expected {RulePackSchema.CurrentVersion}.");
        }

        if (!string.Equals(document.Language, RulePackSchema.RussianLanguage, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Only language 'ru' is accepted in the Russian rule-pack: {file}.");
        }

        if (string.IsNullOrWhiteSpace(document.PackVersion) || document.Rules is null || document.Rules.Count == 0)
        {
            throw new InvalidDataException($"PackVersion and at least one rule are required: {file}.");
        }
    }

    private static void ValidateRule(RuleDefinition rule, string file)
    {
        if (!RuleIdPattern.IsMatch(rule.RuleId ?? string.Empty))
        {
            throw new InvalidDataException($"RuleId '{rule.RuleId}' is not a stable Russian ID in {file}.");
        }

        if (!string.Equals(rule.Language, RulePackSchema.RussianLanguage, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Rule '{rule.RuleId}' must have Language='ru'.");
        }

        if (string.IsNullOrWhiteSpace(rule.Title)
            || string.IsNullOrWhiteSpace(rule.ShortMessage)
            || string.IsNullOrWhiteSpace(rule.DetailedExplanation)
            || string.IsNullOrWhiteSpace(rule.Source))
        {
            throw new InvalidDataException($"Rule '{rule.RuleId}' has incomplete explanatory metadata.");
        }

        if (rule.BadExamples is null || rule.BadExamples.Count == 0
            || rule.GoodExamples is null || rule.GoodExamples.Count == 0
            || rule.Suggestions is null || rule.Suggestions.Count == 0)
        {
            throw new InvalidDataException($"Rule '{rule.RuleId}' requires bad/good examples and suggestions.");
        }

        if (rule.Tests is null
            || !rule.Tests.Any(test => test.ShouldMatch)
            || !rule.Tests.Any(test => !test.ShouldMatch))
        {
            throw new InvalidDataException($"Rule '{rule.RuleId}' requires embedded positive and negative tests.");
        }

        if (rule.Implementation is null)
        {
            throw new InvalidDataException($"Rule '{rule.RuleId}' has no implementation descriptor.");
        }

        var engine = rule.Implementation.Engine;
        if (engine is not ("regex" or "builtin" or "spelling"))
        {
            throw new InvalidDataException($"Rule '{rule.RuleId}' uses unsupported engine '{engine}'.");
        }

        if (engine == "regex"
            && (string.IsNullOrWhiteSpace(rule.Implementation.Pattern)
                || rule.Implementation.Replacement is null))
        {
            throw new InvalidDataException($"Regex rule '{rule.RuleId}' requires Pattern and Replacement.");
        }
    }

    /// <summary>
    /// Runs one rule's embedded test cases and throws on the first disagreement.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this compiles its own pattern instead of reusing the runtime one.</b>
    /// The runtime pattern carries a 100 ms match timeout, which is a guard against a
    /// pathological *user input* reaching a backtracking-heavy rule. The inputs here are the
    /// rule's own examples — tens of characters, matched in microseconds — so a timeout on
    /// one of them says nothing about the pattern and everything about the machine: a
    /// parallel test run starved this thread long enough for a trivial match to cross 100 ms,
    /// and the pack was reported as broken. Validation therefore gets a timeout generous
    /// enough that load cannot masquerade as a bad pattern, while remaining finite so that a
    /// genuinely catastrophic pattern fails instead of hanging the build.</para>
    ///
    /// <para><see cref="RegexOptions.Compiled"/> is deliberately absent: each pattern is
    /// matched a handful of times here and then discarded, and compiling it costs far more
    /// than interpreting those few matches.</para>
    /// </remarks>
    private static void ValidateRegexTests(RuleDefinition rule, string file)
    {
        var options = RegexOptions.CultureInvariant;
        if (rule.Implementation.IgnoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        var pattern = new Regex(rule.Implementation.Pattern!, options, ValidationMatchTimeout);

        foreach (var test in rule.Tests)
        {
            var match = pattern.Match(test.Input);
            if (match.Success != test.ShouldMatch)
            {
                throw new InvalidDataException(
                    $"Embedded test '{test.Name}' failed for '{rule.RuleId}' in {file}: expected match={test.ShouldMatch}.");
            }

            if (!test.ShouldMatch || test.ExpectedSuggestion is null)
            {
                continue;
            }

            var actual = match.Result(rule.Implementation.Replacement!);
            if (!string.Equals(actual, test.ExpectedSuggestion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Embedded test '{test.Name}' failed for '{rule.RuleId}': expected suggestion '{test.ExpectedSuggestion}', got '{actual}'.");
            }
        }
    }
}

public sealed record CompiledRuleDefinition(RuleDefinition Definition, Regex Pattern);
