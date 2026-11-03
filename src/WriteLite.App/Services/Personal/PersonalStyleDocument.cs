using System.IO;
using System.Text.Json;
using WriteLite.Services.Storage;

namespace WriteLite.Services.Personal;

/// <summary>
/// The on-disk form of <see cref="PersonalStyleModel"/>.
/// </summary>
/// <remarks>
/// Arrays rather than objects per word, and no indentation: a library of a few thousand words
/// with its pair and triple tables is the largest file WriteLite writes on its own initiative,
/// and it is rewritten every time the user has written something new. The format is versioned
/// so a later schema can migrate instead of discarding what was learned.
/// </remarks>
internal sealed class PersonalStyleDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Language { get; set; } = "ru";

    /// <summary>word → [count, capitalised, lastDay].</summary>
    public Dictionary<string, int[]>? Words { get; set; }

    public Dictionary<string, Dictionary<string, int>>? Bigrams { get; set; }

    public Dictionary<string, Dictionary<string, int>>? Trigrams { get; set; }

    /// <summary>word → [count, days, lastDay]; display form in <see cref="UnknownDisplay"/>.</summary>
    public Dictionary<string, int[]>? Unknown { get; set; }

    public Dictionary<string, string>? UnknownDisplay { get; set; }

    public Dictionary<string, Dictionary<string, int>>? Corrections { get; set; }

    /// <summary>[words, sentences, commas, guillemets, straightQuotes, exclamations, questions, calibrationTexts, predictionsAccepted].</summary>
    public int[]? Counters { get; set; }

    public ulong[]? SentenceHashes { get; set; }

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static PersonalStyleDocument From(PersonalStyleModel model)
    {
        var c = model.Counters;
        return new PersonalStyleDocument
        {
            Words = model.Words.ToDictionary(p => p.Key, p => new[] { p.Value.Count, p.Value.Capitalized, p.Value.LastDay }, StringComparer.Ordinal),
            Bigrams = Clone(model.Bigrams),
            Trigrams = Clone(model.Trigrams),
            Unknown = model.Unknown.ToDictionary(p => p.Key, p => new[] { p.Value.Count, p.Value.Days, p.Value.LastDay }, StringComparer.Ordinal),
            UnknownDisplay = model.Unknown.Where(p => p.Value.Display != p.Key).ToDictionary(p => p.Key, p => p.Value.Display, StringComparer.Ordinal),
            Corrections = Clone(model.Corrections),
            Counters = [c.Words, c.Sentences, c.Commas, c.Guillemets, c.StraightQuotes, c.Exclamations, c.Questions, c.CalibrationTexts, c.PredictionsAccepted],
            SentenceHashes = model.SentenceHashes.ToArray(),
        };

        static Dictionary<string, Dictionary<string, int>> Clone(Dictionary<string, Dictionary<string, int>> table)
            => table.ToDictionary(p => p.Key, p => new Dictionary<string, int>(p.Value, StringComparer.Ordinal), StringComparer.Ordinal);
    }

    public PersonalStyleModel ToModel()
    {
        var model = new PersonalStyleModel();
        if (Words is not null)
        {
            foreach (var (key, values) in Words)
            {
                if (values is { Length: >= 1 }) model.RestoreWord(key, values[0], values.Length > 1 ? values[1] : 0, values.Length > 2 ? values[2] : 0);
            }
        }

        if (Unknown is not null)
        {
            foreach (var (key, values) in Unknown)
            {
                if (values is not { Length: >= 1 }) continue;
                var display = UnknownDisplay is not null && UnknownDisplay.TryGetValue(key, out var d) ? d : key;
                model.RestoreUnknown(key, values[0], values.Length > 1 ? values[1] : 1, values.Length > 2 ? values[2] : 0, display);
            }
        }

        model.RestoreNgrams(Bigrams, Trigrams);
        model.RestoreCorrections(Corrections);
        model.RestoreSentenceHashes(SentenceHashes);

        if (Counters is { } c)
        {
            var target = model.Counters;
            int At(int i) => i < c.Length ? Math.Max(0, c[i]) : 0;
            target.Words = At(0);
            target.Sentences = At(1);
            target.Commas = At(2);
            target.Guillemets = At(3);
            target.StraightQuotes = At(4);
            target.Exclamations = At(5);
            target.Questions = At(6);
            target.CalibrationTexts = At(7);
            target.PredictionsAccepted = At(8);
        }

        return model;
    }

    /// <summary>Loads the library, or an empty one on first run or when nothing readable exists.</summary>
    public static PersonalStyleModel Load(string path)
    {
        var document = AtomicJsonFile.Read<PersonalStyleDocument>(path);
        if (document is null) return new PersonalStyleModel();

        if (document.SchemaVersion is < 1 or > CurrentSchemaVersion)
        {
            CompatibilityLogger.Technical("personal-style-schema-unsupported", $"version={document.SchemaVersion}");
            return new PersonalStyleModel();
        }

        return document.ToModel();
    }

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, Compact);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WriteLite",
        "personal-style.json");
}
