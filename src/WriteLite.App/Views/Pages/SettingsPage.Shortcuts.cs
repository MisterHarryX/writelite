using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WriteLite.Services.Settings;
using AutomationProperties = System.Windows.Automation.AutomationProperties;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBlock = System.Windows.Controls.TextBlock;

namespace WriteLite.Views.Pages;

/// <summary>
/// The shortcuts section of the settings page.
/// </summary>
/// <remarks>
/// <para><b>Why capture rather than a text box.</b> The way anybody expects to change a
/// shortcut is to press it. Typing "Ctrl+Shift+K" into a field asks the user to know the
/// spelling this application happens to use, and it makes every rejected value a spelling
/// argument rather than a statement about the keyboard. The button here listens for one
/// keypress and shows what it heard.</para>
///
/// <para><b>Why the list is built in code.</b> It is one row per entry in
/// <see cref="ShortcutRegistry.Definitions"/>. Writing it in XAML would mean a second list of
/// the same shortcuts that has to be kept in step with the first, which is exactly the
/// arrangement the registry was created to end — a shortcut added to the registry appears
/// here without anyone remembering to add it.</para>
/// </remarks>
public partial class SettingsPage
{
    private ShortcutRegistry _shortcuts = new();

    /// <summary>The row currently listening for a keypress, if any.</summary>
    private Button? _capturing;

    /// <summary>Raised when a shortcut binding changes, so the shell can re-register.</summary>
    public event Action<ShortcutRegistry>? ShortcutsChanged;

    /// <summary>Gives the page the live registry to display and edit.</summary>
    public void BindShortcuts(ShortcutRegistry shortcuts)
    {
        _shortcuts = shortcuts;
        RenderShortcuts();
    }

    private void RenderShortcuts()
    {
        ShortcutList.Children.Clear();
        _capturing = null;

        string? section = null;
        foreach (var definition in ShortcutRegistry.Definitions)
        {
            if (!string.Equals(section, definition.Section, StringComparison.Ordinal))
            {
                section = definition.Section;
                ShortcutList.Children.Add(new TextBlock
                {
                    Text = section,
                    Style = TryFindResource("WlMonoLabel") as Style,
                    Foreground = (Brush)FindResource("WlTextMuted"),
                    Margin = new Thickness(0, 18, 0, 6)
                });
            }

            ShortcutList.Children.Add(BuildShortcutRow(definition));
        }
    }

