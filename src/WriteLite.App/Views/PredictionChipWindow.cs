using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WriteLite.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace WriteLite.Views;

/// <summary>
/// The word-completion chip shown at the caret in another application: «…ение  Tab».
/// </summary>
/// <remarks>
/// A label, never a target: it cannot be clicked and never takes focus. Accepting is the Tab key,
/// watched only while the chip is on screen (see App.FieldPrediction).
/// </remarks>
public sealed class PredictionChipWindow : Window
{
    private readonly TextBlock _completion;

    public PredictionChipWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Title = "WriteLite";
        NoActivateWindow.Attach(this);

        _completion = new TextBlock
        {
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Resource("WlText", Colors.White),
        };
        var key = new Border
        {
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(5, 1, 5, 1),
            CornerRadius = new CornerRadius(4),
            Background = Resource("WlBrand", Color.FromRgb(0xF2, 0x6B, 0x0F)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "Tab",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = Resource("WlOnBrand", Colors.Black),
            },
        };
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(_completion);
        row.Children.Add(key);
        Content = new Border
        {
            Margin = new Thickness(4),
            Padding = new Thickness(9, 4, 6, 4),
            CornerRadius = new CornerRadius(7),
            Background = Resource("WlSurfaceHi", Color.FromRgb(0x1C, 0x1C, 0x1E)),
            BorderBrush = Resource("WlLineStrong", Color.FromRgb(0x3A, 0x3A, 0x3C)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = row,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.35 },
        };
    }

    /// <summary>Shows the completion just below-right of the caret (physical screen pixels).</summary>
    public void ShowAt(string completion, Rect physicalCaret)
    {
        _completion.Text = completion;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = physicalCaret.Right / dpi.DpiScaleX + 2;
        Top = physicalCaret.Bottom / dpi.DpiScaleY + 4;
        if (!IsVisible) Show();
    }

    private static Brush Resource(string key, Color fallback) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
