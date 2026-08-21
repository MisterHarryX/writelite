using System.IO;
using System.Text.Json;
using WriteLite.Services.Settings;

namespace WriteLite.Tests.Settings;

/// <summary>
/// The Phase 7 rename of the local-AI settings keys to WriteAI, from the point of view of
/// someone who already has a settings.json.
/// </summary>
/// <remarks>
/// §4 of the brief: read the old key, migrate once, write the new key, and do not break
/// existing users' configuration. The failure this guards against is quiet — a renamed key
/// that nothing migrates does not throw, it just silently restores a default, and the user
/// finds their «WriteAI off» preference undone after an update.
/// </remarks>
[TestClass]
public sealed class WriteAiSettingsMigrationTests
{
    private string _directory = null!;
    private string _path = null!;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "writelite-writeai-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "settings.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private void WriteFile(string json) => File.WriteAllText(_path, json);

    private WriteLiteAppSettings Load() => new WriteLiteSettingsStore(_path).Load();

    [TestMethod]
    public void AnOldFileWithLocalAiDisabled_KeepsWriteAiDisabled()
    {
        WriteFile("""
            {
              "schemaVersion": 3,
              "localAiEnabled": false,
              "preferQwen": false,
              "qwenEndpoint": "http://127.0.0.1:9001"
            }
            """);

        var settings = Load();

        Assert.IsFalse(settings.WriteAiEnabled, "the user had switched it off");
        Assert.IsFalse(settings.PreferWriteAi);
        Assert.AreEqual("http://127.0.0.1:9001", settings.WriteAiEndpoint);
    }

    [TestMethod]
    public void TheMigratedFileIsRewrittenUnderTheNewKeys()
    {
        WriteFile("""
            {
              "schemaVersion": 3,
              "localAiEnabled": false,
              "preferQwen": false
            }
            """);

        Load();

        using var document = JsonDocument.Parse(File.ReadAllText(_path));
        var root = document.RootElement;

        // Case-insensitively, because the writer uses the property names as declared and the
        // reader is configured to not care — asserting one casing would be asserting a
        // serializer setting rather than the migration.
        Assert.IsTrue(TryGet(root, "writeAiEnabled", out var enabled));
        Assert.IsFalse(enabled.GetBoolean());
        Assert.IsTrue(TryGet(root, "schemaVersion", out var schema));
        Assert.AreEqual(WriteLiteSettingsStore.CurrentSchemaVersion, schema.GetInt32());

        // The old names must not be written back, or the next reader has two sources of truth.
        Assert.IsFalse(TryGet(root, "localAiEnabled", out _));
        Assert.IsFalse(TryGet(root, "preferQwen", out _));
        Assert.IsFalse(TryGet(root, "qwenEndpoint", out _));
    }

    private static bool TryGet(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }

    [TestMethod]
    public void MigrationIsIdempotent()
    {
        WriteFile("""
            {
              "schemaVersion": 3,
              "localAiEnabled": false,
              "qwenEndpoint": "http://127.0.0.1:9001"
            }
            """);

        Load();
        var second = Load();

        Assert.IsFalse(second.WriteAiEnabled);
        Assert.AreEqual("http://127.0.0.1:9001", second.WriteAiEndpoint);
    }

    [TestMethod]
    public void ANewKeyWinsOverAStaleOldOne()
    {
        // A hand-edited file can carry both. The new name is the one this release writes, so
        // it is the one that reflects the user's most recent choice.
        WriteFile("""
            {
              "schemaVersion": 3,
              "localAiEnabled": false,
              "writeAiEnabled": true
            }
            """);

        Assert.IsTrue(Load().WriteAiEnabled);
    }

    [TestMethod]
    public void TheLegacyAiCheckingEnabledKeyIsAlsoHonoured()
    {
        WriteFile("""
            {
              "schemaVersion": 3,
              "aiCheckingEnabled": false
            }
            """);

        Assert.IsFalse(Load().WriteAiEnabled);
    }

    [TestMethod]
    public void AFileAlreadyOnTheNewSchemaIsNotTouchedByTheMigration()
    {
        WriteFile("""
            {
              "schemaVersion": 4,
              "writeAiEnabled": false,
              "localAiEnabled": true
            }
            """);

        // Schema 4 files are past the rename; a stray old key in one is not evidence of intent.
        Assert.IsFalse(Load().WriteAiEnabled);
    }

    [TestMethod]
    public void TheCompatibilityAliasesStayInSyncWithTheNewProperties()
    {
        // ~40 call sites still say LocalAiEnabled. They must see the same value.
        var settings = new WriteLiteAppSettings { WriteAiEnabled = false };
        Assert.IsFalse(settings.LocalAiEnabled);
        Assert.IsFalse(settings.AiCheckingEnabled);

        settings.LocalAiEnabled = true;
        Assert.IsTrue(settings.WriteAiEnabled);

        settings.QwenEndpoint = "http://127.0.0.1:9100";
        Assert.AreEqual("http://127.0.0.1:9100", settings.WriteAiEndpoint);

        settings.PreferQwen = false;
        Assert.IsFalse(settings.PreferWriteAi);
    }

    [TestMethod]
    public void TheAliasesAreNotSerialised()
    {
        // Serialising both names would write the same value twice and leave the next reader
        // unable to tell which one the user last changed.
        var json = JsonSerializer.Serialize(new WriteLiteAppSettings());

        StringAssert.Contains(json, "WriteAiEnabled");
        Assert.IsFalse(json.Contains("LocalAiEnabled", StringComparison.Ordinal), json);
        Assert.IsFalse(json.Contains("QwenEndpoint", StringComparison.Ordinal), json);
        Assert.IsFalse(json.Contains("PreferQwen", StringComparison.Ordinal), json);
        Assert.IsFalse(json.Contains("AiCheckingEnabled", StringComparison.Ordinal), json);
    }

    [TestMethod]
    public void DefaultsMatchTheReleaseConfiguration()
    {
        // §52: the shipping defaults, asserted rather than assumed.
        var settings = new WriteLiteAppSettings();

        Assert.IsTrue(settings.CheckingEnabled, "deterministic correction on");
        Assert.IsTrue(settings.PunctuationModelEnabled, "WriteLite-Punctuation-v1 on");
        Assert.IsTrue(settings.EnableStyle, "style suggestions on");
        Assert.IsTrue(settings.WriteAiEnabled, "WriteAI available for Smart Actions");
        Assert.AreEqual("General", settings.StyleProfile);
    }
}
