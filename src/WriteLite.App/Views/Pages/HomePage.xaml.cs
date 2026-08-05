using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Views.Pages;

public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
    }

    public void SetMonitorActive(bool active)
    {
        MonitorStatusText.Text = active ? "Активен" : "На паузе";
    }

    public void SetEngineStatus(string status)
    {
        if (FindName("EngineStatusText") is System.Windows.Controls.TextBlock block)
        {
            block.Text = status;
        }
    }
}