    private FrameworkElement BuildShortcutRow(ShortcutDefinition definition)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock
        {
            Text = definition.Name,
            Style = TryFindResource("WlBody") as Style,
            TextWrapping = TextWrapping.Wrap
        });

        // Global and local shortcuts behave differently enough that the difference has to be
        // on the row: one fires while another application is in front, the other does not.
        var scope = definition.Scope == ShortcutScope.Global
            ? "Системное — работает в любом приложении"
            : "Внутри WriteLite";

        if (!definition.IsRebindable)
        {
            scope += " · изменить нельзя";
        }

        label.Children.Add(new TextBlock
        {
            Text = scope,
            Style = TryFindResource("WlCaption") as Style,
            TextWrapping = TextWrapping.Wrap
        });

        row.Children.Add(label);

        var gesture = new Button
        {
            Content = _shortcuts.GestureOf(definition.Id),
            Style = TryFindResource("WlSecondaryButton") as Style,
            MinWidth = 132,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = definition.IsRebindable,
            Tag = definition.Id,
            FontFamily = (FontFamily)FindResource("WlFontMono"),
            FontSize = 12
        };

        AutomationProperties.SetName(
            gesture,
            $"{definition.Name}: {_shortcuts.GestureOf(definition.Id)}. Нажмите, чтобы изменить.");

        gesture.Click += (_, _) => BeginCapture(gesture, definition);
        gesture.PreviewKeyDown += (_, args) => OnCaptureKey(gesture, definition, args);
        gesture.LostKeyboardFocus += (_, _) => EndCapture(gesture, definition);
        Grid.SetColumn(gesture, 1);
        row.Children.Add(gesture);

        var reset = new Button
        {
            Content = "✕",
            Style = TryFindResource("WlIconButton") as Style,
            Width = 28,
            Height = 28,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Вернуть исходное сочетание",

            // Shown only where it does something: a shortcut still on its shipped binding has
            // nothing to be reset to, and an always-visible control that is usually inert
            // teaches people to ignore it.
            Visibility = _shortcuts.IsCustomised(definition.Id) ? Visibility.Visible : Visibility.Hidden
        };

        AutomationProperties.SetName(reset, $"Вернуть исходное сочетание: {definition.Name}");
        reset.Click += (_, _) =>
        {
            _shortcuts.ResetToDefault(definition.Id);
            PersistShortcuts();
            RenderShortcuts();
            ShowShortcutHint($"Восстановлено: {definition.Name} — {_shortcuts.GestureOf(definition.Id)}", isError: false);
        };

        Grid.SetColumn(reset, 2);
        row.Children.Add(reset);

        return row;
    }

    private void BeginCapture(Button gesture, ShortcutDefinition definition)
    {
        if (!definition.IsRebindable) return;

        _capturing = gesture;
        gesture.Content = "Нажмите клавиши…";
        gesture.Focus();
        ShowShortcutHint(string.Empty, isError: false);
    }

    private void EndCapture(Button gesture, ShortcutDefinition definition)
    {
        if (!ReferenceEquals(_capturing, gesture)) return;

        _capturing = null;
        gesture.Content = _shortcuts.GestureOf(definition.Id);
    }

    /// <summary>
    /// Reads one keypress while a row is listening, and either binds it or says why not.
    /// </summary>
    /// <remarks>
    /// The modifier keys themselves are ignored rather than refused, because they arrive
    /// first: someone pressing Ctrl+Shift+K sends Ctrl, then Shift, then K, and rejecting
    /// each of the first two would flash two errors on the way to a perfectly good binding.
    /// </remarks>
    private void OnCaptureKey(Button gesture, ShortcutDefinition definition, KeyEventArgs args)
    {
        if (!ReferenceEquals(_capturing, gesture)) return;

        args.Handled = true;

        // WPF reports the key held with Alt as Key.System and puts the real one here.
        var key = args.Key == Key.System ? args.SystemKey : args.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin)
        {
            return;
        }

        if (key == Key.Escape)
        {
            EndCapture(gesture, definition);
            Keyboard.ClearFocus();
            return;
        }

        var result = _shortcuts.TryRebind(definition.Id, key, Keyboard.Modifiers);
        _capturing = null;

        if (!result.Succeeded)
        {
            gesture.Content = _shortcuts.GestureOf(definition.Id);
            ShowShortcutHint(result.Message, isError: true);
            return;
        }

        PersistShortcuts();
        RenderShortcuts();
        ShowShortcutHint(
            $"{definition.Name} — {_shortcuts.GestureOf(definition.Id)}",
            isError: false);
    }

    private void ResetShortcuts_Click(object sender, RoutedEventArgs e)
    {
        _shortcuts.ResetAll();
        PersistShortcuts();
        RenderShortcuts();
        ShowShortcutHint("Все сочетания возвращены к исходным.", isError: false);
    }

    /// <summary>
    /// Writes the changed bindings into settings and tells the shell to re-register.
    /// </summary>
    /// <remarks>
    /// Saved on every change rather than on leaving the page: a shortcut someone set and then
    /// closed the window on has to still be theirs, and there is no Apply button here to hang
    /// the write on.
    /// </remarks>
    private void PersistShortcuts()
    {
        _settings.Shortcuts = new Dictionary<string, string>(_shortcuts.Overrides, StringComparer.Ordinal);
        SettingsChanged?.Invoke(_settings);
        ShortcutsChanged?.Invoke(_shortcuts);
    }

    private void ShowShortcutHint(string message, bool isError)
    {
        ShortcutHint.Text = message;
        ShortcutHint.Foreground = isError
            ? (Brush)FindResource("WlWarning")
            : (Brush)FindResource("WlTextMuted");
    }
}
