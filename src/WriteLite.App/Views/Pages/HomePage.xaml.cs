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

    private void OpenEditor_Click(object sender, RoutedEventArgs e) => NavigationRequested?.Invoke("editor");

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => NavigationRequested?.Invoke("settings");
}
