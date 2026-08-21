using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WriteLite.Services.Reading;
using Brush = System.Windows.Media.Brush;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;

namespace WriteLite.Views;

/// <summary>
/// Writes one annotation: the passage, the reader's note, a tint and an optional tag.
/// </summary>
/// <remarks>
/// The quote is shown and not editable. The point of an annotation is that it is
/// attached to words someone else wrote, and a dialog that let those words be changed
/// on the way in would make every stored quote unreliable.
/// </remarks>
public partial class AnnotationEditorWindow : Window
{
    private static readonly (HighlightTint Tint, string Key, string Name)[] Tints =
    [
        (HighlightTint.Amber, "WlMarkAmberSolid", "Оранжевый"),
        (HighlightTint.Sage, "WlMarkSageSolid", "Зелёный"),
        (HighlightTint.Coral, "WlMarkCoralSolid", "Красный"),
        (HighlightTint.Neutral, "WlMarkNeutralSolid", "Нейтральный")
    ];

    public AnnotationEditorWindow(string quote)
    {
        InitializeComponent();

        QuoteText.Text = quote;
        BuildTintPicker();

        Loaded += (_, _) => NoteBox.Focus();
    }

    public string Note { get; private set; } = string.Empty;

    /// <summary>The optional label. Named TagText so it does not hide <c>FrameworkElement.Tag</c>.</summary>
    public string? TagText { get; private set; }

    public HighlightTint Tint { get; private set; } = HighlightTint.Amber;

    private void BuildTintPicker()
    {
        foreach (var (tint, key, name) in Tints)
        {
            var swatch = new RadioButton
            {
                GroupName = "Tint",
                IsChecked = tint == Tint,
                Style = (Style)FindResource("WlTintSwatch"),
                Background = (Brush)FindResource(key),
                ToolTip = name,
                Margin = new Thickness(0, 0, 8, 0)
            };

            AutomationProperties.SetName(swatch, name);
            swatch.Checked += (_, _) => Tint = tint;
            TintPanel.Children.Add(swatch);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Note = (NoteBox.Text ?? string.Empty).Trim();

        var tag = (TagBox.Text ?? string.Empty).Trim();
        TagText = tag.Length == 0 ? null : tag;

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
