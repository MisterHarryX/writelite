using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace WriteLite.Views;

/// <summary>
/// Asks for one short line of text — a project name, a bookmark's title.
/// </summary>
/// <remarks>
/// Built in code and styled from the theme, the same way <c>InstructionPrompt</c> is:
/// a WinForms input box would be the only control in the product that looks like
/// another application, and a XAML window for a single field is more file than the
/// job deserves.
/// </remarks>
public sealed class TextPromptWindow : Window
{
    private readonly TextBox _input;

    public TextPromptWindow(string prompt, string? initial = null, string? hint = null)
    {
        Title = prompt;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        AutomationProperties.SetName(this, prompt);

        _input = new TextBox
        {
            Text = initial ?? string.Empty,
            Style = TryFindResource("WlTextInput") as Style,
            MaxLength = 200
        };

        AutomationProperties.SetName(_input, prompt);

        var stack = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };

        stack.Children.Add(new TextBlock
        {
            Text = prompt,
            Margin = new Thickness(0, 0, 0, hint is null ? 10 : 6),
            Style = TryFindResource("WlCardTitle") as Style
        });

        if (hint is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = hint,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                Style = TryFindResource("WlCaption") as Style
            });
        }

        stack.Children.Add(_input);

        var ok = new Button
        {
            Content = "Готово",
            IsDefault = true,
            MinHeight = 30,
            Style = TryFindResource("WlPrimaryButton") as Style
        };

        ok.Click += (_, _) => DialogResult = true;

        var cancel = new Button
        {
            Content = "Отмена",
            IsCancel = true,
            Margin = new Thickness(0, 0, 8, 0),
            Style = TryFindResource("WlTextButton") as Style
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };

        actions.Children.Add(cancel);
        actions.Children.Add(ok);
        stack.Children.Add(actions);

        Content = new Border
        {
            Background = TryFindResource("WlBgBase") as System.Windows.Media.Brush,
            BorderBrush = TryFindResource("WlLineStrong") as System.Windows.Media.Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = stack
        };

        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };

        KeyDown += OnKeyDown;
    }

    /// <summary>What was typed. Trimmed; empty when the dialog was cancelled.</summary>
    public string Value => DialogResult == true ? (_input.Text ?? string.Empty).Trim() : string.Empty;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }
}
