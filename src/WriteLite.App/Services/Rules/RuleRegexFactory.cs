using System.IO;
using System.Text.RegularExpressions;
using WriteLite.Models;
using YamlDotNet.Serialization;

namespace WriteLite.Services.Rules;

/// <summary>
/// Turns the compact declarative lists in <c>resources/rules/ru/regex.yaml</c> into the same
/// compiled <see cref="RuleDefinition"/>s the JSON rule-pack yields. The point is that the
/// author adds a bare word to a list instead of writing a full regex rule by hand — the
/// pattern, the replacement, the tests and the explanatory metadata are all composed here.
///
/// The generated rules go through the identical validation and match.Result pipeline as the
/// JSON regex rules, so a YAML entry has exactly the same behaviour and guarantees.
/// </summary>
public static class RuleRegexFactory
{
    public const string SourceText =
        "Авторское правило WriteLite: нормативное написание, регулярное выражение (из словарного списка).";

    /// <summary>Compiles every <c>*.yaml</c> in <paramref name="root"/> into rule definitions.</summary>
    public static IReadOnlyList<RuleDefinition> CompileDirectory(string root)
    {
        var files = Directory.GetFiles(root, "*.yaml", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal);
        var result = new List<RuleDefinition>();
        foreach (var file in files)
        {
            result.AddRange(CompileFile(file));
        }

        return result;
    }

    public static IReadOnlyList<RuleDefinition> CompileFile(string path)
    {
        var document = ReadDocument(path);
        var rules = new List<RuleDefinition>();

        foreach (var item in document.WordSwap ?? [])
        {
            rules.Add(BuildWordSwap(item));
        }

        foreach (var item in document.SuffixParticles ?? [])
        {
            rules.Add(BuildSuffixParticle(item));
        }

        foreach (var item in document.Abbreviations ?? [])
        {
            rules.Add(BuildAbbreviation(item));
        }

        return rules;
    }

    private static RegexWordlistDocument ReadDocument(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(new LowerFirstCharacterNamingConvention())
            .IgnoreUnmatchedProperties()
            .Build();
        RegexWordlistDocument document;
        try
        {
            document = deserializer.Deserialize<RegexWordlistDocument>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Document is empty.");
        }
        catch (Exception ex) when (ex is InvalidDataException or YamlDotNet.Core.YamlException)
        {
            throw new InvalidDataException($"Invalid regex wordlist YAML: {path}", ex);
        }

        return document;
    }

    private static string RuleId(string leaf)
        => $"ru.spelling.{leaf}";

    /// <summary>«помоему» → «по-моему»: <c>\b<from>\b</c> → <c>&lt;to&gt;</c>, ignoreCase.</summary>
    private static RuleDefinition BuildWordSwap(WordSwapItem item)
    {
        var pattern = $@"\b{Regex.Escape(item.From)}\b";
        return NewRule(
            RuleId(item.Id),
            category: LinguisticIssueCategory.JoinedOrSeparateSpelling,
            issueCategory: IssueCategory.Orthography,
            title: $"«{item.From}» пишется как «{item.To}»",
            shortMessage: $"Правильно: «{item.To}».",
            explanation:
                $"Слово «{item.To}» пишется именно так; написание «{item.From}» ошибочно.",
            badExamples: [item.From],
            goodExamples: [item.To],
            suggestions: [item.To],
            safeToApply: true,
            pattern: pattern,
            replacement: item.To,
            ignoreCase: true,
            tests: PositiveNegativeTests(item.From, item.To, item.To));
    }

