using System.IO;
using System.Windows;
using System.Windows.Controls;
using WriteLite.Resources;
using WriteLite.Services.Ai;
using WriteLite.Services.Settings;
using UserControl = System.Windows.Controls.UserControl;
using CheckBox = System.Windows.Controls.CheckBox;

namespace WriteLite.Views.Pages;

public partial class SettingsPage : UserControl
{
    private WriteLiteAppSettings _settings = new();
    private IWriteLiteAutostartService? _autostart;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
    }

    public event Action<WriteLiteAppSettings>? SettingsChanged;

    public void Bind(WriteLiteAppSettings settings, IWriteLiteAutostartService autostart)
    {
        _settings = settings;
        _autostart = autostart;
        _loading = true;
        ChkChecking.IsChecked = settings.CheckingEnabled;
        ChkIndicator.IsChecked = settings.ShowIndicator;
        ChkAutostart.IsChecked = settings.StartWithWindows && autostart.CanEnableForCurrentBinary;
        ChkAutostart.IsEnabled = autostart.CanEnableForCurrentBinary;
        RowAutostart.Description = autostart.StatusMessage;
        ChkOpenMain.IsChecked = settings.OpenMainWindowOnStart;
        ChkTrayClose.IsChecked = settings.MinimizeToTrayOnClose;
        ChkExtended.IsChecked = settings.ExtendedChecking;
        ChkOrtho.IsChecked = settings.EnableOrthography;
        ChkPunct.IsChecked = settings.EnablePunctuation;
        ChkGrammar.IsChecked = settings.EnableGrammar;
        ChkStyle.IsChecked = settings.EnableStyle;
        ChkTypo.IsChecked = settings.EnableTypography;
        ChkRepeats.IsChecked = settings.EnableRepeats;
        ChkSafeAll.IsChecked = settings.SafeApplyAll;
        TxtDelay.Text = settings.AnalysisDelayMs.ToString();
        TxtMaxLen.Text = settings.MaxTextLength.ToString();
        ChkGreen.IsChecked = settings.ShowGreenIndicatorWhenClean;
        ChkHidePanel.IsChecked = settings.HidePanelAfterSuccessfulApply;
        ChkOnlyEditing.IsChecked = settings.ShowUiOnlyWhileEditing;
        ChkLexical.IsChecked = settings.LexicalCardEnabled;
        ChkMinimizeDup.IsChecked = settings.MinimizeUnderlineDuplication;
        TxtIdleGrace.Text = settings.EditingIdleGraceSeconds.ToString();
        ChkLocalAi.IsChecked = settings.LocalAiEnabled;
        ChkPreferQwen.IsChecked = settings.PreferQwen;
        SelectProfileItem(settings.LocalAiProfile);
        TxtQwenEndpoint.Text = settings.QwenEndpoint ?? string.Empty;
        UpdateLocalAiHint();
        _loading = false;
    }

    private void SelectProfileItem(string? profile)
    {
        var target = (profile ?? "Standard").Trim();
        foreach (ComboBoxItem item in CmbLocalAiProfile.Items)
        {
            if (string.Equals(item.Content.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                CmbLocalAiProfile.SelectedItem = item;
                return;
            }
        }

        CmbLocalAiProfile.SelectedIndex = 1; // Standard
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.CheckingEnabled = ChkChecking.IsChecked == true;
        _settings.ShowIndicator = ChkIndicator.IsChecked == true;
        _settings.StartWithWindows = ChkAutostart.IsChecked == true;
        _settings.OpenMainWindowOnStart = ChkOpenMain.IsChecked == true;
        _settings.MinimizeToTrayOnClose = ChkTrayClose.IsChecked == true;
        _settings.ExtendedChecking = ChkExtended.IsChecked == true;
        _settings.EnableOrthography = ChkOrtho.IsChecked == true;
        _settings.EnablePunctuation = ChkPunct.IsChecked == true;
        _settings.EnableGrammar = ChkGrammar.IsChecked == true;
        _settings.EnableStyle = ChkStyle.IsChecked == true;
        _settings.EnableTypography = ChkTypo.IsChecked == true;
        _settings.EnableRepeats = ChkRepeats.IsChecked == true;
        _settings.SafeApplyAll = ChkSafeAll.IsChecked == true;
        _settings.ShowGreenIndicatorWhenClean = ChkGreen.IsChecked == true;
        _settings.HidePanelAfterSuccessfulApply = ChkHidePanel.IsChecked == true;
        _settings.ShowUiOnlyWhileEditing = ChkOnlyEditing.IsChecked == true;
        _settings.LexicalCardEnabled = ChkLexical.IsChecked == true;
        _settings.MinimizeUnderlineDuplication = ChkMinimizeDup.IsChecked == true;
        if (int.TryParse(TxtIdleGrace.Text, out var idleSec))
        {
            _settings.EditingIdleGraceSeconds = (int)Math.Clamp(idleSec, 2, 30);
        }
        _settings.LocalAiEnabled = ChkLocalAi.IsChecked == true;
        _settings.PreferQwen = ChkPreferQwen.IsChecked == true;
        _settings.LocalAiProfile = (CmbLocalAiProfile.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Standard";
        _settings.QwenEndpoint = TxtQwenEndpoint.Text?.Trim() ?? string.Empty;

        if (int.TryParse(TxtDelay.Text, out var delay))
        {
            _settings.AnalysisDelayMs = delay;
        }

        if (int.TryParse(TxtMaxLen.Text, out var maxLen))
        {
            _settings.MaxTextLength = maxLen;
        }

        try
        {
            _autostart?.SetEnabled(_settings.StartWithWindows);
            if (_autostart is not null)
            {
                RowAutostart.Description = _autostart.StatusMessage;
            }
        }
        catch
        {
            RowAutostart.Description = Strings.SETTINGS_AUTOSTART_ERROR;
        }

        SettingsChanged?.Invoke(_settings);
    }

    private void UpdateLocalAiHint(string? status = null)
    {
        if (LocalAiStatusHint is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            LocalAiStatusHint.Text = status;
            return;
        }

        LocalAiStatusHint.Text = _settings.WriteAiEnabled
            ? WriteAiStatus.DescribeWithHint(WriteAiState.Ready)
              + Strings.SETTINGS_AI_PROBE_HINT
            : WriteAiStatus.DescribeWithHint(WriteAiState.Disabled);
    }

    private async void ProbeWriteAi_Click(object sender, RoutedEventArgs e)
    {
        if (LocalAiStatusHint is null)
        {
            return;
        }

        LocalAiStatusHint.Text = Strings.SETTINGS_AI_PROBE_CONNECTING;
        var endpoint = TxtQwenEndpoint.Text?.Trim() ?? "http://127.0.0.1:8742";
        var modelDir = Path.Combine(AppContext.BaseDirectory, "models", "writelight-qwen");
        var profileName = (CmbLocalAiProfile.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Standard";
        var profile = profileName.Trim().ToLowerInvariant() switch
        {
            "lite" => WriteLite.AI.Contracts.AiModelProfile.Lite,
            "standard" => WriteLite.AI.Contracts.AiModelProfile.Standard,
            "quality" => WriteLite.AI.Contracts.AiModelProfile.Quality,
            _ => WriteLite.AI.Contracts.AiModelProfile.Standard
        };

        try
        {
            await using var provider = new LocalAiTextProvider(new LocalAiOptions
            {
                Enabled = true,
                Profile = profile,
                ModelDirectory = modelDir,
                QwenEndpoint = endpoint,
                PreferQwen = ChkPreferQwen.IsChecked == true,
                TimeoutSeconds = 30,
                MinInputChars = 1,
                MaxInputChars = 12_000
            });

            await provider.WarmupAsync();
            if (!provider.WriteAiAvailable)
            {
                LocalAiStatusHint.Text = WriteAiStatus.DescribeWithHint(WriteAiState.Unavailable);
                return;
            }

            // Use a fixed non-sensitive test phrase to determine the actual backend.
            const string probeText = "Я сегодня небыл дома.";
            _ = await provider.AnalyzeAsync(probeText);
            LocalAiStatusHint.Text =
                WriteAiStatus.DescribeWithHint(WriteAiState.Ready)
                + string.Format(Strings.SETTINGS_AI_PROBE_MODE, WriteAiStatus.BackendLabel(provider.LastBackend, available: true));
        }
        catch (Exception ex)
        {
            // §54: the transport's own message — a refused socket, a JSON parse failure — is
            // diagnostics, not something to put in front of someone who wanted to know
            // whether the feature works.
            WriteLite.Services.CompatibilityLogger.Technical(
                "writeai-probe-failed", $"type={ex.GetType().Name} message={ex.Message}");
            LocalAiStatusHint.Text = WriteAiStatus.DescribeWithHint(WriteAiState.Unavailable);
        }
    }
}
