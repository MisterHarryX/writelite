using System.IO;
using System.Text.Json;

namespace WriteLite.Services.Settings;

public interface IWriteLiteSettingsStore
{
    WriteLiteAppSettings Load();
    void Save(WriteLiteAppSettings settings);
    string StorePath { get; }

    /// <summary>
    /// True when a settings file is already on disk, i.e. this is not the first launch.
    /// </summary>
    /// <remarks>
    /// Read before <see cref="Load"/>: a fresh install has no file and <see cref="Load"/>
    /// answers with defaults that are indistinguishable from a user who chose them.
    /// <see cref="AutostartReconciler"/> needs to tell those two apart.
    /// </remarks>
    bool HasPersistedSettings { get; }
}

public sealed class WriteLiteSettingsStore : IWriteLiteSettingsStore
{
    public const int CurrentSchemaVersion = 4;

    /// <summary>
    /// Persisted keys renamed in Phase 7, old name → new name.
    /// </summary>
    /// <remarks>
    /// The product's local generative subsystem is called WriteAI from this release on, and
    /// the settings keys follow. Users have existing files written under the old names, so the
    /// old key is read when the new one is absent, written back under the new name once, and
    /// never looked at again. Losing someone's «WriteAI off» preference to a rename would be a
    /// worse outcome than the rename is worth.
    /// </remarks>
    private static readonly (string Old, string New)[] RenamedKeys =
    [
        ("localAiEnabled", "writeAiEnabled"),
        ("aiCheckingEnabled", "writeAiEnabled"),
        ("preferQwen", "preferWriteAi"),
        ("qwenEndpoint", "writeAiEndpoint"),
    ];

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

    public bool HasPersistedSettings => File.Exists(_path);

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
            CompatibilityLogger.Technical("settings-load-failed", ex);
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

        // Rename first, defaults second. A schema 1 file carries the old key names *and*
        // pre-dates the local AI option semantics, so ApplyLegacyAiDefaults deliberately
        // overrides whatever it says. Running the rename afterwards would copy the file's
        // stale «off» back over the default that had just been chosen for it.
        if (schemaVersion < 4)
        {
            MigrateRenamedKeys(root, settings);
        }

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

    /// <summary>
    /// Copies the pre-Phase-7 local-AI keys onto their WriteAI successors.
    /// </summary>
    /// <remarks>
    /// Only when the new key is absent from the file: once a settings.json has been written by
    /// this release it carries the new names, and a stale old key left over from a
    /// hand-edited file must not overrule a preference the user has since changed. The
    /// property setters are the aliases, so this is a one-line assignment per key rather than
    /// a parallel model.
    /// </remarks>
    private static void MigrateRenamedKeys(JsonElement root, WriteLiteAppSettings settings)
    {
        foreach (var (oldKey, newKey) in RenamedKeys)
        {
            if (TryGetProperty(root, newKey, out _)) continue;
            if (!TryGetProperty(root, oldKey, out var value)) continue;

            switch (newKey)
            {
                case "writeAiEnabled" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    settings.WriteAiEnabled = value.GetBoolean();
                    break;
                case "preferWriteAi" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    settings.PreferWriteAi = value.GetBoolean();
                    break;
                case "writeAiEndpoint" when value.ValueKind == JsonValueKind.String:
                    settings.WriteAiEndpoint = value.GetString() ?? settings.WriteAiEndpoint;
                    break;
            }

            CompatibilityLogger.Technical("settings-key-renamed", $"from={oldKey} to={newKey}");
        }
    }

    /// <summary>Property lookup that tolerates the casing of both writers of this file.</summary>
    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value)) return true;

        foreach (var property in root.EnumerateObject())
        {
            if (!property.NameEquals(name)
                && !string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }

    private static void ApplyLegacyAiDefaults(WriteLiteAppSettings s)
    {
        s.LocalAiEnabled = true;
        s.PreferQwen = true;
        s.LocalAiProfile = "Standard";
        s.QwenEndpoint = WriteLiteDefaults.Model.QwenDefaultEndpoint;
        s.SchemaVersion = CurrentSchemaVersion;
    }

    private static WriteLiteAppSettings Normalize(WriteLiteAppSettings s)
    {
        s.AnalysisDelayMs = Math.Clamp(s.AnalysisDelayMs, WriteLiteDefaults.Debounce.AnalysisDelayMsClampMin, WriteLiteDefaults.Debounce.AnalysisDelayMsClampMax);
        s.MaxTextLength = Math.Clamp(s.MaxTextLength, WriteLiteDefaults.Debounce.MaxTextLengthClampMin, WriteLiteDefaults.Debounce.MaxTextLengthClampMax);
        s.Language = "ru-RU";
        s.SchemaVersion = Math.Max(CurrentSchemaVersion, s.SchemaVersion);
        s.AiDebounceMs = Math.Clamp(s.AiDebounceMs, WriteLiteDefaults.Debounce.AiDebounceMsClampMin, WriteLiteDefaults.Debounce.AiDebounceMsClampMax);
        s.AiMinTextLength = Math.Clamp(s.AiMinTextLength, WriteLiteDefaults.Debounce.AiMinTextLengthClampMin, WriteLiteDefaults.Debounce.AiMinTextLengthClampMax);
        s.AiMaxTextLength = Math.Clamp(s.AiMaxTextLength, WriteLiteDefaults.Debounce.AiMaxTextLengthClampMin, WriteLiteDefaults.Debounce.AiMaxTextLengthClampMax);

        var profile = (s.LocalAiProfile ?? string.Empty).Trim();
        s.LocalAiProfile = KnownProfiles.Contains(profile) ? profile : "Standard";

        var endpoint = (s.QwenEndpoint ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            || endpointUri.Scheme != Uri.UriSchemeHttp
            || endpointUri.Host is not ("127.0.0.1" or "localhost" or "::1"))
        {
            endpoint = WriteLiteDefaults.Model.QwenDefaultEndpoint;
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
