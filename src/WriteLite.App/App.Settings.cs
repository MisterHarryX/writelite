using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Automation;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Rules;
using WriteLite.Services.Ai;
using WriteLite.Services.Audio;
using WriteLite.Services.Diagnostics;
using WriteLite.Services.Grammar;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Lexical;
using WriteLite.Services.Notes;
using WriteLite.Services.Reading;
using WriteLite.Language.Core;
using WriteLite.Language.Packs;
using WriteLite.Services.Settings;
using WriteLite.Services.Spelling;
using WriteLite.Views;
using MessageBox = System.Windows.MessageBox;

namespace WriteLite;

public partial class App : System.Windows.Application
{
    private WriteLiteSettingsStore? _settingsStore;

    /// <summary>
    /// Takes a shortcut change to Windows, so a rebound global shortcut works immediately.
    /// </summary>
    /// <remarks>
    /// The registry object is the same instance the editor and the shell already hold, so
    /// application shortcuts need nothing here — they are read from it on every keypress.
    /// Only the system-wide ones are held by Windows and have to be told.
    /// </remarks>
    private void OnShortcutsChanged(ShortcutRegistry registry)
    {
        _shortcuts = registry;
        _hotkeys?.Apply(registry);
    }


    private void OnSettingsChanged(WriteLiteAppSettings settings)
    {
        _settings = settings;
        SaveSettingsQuietly(settings);
        ApplyRuntimeSettings(settings);
    }


    /// <summary>Persists settings without re-applying runtime behaviour.</summary>
    private void SaveSettingsQuietly(WriteLiteAppSettings settings)
    {
        try
        {
            _settingsStore?.Save(settings);
        }
        catch
        {
            CompatibilityLogger.State("settings-save-failed");
        }
    }


    private void ApplyRuntimeSettings(WriteLiteAppSettings settings)
    {
        _orchestrator?.ApplySettings(settings);
        _hybrid?.ApplySettings(settings);
        // Rebuild the local provider when its endpoint/profile changes.
        RebuildAiProvider(settings);
        _mainWindow?.SetMinimizeToTray(settings.MinimizeToTrayOnClose);
        _bubble?.ApplyDisplaySettings(settings.ShowIndicator, settings.ShowGreenIndicatorWhenClean);
        IssueUnderlineTheme.MinimizeDuplication = settings.MinimizeUnderlineDuplication;
        _monitor?.SetShowUiOnlyWhileEditing(settings.ShowUiOnlyWhileEditing);
        _monitor?.SetIdleGrace(UiInteractionStateMachine.FromSecondsClamped(settings.EditingIdleGraceSeconds));
        var delayMs = ResolveAnalysisDelayMs(settings, _aiProvider);
        _monitor?.SetAnalysisDelay(TimeSpan.FromMilliseconds(delayMs));
        _mainWindow?.SetEngineStatus(ResolveEngineStatusText());

        if (_languageEngine is not null)
        {
            // Fire-and-forget is intentional: settings UI must not block on engine stop/start.
            _ = ApplyExtendedCheckingAsync(settings.ExtendedChecking);
        }

        if (_monitor is not null)
        {
            if (settings.CheckingEnabled && !_monitorPaused)
            {
                // Start is idempotent when already running.
                _monitor.Start();
                if (settings.LexicalCardEnabled)
                {
                    _doubleClickObserver?.Start();
                }
                else
                {
                    _doubleClickObserver?.Stop();
                    _lexicalPopup?.Hide();
                }
            }
            else if (!settings.CheckingEnabled)
            {
                _monitor.Stop();
                _doubleClickObserver?.Stop();
                _bubble?.Hide();
                _suggestions?.Hide();
                _lexicalPopup?.Hide();
            }
        }

        if (!settings.ShowIndicator)
        {
            _bubble?.Hide();
        }

        CompatibilityLogger.State("settings-applied");
    }


    private async Task ApplyExtendedCheckingAsync(bool enabled)
    {
        if (_languageEngine is null)
        {
            return;
        }

        try
        {
            await _languageEngine.SetExtendedEnabledAsync(enabled).ConfigureAwait(true);
            _ = Dispatcher.BeginInvoke(() => _mainWindow?.SetEngineStatus(_languageEngine.UserFacingStatus));
        }
        catch
        {
            CompatibilityLogger.State("language-engine-fallback-active");
        }
    }


    private async Task<bool> RestartEngineAsync()
    {
        if (_languageEngine is null)
        {
            return false;
        }

        var ok = await _languageEngine.TryRestartAsync().ConfigureAwait(true);
        _mainWindow?.SetEngineStatus(_languageEngine.UserFacingStatus);
        return ok;
    }


}
