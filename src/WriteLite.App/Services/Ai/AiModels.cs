using System.Text.Json.Serialization;
using WriteLite.Models;

namespace WriteLite.Services.Ai;

public sealed class AiTextAnalysisResponse
{
    [JsonPropertyName("correctedText")]
    public string? CorrectedText { get; set; }

    [JsonPropertyName("issues")]
    public List<AiTextIssueDto>? Issues { get; set; }
}

public sealed class AiTextIssueDto
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("original")]
    public string? Original { get; set; }

    [JsonPropertyName("replacement")]
    public string? Replacement { get; set; }

    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("length")]
    public int Length { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } = 0.5;

    [JsonPropertyName("safeToApply")]
    public bool SafeToApply { get; set; }
}

public static class AiIssueTypeMapper
{
    public static (IssueCategory Category, LinguisticIssueCategory Linguistic) Map(string? type)
    {
        return (type ?? "").Trim().ToLowerInvariant() switch
        {
            "spelling" or "orthography" => (IssueCategory.Orthography, LinguisticIssueCategory.Typo),
            "punctuation" => (IssueCategory.Punctuation, LinguisticIssueCategory.PunctuationRecommendation),
            "grammar" => (IssueCategory.Grammar, LinguisticIssueCategory.AgreementError),
            "capitalization" => (IssueCategory.Orthography, LinguisticIssueCategory.EndingError),
            "typography" => (IssueCategory.Readability, LinguisticIssueCategory.ExtraSpace),
            "style" => (IssueCategory.Style, LinguisticIssueCategory.LongSentence),
            "clarity" => (IssueCategory.Style, LinguisticIssueCategory.LongSentence),
            _ => (IssueCategory.Grammar, LinguisticIssueCategory.UnknownWord)
        };
    }
}
