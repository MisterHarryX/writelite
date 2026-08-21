using System.Text.Json.Serialization;

namespace WriteLite.Services.Lexical;

public sealed class LexicalPackManifest
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("packId")]
    public string PackId { get; set; } = "";

    [JsonPropertyName("packVersion")]
    public string PackVersion { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("license")]
    public string License { get; set; } = "";

    [JsonPropertyName("licenseNote")]
    public string? LicenseNote { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// Language of every entry in the pack ("ru", "en"). Absent in packs written
    /// before multi-language support, which are Russian by definition.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("isDemo")]
    public bool IsDemo { get; set; }

    [JsonPropertyName("entryCount")]
    public int EntryCount { get; set; }

    [JsonPropertyName("maxBytes")]
    public long MaxBytes { get; set; } = 8 * 1024 * 1024;

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}

public sealed class LexicalPackDocument
{
    [JsonPropertyName("manifest")]
    public LexicalPackManifest Manifest { get; set; } = new();

    [JsonPropertyName("entries")]
    public List<LexicalEntryDto> Entries { get; set; } = [];
}

public sealed class LexicalEntryDto
{
    [JsonPropertyName("lemma")]
    public string Lemma { get; set; } = "";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "ru";

    [JsonPropertyName("pos")]
    public string Pos { get; set; } = "unknown";

    [JsonPropertyName("inflections")]
    public List<string>? Inflections { get; set; }

    [JsonPropertyName("synonyms")]
    public List<LexicalSenseLinkDto>? Synonyms { get; set; }

    [JsonPropertyName("antonyms")]
    public List<LexicalSenseLinkDto>? Antonyms { get; set; }

    [JsonPropertyName("definitions")]
    public List<LexicalDefinitionDto>? Definitions { get; set; }

    [JsonPropertyName("examples")]
    public List<LexicalExampleDto>? Examples { get; set; }
}

public sealed class LexicalSenseLinkDto
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("pos")]
    public string? Pos { get; set; }

    [JsonPropertyName("relevance")]
    public double Relevance { get; set; } = 0.5;

    /// <summary>Id of the source this link was imported from, for provenance.</summary>
    [JsonPropertyName("sourceId")]
    public string? SourceId { get; set; }
}

public sealed class LexicalDefinitionDto
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("pos")]
    public string? Pos { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("senseId")]
    public string? SenseId { get; set; }

    [JsonPropertyName("sourceId")]
    public string? SourceId { get; set; }

    [JsonPropertyName("historical")]
    public bool Historical { get; set; }

    [JsonPropertyName("era")]
    public string? Era { get; set; }
}

public sealed class LexicalExampleDto
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("senseId")]
    public string? SenseId { get; set; }
}

public sealed class LexicalEntry
{
    public required string Lemma { get; init; }
    public required LexicalLanguage Language { get; init; }
    public LexicalPartOfSpeech PartOfSpeech { get; init; }
    public IReadOnlyList<string> Inflections { get; init; } = [];
    public IReadOnlyList<LexicalSuggestion> Synonyms { get; init; } = [];
    public IReadOnlyList<LexicalSuggestion> Antonyms { get; init; } = [];
    public IReadOnlyList<LexicalDefinition> Definitions { get; init; } = [];
    public IReadOnlyList<LexicalExample> Examples { get; init; } = [];
}
