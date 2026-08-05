using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WriteLite.Services.Lexical;

public sealed class OzhegovPackManifest
{
    public string PackId { get; set; } = "ozhegov-licensed";
    public string PackVersion { get; set; } = "";
    public string RightsHolder { get; set; } = "";
    public string License { get; set; } = "";
    public string? LicenseNote { get; set; }
    public string? Source { get; set; }
    public string? LicenseEvidence { get; set; }
    public bool LocalUsePermitted { get; set; }
    public bool RedistributionPermitted { get; set; }
    public string? Sha256 { get; set; }
    public DateTimeOffset? RetrievedAt { get; set; }
    public int EntryCount { get; set; }
}

/// <summary>
/// Imports a user-provided licensed Ozhegov pack. Never downloads or fabricates content.
/// Expected layout: {dir}/ozhegov.manifest.json + ozhegov.entries.jsonl
/// </summary>
public sealed class LicensedOzhegovPackImporter
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WriteLite", "dictionaries", "ozhegov");

    public OzhegovPackLoadResult TryLoad(string? directory = null)
    {
        directory ??= DefaultDirectory;
        var manifestPath = Path.Combine(directory, "ozhegov.manifest.json");
        var entriesPath = Path.Combine(directory, "ozhegov.entries.jsonl");

        if (!File.Exists(manifestPath) || !File.Exists(entriesPath))
        {
            return OzhegovPackLoadResult.Missing(
                "Лицензионный pack словаря Ожегова не установлен. Укажите каталог с ozhegov.manifest.json и ozhegov.entries.jsonl.");
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<OzhegovPackManifest>(File.ReadAllText(manifestPath));
            if (!HasRequiredRightsMetadata(manifest))
            {
                return OzhegovPackLoadResult.Missing(
                    "Манифест Ожегова неполный: нужны версия, правообладатель, источник, доказательство лицензии, разрешение локального использования, SHA-256 и число статей.");
            }

            if (new FileInfo(entriesPath).Length > 256L * 1024 * 1024)
                return OzhegovPackLoadResult.Missing("Лицензионный пакет превышает допустимый размер.");

            using (var stream = File.OpenRead(entriesPath))
            {
                var actualSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!string.Equals(actualSha256, manifest!.Sha256, StringComparison.OrdinalIgnoreCase))
                    return OzhegovPackLoadResult.Missing("SHA-256 словарных статей не совпадает с манифестом.");
            }

            var map = new Dictionary<string, List<LexicalDefinition>>(StringComparer.OrdinalIgnoreCase);
            var definitionCount = 0;
            foreach (var line in File.ReadLines(entriesPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var lemma = root.GetProperty("lemma").GetString() ?? "";
                if (string.IsNullOrWhiteSpace(lemma) || !lemma.Any(LexicalLanguageDetector.IsCyrillic))
                    return OzhegovPackLoadResult.Missing("Пакет содержит некорректную или нерусскую лемму.");
                var text = root.TryGetProperty("definition", out var d) ? d.GetString() : null;
                if (string.IsNullOrWhiteSpace(text)) continue;
                var pos = LexicalPackLoader.ParsePos(root.TryGetProperty("pos", out var p) ? p.GetString() : null);
                var key = FoldYo(lemma.Trim());
                if (!map.TryGetValue(key, out var list))
                {
                    list = [];
                    map[key] = list;
                }

                list.Add(new LexicalDefinition(
                    text!,
                    pos,
                    "лицензионный пользовательский пакет",
                    SourceId: manifest!.Source,
                    IsHistorical: false));
                definitionCount++;
            }

            if (definitionCount != manifest!.EntryCount)
                return OzhegovPackLoadResult.Missing("Число статей не совпадает с манифестом.");

            return new OzhegovPackLoadResult(true, manifest, map, null);
        }
        catch (Exception)
        {
            return OzhegovPackLoadResult.Missing("Не удалось прочитать лицензионный pack Ожегова.");
        }
    }

    private static bool HasRequiredRightsMetadata(OzhegovPackManifest? manifest)
        => manifest is not null
           && !string.IsNullOrWhiteSpace(manifest.PackId)
           && !string.IsNullOrWhiteSpace(manifest.PackVersion)
           && !string.IsNullOrWhiteSpace(manifest.License)
           && !string.IsNullOrWhiteSpace(manifest.RightsHolder)
           && !string.IsNullOrWhiteSpace(manifest.Source)
           && !string.IsNullOrWhiteSpace(manifest.LicenseEvidence)
           && manifest.LocalUsePermitted
           && manifest.RetrievedAt is not null
           && manifest.EntryCount > 0
           && !string.IsNullOrWhiteSpace(manifest.Sha256)
           && Regex.IsMatch(manifest.Sha256, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    private static string FoldYo(string value)
        => value.Replace('ё', 'е').Replace('Ё', 'Е');
}

