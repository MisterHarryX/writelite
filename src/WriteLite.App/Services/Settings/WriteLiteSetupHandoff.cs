using Microsoft.Win32;

namespace WriteLite.Services.Settings;

/// <summary>
/// A one-shot message from the Windows installer to the application.
/// </summary>
/// <remarks>
/// The installer's «Запускать WriteLite вместе с Windows» checkbox and the Settings
/// page toggle write the same HKCU Run value, but they cannot see each other. The Run
/// value alone is not enough to tell them apart: on an upgrade, a user whose stored
/// preference says «off» would have the box they just ticked overwritten by their own
/// older answer on the next launch.
///
/// So the installer also leaves a note saying what was chosen <em>during this install</em>,
/// which is by definition newer than anything in settings.json. The application reads it
/// once, folds it into settings, and deletes it — the note is a handoff, not a setting,
/// and leaving it behind would make every later launch re-apply a stale install choice.
/// </remarks>
public interface IWriteLiteSetupHandoff
{
    /// <summary>The installer's autostart choice, or null when there is no pending note.</summary>
    bool? ReadAutostartRequest();

    /// <summary>Removes the note so it is acted on exactly once.</summary>
    void Clear();
}

/// <inheritdoc />
public sealed class WriteLiteSetupHandoff : IWriteLiteSetupHandoff
{
    /// <summary>Must match the [Registry] section of installer/WriteLite.iss.</summary>
    public const string KeyPath = @"Software\WriteLite\Setup";

    /// <summary>Must match the [Registry] section of installer/WriteLite.iss.</summary>
    public const string AutostartValueName = "AutostartRequested";

    public bool? ReadAutostartRequest()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            var raw = key?.GetValue(AutostartValueName);
            return raw switch
            {
                int i => i != 0,
                string s when int.TryParse(s, out var parsed) => parsed != 0,
                _ => null,
            };
        }
        catch (Exception exception)
        {
            // Policy or permissions can refuse the read. There is then simply no note.
            CompatibilityLogger.Technical("setup-handoff-read-failed", $"type={exception.GetType().Name}");
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(AutostartValueName, throwOnMissingValue: false);
        }
        catch (Exception exception)
        {
            // Worst case the note is read again next launch and the same choice re-applied.
            CompatibilityLogger.Technical("setup-handoff-clear-failed", $"type={exception.GetType().Name}");
        }
    }
}
