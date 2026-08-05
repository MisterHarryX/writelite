using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace WriteLite.Services.Lexical;

public sealed class LexicalPackLoadResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public LexicalPackManifest? Manifest { get; init; }
    public IReadOnlyDictionary<string, LexicalEntry>? Index { get; init; }
}

/// <summary>
/// Loads and validates a local lexical pack. Corrupted / oversized packs fail gracefully.
/// </summary>
public static class LexicalPackLoader
{
    public const int CurrentFormatVersion = 2;
    public const long DefaultMaxBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static LexicalPackLoadResult LoadFromFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return Fail("lexical pack file not found");

            var info = new FileInfo(path);
            if (info.Length <= 0)
                return Fail("lexical pack is empty");
            if (info.Length > DefaultMaxBytes)
                return Fail("lexical pack exceeds size limit");

            var bytes = File.ReadAllBytes(path);
            return LoadFromBytes(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Fail("lexical pack unreadable");
        }
    }

    public static LexicalPackLoadResult LoadFromBytes(byte[] bytes)
    {
        try
        {
            if (bytes.Length == 0) return Fail("lexical pack is empty");
            if (bytes.Length > DefaultMaxBytes) return Fail("lexical pack exceeds size limit");

            var doc = JsonSerializer.Deserialize<LexicalPackDocument>(bytes, JsonOptions);
            if (doc?.Manifest is null || doc.Entries is null)
                return Fail("invalid pack structure");

            var manifest = doc.Manifest;
            if (manifest.FormatVersion is < 1 or > CurrentFormatVersion)
                return Fail("unsupported pack format version");

            if (string.IsNullOrWhiteSpace(manifest.PackId) || string.IsNullOrWhiteSpace(manifest.PackVersion))
                return Fail("pack manifest missing id/version");

            if (string.IsNullOrWhiteSpace(manifest.License))
                return Fail("pack license missing");

            if (manifest.MaxBytes > 0 && bytes.Length > manifest.MaxBytes)
                return Fail("pack exceeds declared maxBytes");

            if (!string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(hash, manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    // Allow hash of entries-only for demo flexibility: recompute on normalized JSON is brittle;
                    // if declared, require exact file match.
                    return Fail("pack integrity check failed");
                }
            }

            if (manifest.EntryCount != doc.Entries.Count)
                return Fail("pack entryCount mismatch");

            var index = new Dictionary<string, LexicalEntry>(StringComparer.OrdinalIgnoreCase);
            var lemmas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dto in doc.Entries)
            {
                if (string.IsNullOrWhiteSpace(dto.Lemma))
                    return Fail("pack contains an entry without lemma");
                if (ParseLanguage(dto.Language) != LexicalLanguage.Russian)
                    return Fail("pack contains a non-Russian entry");
                if (!lemmas.Add(FoldYo(dto.Lemma.Trim())))
                    return Fail("pack contains duplicate lemmas");
                var entry = MapEntry(dto);
                AddIndex(index, entry.Lemma, entry);
                foreach (var form in entry.Inflections)
                    AddIndex(index, form, entry);
            }

            if (index.Count == 0)
                return Fail("pack index is empty");

            return new LexicalPackLoadResult
            {
                Success = true,
                Manifest = manifest,
                Index = index
            };
        }
        catch (JsonException)
        {
            return Fail("pack JSON parse error");
        }
        catch (Exception)
        {
            return Fail("pack load failed");
        }
    }

    public static string ComputeSha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void AddIndex(Dictionary<string, LexicalEntry> index, string key, LexicalEntry entry)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var literal = key.Trim();
        index.TryAdd(literal, entry);
        index.TryAdd(FoldYo(literal), entry);
    }

    private static string FoldYo(string value)
        => value.Replace('ё', 'е').Replace('Ё', 'Е');

    private static LexicalEntry MapEntry(LexicalEntryDto dto)
    {
        var lang = ParseLanguage(dto.Language);
        var pos = ParsePos(dto.Pos);
        var lemma = dto.Lemma.Trim();
        return new LexicalEntry
        {
            Lemma = lemma,
            Language = lang,
            PartOfSpeech = pos,
            Inflections = (dto.Inflections ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Synonyms = MapSenseLinks(dto.Synonyms, dto.Pos, canReplace: true),
            Antonyms = MapSenseLinks(dto.Antonyms, dto.Pos, canReplace: true),
            Definitions = (dto.Definitions ?? [])
                .Where(d => !string.IsNullOrWhiteSpace(d.Text))
                .Select(d => new LexicalDefinition(
                    d.Text.Trim(),
                    ParsePos(d.Pos ?? dto.Pos),
                    d.Label,
                    d.SenseId,
                    d.SourceId ?? "writelight-cc0",
                    d.Historical,
                    d.Era))
                .ToArray(),
            Examples = (dto.Examples ?? [])
                .Where(e => !string.IsNullOrWhiteSpace(e.Text))
                .Select(e => new LexicalExample(e.Text.Trim(), e.SenseId, lemma))
                .ToArray()
        };
    }

    private static IReadOnlyList<LexicalSuggestion> MapSenseLinks(
        List<LexicalSenseLinkDto>? links, string? defaultPos, bool canReplace)
        => (links ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.Value))
            .Select(s => new LexicalSuggestion(
                s.Value.Trim(),
                s.Value.Trim(),
                ParsePos(s.Pos ?? defaultPos),
                s.Label,
                Clamp01(s.Relevance),
                canReplace))
            .ToArray();

    public static LexicalLanguage ParseLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return LexicalLanguage.Unknown;
        return value.Trim().ToLowerInvariant() switch
        {
            "ru" or "rus" or "russian" => LexicalLanguage.Russian,
            _ => LexicalLanguage.Unknown
        };
    }

    public static LexicalPartOfSpeech ParsePos(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return LexicalPartOfSpeech.Unknown;
        return value.Trim().ToLowerInvariant() switch
        {
            "noun" or "n" or "сущ" => LexicalPartOfSpeech.Noun,
            "verb" or "v" or "гл" => LexicalPartOfSpeech.Verb,
            "adj" or "adjective" or "прил" => LexicalPartOfSpeech.Adjective,
            "adv" or "adverb" or "нар" => LexicalPartOfSpeech.Adverb,
            "pron" or "pronoun" => LexicalPartOfSpeech.Pronoun,
            "prep" or "preposition" => LexicalPartOfSpeech.Preposition,
            "conj" or "conjunction" => LexicalPartOfSpeech.Conjunction,
            "particle" => LexicalPartOfSpeech.Particle,
            "interj" or "interjection" => LexicalPartOfSpeech.Interjection,
            "num" or "numeral" => LexicalPartOfSpeech.Numeral,
            _ => LexicalPartOfSpeech.Unknown
        };
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);

    private static LexicalPackLoadResult Fail(string error)
        => new() { Success = false, Error = error };
}