public sealed class OzhegovPackLoadResult
{
    public OzhegovPackLoadResult(bool available, OzhegovPackManifest? manifest,
        IReadOnlyDictionary<string, List<LexicalDefinition>>? entries, string? message)
    {
        IsAvailable = available;
        Manifest = manifest;
        Entries = entries ?? new Dictionary<string, List<LexicalDefinition>>();
        StatusMessage = message;
    }

    public bool IsAvailable { get; }
    public OzhegovPackManifest? Manifest { get; }
    public IReadOnlyDictionary<string, List<LexicalDefinition>> Entries { get; }
    public string? StatusMessage { get; }

    public static OzhegovPackLoadResult Missing(string message)
        => new(false, null, null, message);
}

public sealed class LicensedOzhegovDictionaryProvider : IOzhegovDictionaryProvider
{
    private readonly OzhegovPackLoadResult _load;

    public LicensedOzhegovDictionaryProvider(string? directory = null)
    {
        _load = new LicensedOzhegovPackImporter().TryLoad(directory);
        if (_load.IsAvailable)
            CompatibilityLogger.Technical("ozhegov-pack-loaded", $"entries={_load.Entries.Count}");
        else
            CompatibilityLogger.Technical("ozhegov-pack-missing", "ok=0");
    }

    public bool IsAvailable => _load.IsAvailable;
    public string? StatusMessage => _load.StatusMessage
        ?? (IsAvailable ? "Словарь Ожегова доступен (лицензионный pack)." : "Словарь Ожегова не установлен.");

    public IReadOnlyList<LexicalDefinition> LookupDefinitions(string lemma)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(lemma)) return [];
        var key = lemma.Trim().Replace('ё', 'е').Replace('Ё', 'Е');
        return _load.Entries.TryGetValue(key, out var list) ? list : [];
    }
}

/// <summary>
/// Historical dictionary layer (e.g. public-domain Dal edition pack produced by import_dal_dictionary.py).
/// </summary>
public sealed class HistoricalDictionaryProvider : IHistoricalDictionaryProvider
{
    private readonly Dictionary<string, List<LexicalDefinition>> _entries = new(StringComparer.OrdinalIgnoreCase);
    public bool IsAvailable { get; private set; }
    public string? EditionLabel { get; private set; }

    public HistoricalDictionaryProvider(string? directory = null)
    {
        directory ??= Path.Combine(AppContext.BaseDirectory, "resources", "lexical", "historical");
        var manifestPath = Path.Combine(directory, "historical.manifest.json");
        var entriesPath = Path.Combine(directory, "historical.entries.jsonl");
        if (!File.Exists(manifestPath) || !File.Exists(entriesPath))
        {
            // Also try bundled sample path
            var sample = Path.Combine(AppContext.BaseDirectory, "resources", "lexical", "historical-sample.entries.jsonl");
            var sampleManifest = Path.Combine(AppContext.BaseDirectory, "resources", "lexical", "historical-sample.manifest.json");
            if (File.Exists(sample) && File.Exists(sampleManifest))
            {
                manifestPath = sampleManifest;
                entriesPath = sample;
            }
            else return;
        }

        try
        {
            using var manDoc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            EditionLabel = manDoc.RootElement.TryGetProperty("editionLabel", out var el)
                ? el.GetString()
                : manDoc.RootElement.TryGetProperty("displayName", out var dn) ? dn.GetString() : "Historical dictionary";
            var license = manDoc.RootElement.TryGetProperty("license", out var lic) ? lic.GetString() : null;
            if (string.IsNullOrWhiteSpace(license)) return;

            foreach (var line in File.ReadLines(entriesPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var lemma = root.GetProperty("lemma").GetString() ?? "";
                var text = root.TryGetProperty("definition", out var d) ? d.GetString() : null;
                if (string.IsNullOrWhiteSpace(lemma) || string.IsNullOrWhiteSpace(text)) continue;
                if (!_entries.TryGetValue(lemma, out var list))
                {
                    list = [];
                    _entries[lemma] = list;
                }

                list.Add(new LexicalDefinition(
                    text!,
                    LexicalPackLoader.ParsePos(root.TryGetProperty("pos", out var p) ? p.GetString() : null),
                    "историч.",
                    SourceId: "historical-pd",
                    IsHistorical: true,
                    EraLabel: EditionLabel));
            }

            IsAvailable = _entries.Count > 0;
            CompatibilityLogger.Technical("historical-dict-loaded", $"entries={_entries.Count}");
        }
        catch
        {
            IsAvailable = false;
        }
    }

    public IReadOnlyList<LexicalDefinition> LookupHistoricalDefinitions(string lemma)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(lemma)) return [];
        return _entries.TryGetValue(lemma.Trim(), out var list) ? list : [];
    }
}
