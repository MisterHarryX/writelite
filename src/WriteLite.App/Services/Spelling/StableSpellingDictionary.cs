using System.IO;
using WriteLite.Services.Rules;
using YamlDotNet.Serialization;

namespace WriteLite.Services.Spelling;

/// <summary>
/// Loads the curated stable spelling corrections from
/// <c>resources/rules/ru/stable-spelling.yaml</c>.
/// </summary>
/// <remarks>
/// The keys are already folded (lower case, ё collapsed to е) exactly as the caller folds an
/// input word before looking it up, matching the semantics of the table it replaces. Each
/// entry is one unambiguous typo that the generic Hunspell ranker is known to mis-correct.
/// The table is the single source for both spelling lanes: <c>spell</c> (per-word spell
/// checker) and <c>ai</c> (Lite corrector, whose lite-rules.json is generated from this file
/// at build time). Entries declare their lane; the default is <c>spell</c>.
/// </remarks>
public static class StableSpellingDictionary
{
    public const string SpellLane = "spell";
    public const string AiLane = "ai";

    public static IReadOnlyDictionary<string, string> Load(string lane = SpellLane, string? directory = null)
    {
        var root = directory ?? Path.Combine(AppContext.BaseDirectory, "resources", "rules", "ru");
        var path = Path.Combine(root, "stable-spelling.yaml");
        if (!File.Exists(path))
        {
            return Empty;
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(new LowerFirstCharacterNamingConvention())
            .IgnoreUnmatchedProperties()
            .Build();

        StableSpellingDocument document;
        try
        {
            document = deserializer.Deserialize<StableSpellingDocument>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Document is empty.");
        }
        catch (Exception ex) when (ex is InvalidDataException or YamlDotNet.Core.YamlException)
        {
            CompatibilityLogger.Technical("stable-spelling-load-failed", $"path={path} type={ex.GetType().Name}");
            return Empty;
        }

        var corrections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in document.StableSpelling ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.From) || item.To is null)
            {
                continue;
            }

            if (!MatchesLane(item.Lane, lane))
            {
                continue;
            }

            corrections[item.From] = item.To;
        }

        return corrections;
    }

    /// <summary>An entry with lane <c>both</c> is served by both pipelines.</summary>
    private static bool MatchesLane(string entryLane, string requestedLane)
    {
        var effective = string.IsNullOrWhiteSpace(entryLane) ? SpellLane : entryLane;
        return effective.Equals(SpellLane, StringComparison.OrdinalIgnoreCase) && requestedLane.Equals(SpellLane, StringComparison.OrdinalIgnoreCase)
            || effective.Equals(AiLane, StringComparison.OrdinalIgnoreCase) && requestedLane.Equals(AiLane, StringComparison.OrdinalIgnoreCase)
            || effective.Equals("both", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed class StableSpellingDocument
{
    public List<StableSpellingItem>? StableSpelling { get; set; }
}

public sealed class StableSpellingItem
{
    public string Id { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Lane { get; set; } = string.Empty;
}
