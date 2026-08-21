using System.Windows;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Views.Pages;

/// <summary>Text-only section (About, Compatibility) rendered with the page type scale.</summary>
public partial class PlaceholderPage : UserControl
{
    private Action? _action;

    public PlaceholderPage()
    {
        InitializeComponent();
    }

    public void Configure(string index, string title, string body, string? actionLabel = null, Action? action = null)
    {
        Header.Index = index;
        Header.Title = title;
        DescriptionText.Text = body;
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
            // Opening an external file is best-effort; a failure must not take the page down.
        }
    }
}
