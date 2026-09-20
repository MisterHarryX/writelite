using WriteLite.Services.Settings;

namespace WriteLite.Tests.Settings;

/// <summary>
/// The installer and the Settings page both write the HKCU Run value. These pin down
/// which one wins at startup, and in particular that a fresh install's checkbox
/// survives the first launch.
/// </summary>
[TestClass]
public sealed class AutostartReconcilerTests
{
    /// <summary>
    /// The release-blocking case: the installer ticked «Запускать вместе с Windows»,
    /// so the Run value exists before settings.json does. Pushing the default
    /// StartWithWindows=false into the registry here would delete it.
    /// </summary>
    [TestMethod]
    public void FirstLaunch_AdoptsInstallerRunValue_InsteadOfDeletingIt()
    {
        var autostart = new FakeAutostart { IsEnabled = true, CanEnableForCurrentBinary = true };
        var settings = new WriteLiteAppSettings { StartWithWindows = false };

        var changed = AutostartReconciler.Reconcile(settings, autostart, hasPersistedSettings: false);

        Assert.IsTrue(changed, "settings should be updated so the adopted value is saved");
        Assert.IsTrue(settings.StartWithWindows, "the installer's choice must be adopted");
        Assert.IsTrue(autostart.IsEnabled, "the Run value must survive the first launch");
        Assert.IsEmpty(autostart.Writes, "first launch must not write the registry at all");
    }

    [TestMethod]
    public void FirstLaunch_WithoutInstallerRunValue_LeavesAutostartOff()
    {
        var autostart = new FakeAutostart { IsEnabled = false, CanEnableForCurrentBinary = true };
        var settings = new WriteLiteAppSettings { StartWithWindows = false };

        var changed = AutostartReconciler.Reconcile(settings, autostart, hasPersistedSettings: false);

        Assert.IsFalse(changed, "nothing changed, so nothing needs saving");
        Assert.IsFalse(settings.StartWithWindows);
        Assert.IsFalse(autostart.IsEnabled);
    }

    [TestMethod]
    public void ReturningLaunch_StoredPreferenceWins_AndRewritesRunValue()
    {
        var autostart = new FakeAutostart { IsEnabled = false, CanEnableForCurrentBinary = true };
        var settings = new WriteLiteAppSettings { StartWithWindows = true };

        var changed = AutostartReconciler.Reconcile(settings, autostart, hasPersistedSettings: true);

        Assert.IsFalse(changed, "settings were authoritative and were not modified");
        Assert.IsTrue(settings.StartWithWindows);
        Assert.IsTrue(autostart.IsEnabled, "a Run value removed behind our back is restored");
        CollectionAssert.AreEqual(new[] { true }, autostart.Writes.ToArray());
    }

    /// <summary>
    /// Turning the toggle off must still clear the Run value — the original behaviour
    /// this change must not regress.
    /// </summary>
    [TestMethod]
    public void ReturningLaunch_DisabledPreference_ClearsStaleRunValue()
    {
        var autostart = new FakeAutostart { IsEnabled = true, CanEnableForCurrentBinary = true };
        var settings = new WriteLiteAppSettings { StartWithWindows = false };

        AutostartReconciler.Reconcile(settings, autostart, hasPersistedSettings: true);

        Assert.IsFalse(autostart.IsEnabled);
        CollectionAssert.AreEqual(new[] { false }, autostart.Writes.ToArray());
    }

    /// <summary>
    /// Under `dotnet run` the host is dotnet.exe. Writing would register the wrong
    /// binary and clearing would break a real installation on the same machine.
    /// </summary>
    [TestMethod]
    public void UnpublishedBinary_TouchesNeitherSide()
    {
        var autostart = new FakeAutostart { IsEnabled = true, CanEnableForCurrentBinary = false };
        var settings = new WriteLiteAppSettings { StartWithWindows = false };

        var changed = AutostartReconciler.Reconcile(settings, autostart, hasPersistedSettings: false);

        Assert.IsFalse(changed);
        Assert.IsFalse(settings.StartWithWindows);
        Assert.IsTrue(autostart.IsEnabled, "an installed copy's Run value must be left alone");
        Assert.IsEmpty(autostart.Writes);
    }

