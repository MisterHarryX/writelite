using System.Text.Json.Serialization;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Internal transport model for the local engine HTTP JSON response.
/// Not for UI binding — map to WriteLite models in a later stage.
/// </summary>
public sealed class WriteLiteLanguageResponseDto
{
    [JsonPropertyName("software")]
    public WriteLiteLanguageSoftwareDto? Software { get; set; }

    [JsonPropertyName("language")]
    public WriteLiteLanguageInfoDto? Language { get; set; }

    [JsonPropertyName("matches")]
    public List<WriteLiteLanguageMatchDto> Matches { get; set; } = [];
}

public sealed class WriteLiteLanguageSoftwareDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("apiVersion")]
    public int? ApiVersion { get; set; }

    [JsonPropertyName("premium")]
    public bool? Premium { get; set; }
}

public sealed class WriteLiteLanguageInfoDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("detectedLanguage")]
    public WriteLiteLanguageDetectedDto? DetectedLanguage { get; set; }
}

public sealed class WriteLiteLanguageDetectedDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }
}

public sealed class WriteLiteLanguageMatchDto
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("shortMessage")]
    public string? ShortMessage { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("length")]
    public int Length { get; set; }

    [JsonPropertyName("replacements")]
    public List<WriteLiteLanguageReplacementDto> Replacements { get; set; } = [];

    [JsonPropertyName("context")]
    public WriteLiteLanguageContextDto? Context { get; set; }

    [JsonPropertyName("sentence")]
    public string? Sentence { get; set; }

    [JsonPropertyName("type")]
    public WriteLiteLanguageTypeDto? Type { get; set; }

    [JsonPropertyName("rule")]
    public WriteLiteLanguageRuleDto? Rule { get; set; }

    [JsonPropertyName("ignoreForIncompleteSentence")]
    public bool? IgnoreForIncompleteSentence { get; set; }

    [JsonPropertyName("contextForSureMatch")]
    public int? ContextForSureMatch { get; set; }
}

public sealed class WriteLiteLanguageReplacementDto
{
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

public sealed class WriteLiteLanguageContextDto
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("length")]
    public int Length { get; set; }
}

public sealed class WriteLiteLanguageTypeDto
{
    [JsonPropertyName("typeName")]
    public string? TypeName { get; set; }
}

public sealed class WriteLiteLanguageRuleDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("issueType")]
    public string? IssueType { get; set; }

    [JsonPropertyName("category")]
    public WriteLiteLanguageCategoryDto? Category { get; set; }
}

public sealed class WriteLiteLanguageCategoryDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
