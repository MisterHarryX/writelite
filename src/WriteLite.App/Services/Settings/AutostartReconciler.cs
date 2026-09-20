namespace WriteLite.Services.Settings;

/// <summary>
/// Decides, at startup, which of the two autostart records wins: the stored
/// preference in settings.json or the HKCU Run value.
/// </summary>
/// <remarks>
/// There are two writers of the Run value and they do not know about each other.
/// The application writes it when the Settings page toggle changes; the Windows
/// installer writes it when «Запускать WriteLite вместе с Windows» is ticked, which
/// happens before the application has ever run and therefore before settings.json
/// exists.
///
/// Pushing settings → registry unconditionally loses that second case: a fresh
/// install has no settings file, <see cref="WriteLiteAppSettings.StartWithWindows"/>
/// reads false, and the first launch deletes the value the installer had just
/// written — the installer checkbox silently stops working after one run.
///
/// So the direction depends on whether a stored preference exists at all. With no
/// settings file this is the first launch after an install and the registry is the
/// only statement of intent anyone has made, so it is adopted into settings. Once a
/// settings file exists the user has had the chance to express a preference in the
/// Settings page, and settings become authoritative again.
/// </remarks>
public static class AutostartReconciler
{
    /// <summary>
    /// Brings <paramref name="settings"/> and the Run value into agreement.
    /// </summary>
    /// <param name="settings">Loaded settings; mutated when the registry wins.</param>
    /// <param name="autostart">The Run-value reader/writer.</param>
    /// <param name="hasPersistedSettings">
    /// Whether a settings file existed before this launch. False means first run.
    /// </param>
    /// <param name="installerRequest">
    /// What the installer's autostart checkbox was set to during the install that
    /// preceded this launch, or null when there is no pending note. See
    /// <see cref="IWriteLiteSetupHandoff"/>.
    /// </param>
    /// <returns>
    /// True when <paramref name="settings"/> was changed and should be saved.
    /// </returns>
    public static bool Reconcile(
        WriteLiteAppSettings settings,
        IWriteLiteAutostartService autostart,
        bool hasPersistedSettings,
        bool? installerRequest = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(autostart);

        // A binary that cannot autostart (dotnet run, where the host is dotnet.exe)
        // must not touch the registry in either direction: the value it would write
        // points at the wrong executable, and the value it would clear belongs to a
        // real installation that happens to share this machine.
        if (!autostart.CanEnableForCurrentBinary)
        {
            return false;
        }

        if (installerRequest is bool requested)
        {
            // The newest statement of intent there is: the user answered it during the
            // install that produced this very binary. It wins over a settings file that
            // predates the install, which is the upgrade case the Run value alone cannot
            // distinguish from a stale entry.
            autostart.SetEnabled(requested);
            if (settings.StartWithWindows == requested)
            {
                return false;
            }

            settings.StartWithWindows = requested;
            return true;
        }

        if (!hasPersistedSettings)
        {
            // First launch after an install. Whatever the installer left in the Run
            // key is the only expressed intent, so record it rather than erase it.
            var fromRegistry = autostart.IsEnabled;
            if (settings.StartWithWindows == fromRegistry)
            {
                return false;
            }

            settings.StartWithWindows = fromRegistry;
            return true;
        }

        // Normal launch: the stored preference is authoritative, and the Run value is
        // rewritten to match it so an externally removed or stale entry is corrected.
        autostart.SetEnabled(settings.StartWithWindows);
        return false;
    }
}
