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

    public static RuleCatalog LoadDefault()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "resources", "rules", "ru");
        return LoadFromDirectory(root);
    }

    public static RuleCatalog LoadFromDirectory(string root)
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
                        pattern = new Regex(rule.Implementation.Pattern!, options, TimeSpan.FromMilliseconds(100));
                    }
                    catch (ArgumentException ex)
                    {
                        throw new InvalidDataException($"Invalid regex in rule '{rule.RuleId}'.", ex);
                    }

                    ValidateRegexTests(rule, pattern, file);
                    regexRules.Add(new CompiledRuleDefinition(rule, pattern));
                }
            }
        }

        return new RuleCatalog(rules, regexRules, packVersion!);
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

    private static void ValidateRegexTests(RuleDefinition rule, Regex pattern, string file)
    {
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
