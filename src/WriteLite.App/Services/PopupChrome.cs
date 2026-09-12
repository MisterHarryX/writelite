using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MediaBrush = System.Windows.Media.Brush;

namespace WriteLite.Services;

/// <summary>
/// Shared brushed chrome for the code-built popup windows: the correction card and
/// the lexical card both resolve the same brand tokens and draw the same rounded
/// ghost buttons. Previously each window redeclared this block and its template,
/// and the two templates drifted only in name.
/// </summary>
internal static class PopupChrome
{
    public static readonly MediaBrush CardBackground = ThemeResource.Brush("WlSurface", Brushes.Black);
    public static readonly MediaBrush CardBorderBrush = ThemeResource.Brush("WlLineStrong", Brushes.DimGray);
    public static readonly MediaBrush MutedBrush = ThemeResource.Brush("WlTextSecondary", Brushes.LightGray);
    public static readonly MediaBrush FaintBrush = ThemeResource.Brush("WlTextMuted", Brushes.Gray);
    public static readonly MediaBrush AccentBrush = ThemeResource.Brush("WlBrand", Brushes.Orange);
    public static readonly MediaBrush OnAccentBrush = ThemeResource.Brush("WlOnBrand", Brushes.Black);

    public static Button CreateGhostButton(string text, double width, double height, double fontSize = 14)
    {
        var button = new Button
        {
            Content = text,
            Width = width,
            Height = height,
            FontSize = fontSize,
            Foreground = MutedBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2, 8, 2),
            Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Template = RoundedButtonTemplate(new CornerRadius(5), ThemeResource.Brush("WlHover", Brushes.DimGray));
        return button;
    }

    /// <summary>
    /// Minimal rounded button template. The popups are built without XAML, so the
    /// shape and hover surface follow the same tokens as the styles in Themes/Buttons.xaml.
    /// </summary>
    public static ControlTemplate RoundedButtonTemplate(CornerRadius radius, MediaBrush hoverBrush)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "border";
        border.SetValue(Border.CornerRadiusProperty, radius);
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, hoverBrush, "border"));
        template.Triggers.Add(hover);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "border"));
        template.Triggers.Add(disabled);

        return template;
    }
}