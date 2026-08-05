using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;

namespace WriteLite.AI.Local;

public sealed class LiteRulesModel
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("modelVersion")]
    public string ModelVersion { get; set; } = "writelight-lite-1.0.0";

    [JsonPropertyName("ruSpelling")]
    public Dictionary<string, string> RuSpelling { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("ruIntroducers")]
    public List<string> RuIntroducers { get; set; } = [];

    [JsonPropertyName("ruParentheticals")]
    public List<string> RuParentheticals { get; set; } = [];

    public static LiteRulesModel LoadEmbedded()
    {
        var asm = typeof(LiteRulesModel).Assembly;
        using var stream = asm.GetManifestResourceStream("WriteLite.AI.Local.Models.lite-rules.json")
                           ?? throw new InvalidOperationException("Embedded lite-rules.json missing.");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var model = JsonSerializer.Deserialize<LiteRulesModel>(json, JsonOptions)
                    ?? throw new InvalidOperationException("Failed to parse lite-rules.json.");
        // Normalize dictionaries to case-insensitive lookups.
        model.RuSpelling = new Dictionary<string, string>(model.RuSpelling, StringComparer.OrdinalIgnoreCase);
        return model;
    }

    public static LiteRulesModel LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        var model = JsonSerializer.Deserialize<LiteRulesModel>(json, JsonOptions)
                    ?? throw new InvalidOperationException("Failed to parse rules file.");
        model.RuSpelling = new Dictionary<string, string>(model.RuSpelling, StringComparer.OrdinalIgnoreCase);
        return model;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
