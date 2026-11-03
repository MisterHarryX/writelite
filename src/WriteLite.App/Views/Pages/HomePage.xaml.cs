using System.Windows;
using WriteLite.Resources;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Views.Pages;

public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the page wants the shell to select another rail section.</summary>
    public event Action<string>? NavigationRequested;

    public void SetMonitorActive(bool active) => MonitorStatusText.Text = active ? Strings.Home_StatusActive : Strings.Home_StatusPaused;

    public void SetEngineStatus(string status) => EngineStatusText.Text = status;

    /// <summary>Raised when the user takes up the offer to calibrate «Мой стиль».</summary>
    public event Action? StyleCalibrationRequested;

    /// <summary>Raised when the user turns the calibration offer down for good.</summary>
    public event Action? StyleOfferDismissed;

    /// <summary>Shows or hides the calibration offer at the top of the page.</summary>
    public void SetStyleOffer(bool visible) => StyleOfferCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void StartStyleCalibration_Click(object sender, RoutedEventArgs e) => StyleCalibrationRequested?.Invoke();

    private void DismissStyleOffer_Click(object sender, RoutedEventArgs e) => StyleOfferDismissed?.Invoke();

    private void OpenEditor_Click(object sender, RoutedEventArgs e) => NavigationRequested?.Invoke("editor");

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => NavigationRequested?.Invoke("settings");
}
