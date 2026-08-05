using System.IO;
using System.Text.Json;

namespace WriteLite.Services.Settings;

public interface IWriteLiteSettingsStore
{
    WriteLiteAppSettings Load();
    void Save(WriteLiteAppSettings settings);
    string StorePath { get; }
}

public sealed class WriteLiteSettingsStore : IWriteLiteSettingsStore
{
    public const int CurrentSchemaVersion = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // Unknown future fields are ignored by default on deserialize into typed class.
    };

    private static readonly HashSet<string> KnownProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Lite",
        "Standard",
        "Quality",
        "Auto"
    };

    private readonly string _path;

    public WriteLiteSettingsStore(string? path = null)
    {
        _path = path ?? GetDefaultPath();
    }

    public string StorePath => _path;

    public static string GetDefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WriteLite",
            "settings.json");

    public WriteLiteAppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                var fresh = new WriteLiteAppSettings();
                Normalize(fresh);
                return fresh;
            }

            var json = File.ReadAllText(_path);
            WriteLiteAppSettings settings;
            bool migrated;
            try
            {
                using var doc = JsonDocument.Parse(json);
                settings = DeserializeWithMigration(doc, out migrated);
            }
            catch (JsonException)
            {
                BackupCorruptFile(_path);
                CompatibilityLogger.Technical("settings-load-corrupt", "backup-created");
                return new WriteLiteAppSettings();
            }

            Normalize(settings);

            if (migrated)
            {
                try
                {
                    Save(settings);
                }
                catch
                {
                    // migration save is best-effort
                }

                CompatibilityLogger.Technical(
                    "settings-migrated",
                    $"schemaVersion={settings.SchemaVersion} localAi={(settings.LocalAiEnabled ? 1 : 0)} profile={settings.LocalAiProfile} endpoint={SanitizeEndpoint(settings.QwenEndpoint)}");
            }

            return settings;
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("settings-load-failed", $"type={ex.GetType().Name}");
            return new WriteLiteAppSettings();
        }
    }

    public void Save(WriteLiteAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = Normalize(settings);

        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);

        // Atomic replace when possible.
        if (File.Exists(_path))
        {
            File.Replace(temp, _path, null);
        }
        else
        {
            File.Move(temp, _path);
        }
    }

    private static WriteLiteAppSettings DeserializeWithMigration(JsonDocument doc, out bool migrated)
    {
        migrated = false;
        var root = doc.RootElement;
        var schemaVersion = 1;
        if (root.TryGetProperty("schemaVersion", out var svProp)
            && svProp.ValueKind == JsonValueKind.Number)
        {
            schemaVersion = svProp.GetInt32();
        }
        else if (root.TryGetProperty("SchemaVersion", out var svPropAlt)
                 && svPropAlt.ValueKind == JsonValueKind.Number)
        {
            schemaVersion = svPropAlt.GetInt32();
        }

        var settings = JsonSerializer.Deserialize<WriteLiteAppSettings>(doc, JsonOptions)
                       ?? new WriteLiteAppSettings();

        if (schemaVersion < 2)
        {
            // Schema 1 pre-dates the local AI option semantics.
            ApplyLegacyAiDefaults(settings);
        }

        if (schemaVersion < CurrentSchemaVersion)
        {
            // Re-serializing schema 2 strips retired external-provider fields.
            settings.SchemaVersion = CurrentSchemaVersion;
            migrated = true;
        }

        return settings;
    }

    private static void ApplyLegacyAiDefaults(WriteLiteAppSettings s)
    {
        s.LocalAiEnabled = true;
        s.PreferQwen = true;
        s.LocalAiProfile = "Standard";
        s.QwenEndpoint = "http://127.0.0.1:8742";
        s.SchemaVersion = CurrentSchemaVersion;
    }

    private static WriteLiteAppSettings Normalize(WriteLiteAppSettings s)
    {
        s.AnalysisDelayMs = Math.Clamp(s.AnalysisDelayMs, 100, 5000);
        s.MaxTextLength = Math.Clamp(s.MaxTextLength, 500, 100_000);
        s.Language = "ru-RU";
        s.SchemaVersion = Math.Max(CurrentSchemaVersion, s.SchemaVersion);
        s.AiDebounceMs = Math.Clamp(s.AiDebounceMs, 1500, 2000);
        s.AiMinTextLength = Math.Clamp(s.AiMinTextLength, 4, 200);
        s.AiMaxTextLength = Math.Clamp(s.AiMaxTextLength, 200, 20_000);

        var profile = (s.LocalAiProfile ?? string.Empty).Trim();
        s.LocalAiProfile = KnownProfiles.Contains(profile) ? profile : "Standard";

        var endpoint = (s.QwenEndpoint ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            || endpointUri.Scheme != Uri.UriSchemeHttp
            || endpointUri.Host is not ("127.0.0.1" or "localhost" or "::1"))
        {
            endpoint = "http://127.0.0.1:8742";
        }

        s.QwenEndpoint = endpoint;
        return s;
    }

    private static void BackupCorruptFile(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var stamp = DateTimeOffset.Now.ToString("yyyyMMddHHmmssfff");
            var backup = Path.Combine(dir, $"settings.corrupt.{stamp}.bak");
            File.Move(path, backup);
        }
        catch
        {
            // Best-effort backup; do not let backup failure break startup.
        }
    }

    private static string SanitizeEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint.Trim().TrimEnd('/'), UriKind.Absolute, out var uri))
        {
            return "default";
        }

        return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }
}