    /// <summary>
    /// The upgrade case the Run value alone cannot express: the user has had WriteLite
    /// before with autostart off, and has just ticked the installer's checkbox. Their own
    /// older answer must not undo the one they gave a minute ago.
    /// </summary>
    [TestMethod]
    public void InstallerChoiceBeatsAnOlderStoredPreference()
    {
        var autostart = new FakeAutostart { IsEnabled = true, CanEnableForCurrentBinary = true };
        var settings = new WriteLiteAppSettings { StartWithWindows = false };

        var changed = AutostartReconciler.Reconcile(
            settings, autostart, hasPersistedSettings: true, installerRequest: true);

        Assert.IsTrue(changed, "the adopted choice has to be written to settings");
        Assert.IsTrue(settings.StartWithWindows);
        Assert.IsTrue(autostart.IsEnabled, "the Run value the installer wrote must stay");
        CollectionAssert.AreEqual(new[] { true }, autostart.Writes.ToArray());
    }

    /// <summary>
    /// And the other direction: leaving the box unticked on a reinstall turns autostart
    /// off for someone who had it on, rather than silently keeping it.
    /// </summary>
    [TestMethod]
    public void InstallerDeclineTurnsAutostartOff()
    {
        var autostart = new FakeAutostart { IsEnabled = true, CanEnableForCurrentBinary = true };
        var settings = new WriteLiteAppSettings { StartWithWindows = true };

        var changed = AutostartReconciler.Reconcile(
            settings, autostart, hasPersistedSettings: true, installerRequest: false);

        Assert.IsTrue(changed);
        Assert.IsFalse(settings.StartWithWindows);
        Assert.IsFalse(autostart.IsEnabled);
    }

    /// <summary>
    /// An installer note that agrees with settings still applies the Run value but needs
    /// no save — a launch that changes nothing should not rewrite settings.json.
    /// </summary>
    [TestMethod]
    public void InstallerChoiceMatchingSettings_AppliesWithoutSaving()
    {
        var autostart = new FakeAutostart { IsEnabled = false, CanEnableForCurrentBinary = true };
        var settings = new WriteLiteAppSettings { StartWithWindows = true };

        var changed = AutostartReconciler.Reconcile(
            settings, autostart, hasPersistedSettings: true, installerRequest: true);

        Assert.IsFalse(changed);
        Assert.IsTrue(autostart.IsEnabled);
    }

    /// <summary>
    /// The registry names the installer writes and the application reads are one
    /// contract in two files. If either side is renamed, this fails.
    /// </summary>
    [TestMethod]
    public void HandoffRegistryNamesMatchTheInstallerScript()
    {
        Assert.AreEqual(@"Software\WriteLite\Setup", WriteLiteSetupHandoff.KeyPath);
        Assert.AreEqual("AutostartRequested", WriteLiteSetupHandoff.AutostartValueName);

        var iss = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "installer", "WriteLite.iss");
        if (!File.Exists(iss))
        {
            Assert.Inconclusive($"installer script not reachable from the test output: {iss}");
        }

        var text = File.ReadAllText(iss);
        StringAssert.Contains(text, $"#define SetupKey       \"{WriteLiteSetupHandoff.KeyPath}\"");
        StringAssert.Contains(text, $"#define AutostartValue \"{WriteLiteSetupHandoff.AutostartValueName}\"");
        StringAssert.Contains(
            text,
            $"#define RunValueName   \"{WriteLiteAutostartService.RunValueName}\"");
    }

    /// <summary>
    /// The store must be able to answer "has the user ever had settings" before Load()
    /// flattens that into defaults.
    /// </summary>
    [TestMethod]
    public void SettingsStore_ReportsWhetherFileExists()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "WriteLite-reconciler-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new WriteLiteSettingsStore(path);
        try
        {
            Assert.IsFalse(store.HasPersistedSettings, "no file yet");
            store.Save(new WriteLiteAppSettings { StartWithWindows = true });
            Assert.IsTrue(store.HasPersistedSettings, "file written");
            Assert.IsTrue(store.Load().StartWithWindows);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    private sealed class FakeAutostart : IWriteLiteAutostartService
    {
        public bool IsEnabled { get; set; }

        public bool CanEnableForCurrentBinary { get; set; } = true;

        public string StatusMessage => IsEnabled ? "on" : "off";

        /// <summary>Every SetEnabled call, so "did not touch the registry" is assertable.</summary>
        public List<bool> Writes { get; } = [];

        public void SetEnabled(bool enabled)
        {
            Writes.Add(enabled);
            IsEnabled = enabled;
        }
    }
}