    /// <summary>«ктото» → «кто-то»: <c>\b(кто|что|…)то\b</c> → <c>$1-то</c>.</summary>
    private static RuleDefinition BuildSuffixParticle(SuffixParticleItem item)
    {
        var bases = string.Join("|", item.Bases.Select(Regex.Escape));
        var suffix = item.Suffix;
        var pattern = $@"\b({bases}){suffix}\b";
        var firstBase = item.Bases[0];
        var positive = $"{firstBase}{suffix}";
        var negative = $"{firstBase}-{suffix}";

        return NewRule(
            RuleId(item.Id),
            category: LinguisticIssueCategory.JoinedOrSeparateSpelling,
            issueCategory: IssueCategory.Orthography,
            title: $"Частица «-{suffix}» пишется через дефис",
            shortMessage: $"Местоимение с частицей «-{suffix}» пишется через дефис.",
            explanation:
                $"Неопределённые местоимения и наречия с частицей «-{suffix}» пишутся через дефис: «{negative}», «где-{suffix}».",
            badExamples: [$"{firstBase}{suffix} здесь."],
            goodExamples: [$"{firstBase}-{suffix} здесь."],
            suggestions: [$"Добавить дефис перед «{suffix}»."],
            safeToApply: true,
            pattern: pattern,
            replacement: $"$1-{suffix}",
            ignoreCase: true,
            tests: PositiveNegativeTests(positive, negative, negative));
    }

    /// <summary>«т.д.» → «т. д.»: <c>\bт\.д\.</c> → <c>т. д.</c>.</summary>
    private static RuleDefinition BuildAbbreviation(AbbreviationItem item)
    {
        var pattern = $@"\b{Regex.Escape(item.Compact)}";
        return NewRule(
            $"ru.punctuation.{item.Id}",
            category: LinguisticIssueCategory.PunctuationRecommendation,
            issueCategory: IssueCategory.Punctuation,
            title: $"Сокращение «{item.Spaced}»",
            shortMessage: "Между частями сокращения нужен пробел.",
            explanation: $"Общепринятое оформление сокращения: «{item.Spaced}».",
            badExamples: [item.Compact],
            goodExamples: [item.Spaced],
            suggestions: [item.Spaced],
            safeToApply: true,
            pattern: pattern,
            replacement: item.Spaced,
            ignoreCase: true,
            tests: PositiveNegativeTests(item.Compact, item.Spaced, item.Spaced));
    }

    private static RuleDefinition NewRule(
        string ruleId,
        LinguisticIssueCategory category,
        IssueCategory issueCategory,
        string title,
        string shortMessage,
        string explanation,
        IReadOnlyList<string> badExamples,
        IReadOnlyList<string> goodExamples,
        IReadOnlyList<string> suggestions,
        bool safeToApply,
        string pattern,
        string replacement,
        bool ignoreCase,
        IReadOnlyList<RuleTestCase> tests)
    {
        return new RuleDefinition(
            ruleId,
            RulePackSchema.RussianLanguage,
            category,
            issueCategory,
            title,
            shortMessage,
            explanation,
            badExamples,
            goodExamples,
            IssueSeverity.Error,
            suggestions,
            safeToApply,
            SourceText,
            new RuleImplementation("regex", pattern, replacement, ignoreCase),
            tests);
    }

    private static IReadOnlyList<RuleTestCase> PositiveNegativeTests(string positive, string suggestion, string negative)
        => new RuleTestCase[]
        {
            new("positive", positive, true, suggestion),
            new("negative", negative, false)
        };
}

public sealed class RegexWordlistDocument
{
    public List<WordSwapItem>? WordSwap { get; set; }
    public List<SuffixParticleItem>? SuffixParticles { get; set; }
    public List<AbbreviationItem>? Abbreviations { get; set; }
}

public sealed class WordSwapItem
{
    public string Id { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
}

public sealed class SuffixParticleItem
{
    public string Id { get; set; } = string.Empty;
    public string Suffix { get; set; } = string.Empty;
    public List<string> Bases { get; set; } = [];
}

public sealed class AbbreviationItem
{
    public string Id { get; set; } = string.Empty;
    public string Compact { get; set; } = string.Empty;
    public string Spaced { get; set; } = string.Empty;
}

/// <summary>
/// Maps C# <c>WordSwap</c> to the YAML key <c>wordSwap</c> (and so on) — YamlDotNet matches
/// YAML keys to properties case-sensitively, and every section in <c>regex.yaml</c> is
/// written in lower-camelCase. Lowering only the first character keeps the C# view in
/// idiomatic PascalCase while reading idiomatic YAML.
/// </summary>
public sealed class LowerFirstCharacterNamingConvention : YamlDotNet.Serialization.INamingConvention
{
    public string Apply(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return char.ToLowerInvariant(value[0]) + value.Substring(1);
    }

    public string Reverse(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return char.ToUpperInvariant(value[0]) + value.Substring(1);
    }
}