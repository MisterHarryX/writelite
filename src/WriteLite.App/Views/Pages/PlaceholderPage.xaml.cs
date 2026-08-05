using System.Windows;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Views.Pages;

public partial class PlaceholderPage : UserControl
{
    private Action? _action;

    public PlaceholderPage()
    {
        InitializeComponent();
    }

    public void Configure(string title, string description, string? actionLabel = null, Action? action = null)
    {
        TitleText.Text = title;
        DescriptionText.Text = description;
        _action = action;
        if (!string.IsNullOrWhiteSpace(actionLabel) && action is not null)
        {
            ActionButton.Content = actionLabel;
            ActionButton.Visibility = Visibility.Visible;
        }
        else
        {
            ActionButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _action?.Invoke();
        }
        catch
        {
            // non-fatal
        }
    }
}
