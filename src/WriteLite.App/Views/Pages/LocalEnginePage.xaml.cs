using System.Windows;
using WriteLite.Resources;
using WriteLite.Services.Settings;
using TextBlock = System.Windows.Controls.TextBlock;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Views.Pages;

/// <summary>Read-only status view of the offline engine. Editing lives in Settings.</summary>
public partial class LocalEnginePage : UserControl
{
    private WriteLiteAppSettings? _settings;

    public LocalEnginePage()
    {
        InitializeComponent();
    }

    public event Action<string>? NavigationRequested;

    public void Bind(WriteLiteAppSettings settings)
    {
        _settings = settings;
        Reload();
    }

    /// <summary>Mirrors the engine status string the shell already receives.</summary>
    public void SetEngineStatus(string status)
    {
        StateDetail.Text = status;

        // The status line is user-facing prose, so treat "unavailable" wording as the
        // only degraded signal rather than parsing it for structure.
        var degraded = status.Contains("недоступ", StringComparison.OrdinalIgnoreCase)
                       || status.Contains("не удалось", StringComparison.OrdinalIgnoreCase)
                       || status.Contains("ошибк", StringComparison.OrdinalIgnoreCase);

        StateDot.Fill = (System.Windows.Media.Brush)FindResource(degraded ? "WlWarning" : "WlSuccess");
        StateText.Text = degraded ? Strings.LocalAi_DegradedState : Strings.LocalAi_EngineActive;
    }

    private void Reload()
    {
        if (_settings is null)
        {
            return;
        }

        RowLocalAi.Control = Value(_settings.LocalAiEnabled ? Strings.LocalAi_Enabled : Strings.LocalAi_Disabled);
        RowProfile.Control = Value(_settings.LocalAiProfile);
        RowEndpoint.Control = Value(_settings.QwenEndpoint);
        ModelText.Text = _settings.LocalAiEnabled ? string.Format(Strings.LocalAi_ModelFormat, _settings.LocalAiProfile) : Strings.LocalAi_NotUsed;
    }

    private TextBlock Value(string text) => new()
    {
        Text = text,
        Style = (Style)FindResource("WlMonoValue"),
        Foreground = (System.Windows.Media.Brush)FindResource("WlText")
    };

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => NavigationRequested?.Invoke("settings");
}
