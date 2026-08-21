using WriteLite.Services.Settings;

namespace WriteLite.Tests.Settings;

[TestClass]
public sealed class WriteLiteSettingsStoreTests
{
    [TestMethod]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var path = TempPath();
        var store = new WriteLiteSettingsStore(path);
        var s = store.Load();
        Assert.IsTrue(s.CheckingEnabled);
        Assert.IsTrue(s.ShowIndicator);
        Assert.IsTrue(s.ExtendedChecking);
        Assert.AreEqual(400, s.AnalysisDelayMs);
        Assert.AreEqual("ru-RU", s.Language);
    }

    [TestMethod]
    public void SaveAndLoad_RoundTrips()
    {
        var path = TempPath();
        var store = new WriteLiteSettingsStore(path);
        var s = new WriteLiteAppSettings
        {
            CheckingEnabled = false,
            ShowIndicator = false,
            ExtendedChecking = false,
            EnableGrammar = false,
            AnalysisDelayMs = 750,
            MaxTextLength = 5000,
            StartWithWindows = true,
            OpenMainWindowOnStart = true,
            MinimizeToTrayOnClose = false,
            HidePanelAfterSuccessfulApply = false
        };
        store.Save(s);
        var loaded = store.Load();
        Assert.IsFalse(loaded.CheckingEnabled);
        Assert.IsFalse(loaded.ShowIndicator);
        Assert.IsFalse(loaded.ExtendedChecking);
        Assert.IsFalse(loaded.EnableGrammar);
        Assert.AreEqual(750, loaded.AnalysisDelayMs);
        Assert.AreEqual(5000, loaded.MaxTextLength);
        Assert.IsTrue(loaded.StartWithWindows);
        Assert.IsTrue(loaded.OpenMainWindowOnStart);
        Assert.IsFalse(loaded.MinimizeToTrayOnClose);
        Assert.IsFalse(loaded.HidePanelAfterSuccessfulApply);
    }

    [TestMethod]
    public void Load_CorruptJson_ReturnsDefaults()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not-json !!!");
        var store = new WriteLiteSettingsStore(path);
        var s = store.Load();
        Assert.IsTrue(s.CheckingEnabled);
        Assert.IsTrue(s.ExtendedChecking);
    }

    [TestMethod]
    public void Load_UnknownFields_Ignored()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "CheckingEnabled": false,
              "FutureFieldOnly": "hello",
              "SchemaVersion": 99
            }
            """);
        var store = new WriteLiteSettingsStore(path);
        var s = store.Load();
        Assert.IsFalse(s.CheckingEnabled);
        Assert.AreEqual(99, s.SchemaVersion);
    }

    [TestMethod]
    public void Save_NormalizesBounds()
    {
        var path = TempPath();
        var store = new WriteLiteSettingsStore(path);
        store.Save(new WriteLiteAppSettings
        {
            AnalysisDelayMs = 1,
            MaxTextLength = 10,
            Language = "en-US"
        });
        var s = store.Load();
        Assert.IsGreaterThanOrEqualTo(100, s.AnalysisDelayMs);
        Assert.IsGreaterThanOrEqualTo(500, s.MaxTextLength);
        Assert.AreEqual("ru-RU", s.Language);
    }

    [TestMethod]
    public void Save_IsAtomic_NoTmpLeft()
    {
        var path = TempPath();
        var store = new WriteLiteSettingsStore(path);
        store.Save(new WriteLiteAppSettings());
        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(File.Exists(path + ".tmp"));
    }

    [TestMethod]
    public void Load_LegacySchema_MigratesAiDefaults()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "CheckingEnabled": true,
              "SchemaVersion": 1,
              "LocalAiEnabled": false,
              "LocalAiProfile": "Auto",
              "QwenEndpoint": ""
            }
            """);

        var store = new WriteLiteSettingsStore(path);
        var s = store.Load();

        Assert.AreEqual(WriteLiteSettingsStore.CurrentSchemaVersion, s.SchemaVersion);
        Assert.IsTrue(s.LocalAiEnabled);
        Assert.IsTrue(s.PreferQwen);
        Assert.AreEqual("Standard", s.LocalAiProfile);
        Assert.AreEqual("http://127.0.0.1:8742", s.QwenEndpoint);
    }

    [TestMethod]
    public void Load_Schema2_ExplicitLocalValues_PreservedDuringMigration()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "SchemaVersion": 2,
              "LocalAiEnabled": false,
              "PreferQwen": false,
              "LocalAiProfile": "Lite",
              "QwenEndpoint": "http://127.0.0.1:8742"
            }
            """);

        var store = new WriteLiteSettingsStore(path);
        var s = store.Load();

        Assert.IsFalse(s.LocalAiEnabled);
        Assert.IsFalse(s.PreferQwen);
        Assert.AreEqual("Lite", s.LocalAiProfile);
        Assert.AreEqual(WriteLiteSettingsStore.CurrentSchemaVersion, s.SchemaVersion);
    }

    [TestMethod]
    public void Load_Schema2_MissingFields_KeepsDefaults()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "SchemaVersion": 2,
              "CheckingEnabled": true
            }
            """);

        var store = new WriteLiteSettingsStore(path);
        var s = store.Load();

        Assert.IsTrue(s.LocalAiEnabled);
        Assert.IsTrue(s.PreferQwen);
        Assert.AreEqual("Standard", s.LocalAiProfile);
        Assert.AreEqual("http://127.0.0.1:8742", s.QwenEndpoint);
    }

    [TestMethod]
    public void Load_UnknownProfile_ReplacedWithStandard()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "SchemaVersion": 2,
              "LocalAiProfile": "Turbo"
            }
            """);

        var store = new WriteLiteSettingsStore(path);
        var s = store.Load();

        Assert.AreEqual("Standard", s.LocalAiProfile);
    }

    [TestMethod]
    public void Load_EmptyEndpoint_ReplacedWithDefault()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "SchemaVersion": 2,
              "QwenEndpoint": "   "
            }
            """);

        var store = new WriteLiteSettingsStore(path);
        var s = store.Load();

        Assert.AreEqual("http://127.0.0.1:8742", s.QwenEndpoint);
    }

    [TestMethod]
    public void Load_NonLoopbackQwenEndpoint_ReplacedWithDefault()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "SchemaVersion": 3,
              "QwenEndpoint": "https://example.com/v1"
            }
            """);

        var s = new WriteLiteSettingsStore(path).Load();

        Assert.AreEqual("http://127.0.0.1:8742", s.QwenEndpoint);
    }

    [TestMethod]
    public void Load_Schema2_StripsRetiredCloudFields()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "SchemaVersion": 2,
              "LocalAiEnabled": false,
              "CloudAiEnabled": true,
              "CloudEndpoint": "https://example.com/api/analyze",
              "CloudTimeoutSeconds": 70
            }
            """);

        var settings = new WriteLiteSettingsStore(path).Load();
        var migratedJson = File.ReadAllText(path);

        Assert.AreEqual(WriteLiteSettingsStore.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.IsFalse(settings.LocalAiEnabled);
        Assert.DoesNotContain("CloudAiEnabled", migratedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("CloudEndpoint", migratedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("CloudTimeoutSeconds", migratedJson, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Load_CorruptJson_CreatesBackup()
    {
        var path = TempPath();
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, "{ not-json !!!");
        var backupsBefore = Directory.GetFiles(dir, "settings.corrupt.*.bak").Length;

        var store = new WriteLiteSettingsStore(path);
        var s = store.Load();

        Assert.IsTrue(s.CheckingEnabled);
        Assert.IsTrue(s.LocalAiEnabled);
        var backupsAfter = Directory.GetFiles(dir, "settings.corrupt.*.bak").Length;
        Assert.AreEqual(backupsBefore + 1, backupsAfter);
    }

    [TestMethod]
    public void Save_WritesCurrentSchemaVersion()
    {
        var path = TempPath();
        var store = new WriteLiteSettingsStore(path);
        store.Save(new WriteLiteAppSettings());
        var json = File.ReadAllText(path);
        // Schema 4 renames the local-AI keys to WriteAI.
        StringAssert.Contains(json, """"SchemaVersion": 4"""");
    }

    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), "wl-settings-" + Guid.NewGuid().ToString("N") + ".json");
}
