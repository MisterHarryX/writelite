using System.Windows;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaFontFamily = System.Windows.Media.FontFamily;
using WpfApplication = System.Windows.Application;
using WpfBrushes = System.Windows.Media.Brushes;

namespace WriteLite.Services;

public static class ThemeResource
{
    public static MediaBrush Brush(string key, MediaBrush? fallback = null) =>
        WpfApplication.Current?.TryFindResource(key) as MediaBrush ?? fallback ?? WpfBrushes.Transparent;

    public static MediaFontFamily Font(string key, string fallback = "Segoe UI") =>
        WpfApplication.Current?.TryFindResource(key) as MediaFontFamily ?? new MediaFontFamily(fallback);

    /// <summary>
    /// Resolves a control style so windows built in code can use the same templates as
    /// the XAML views. Returns null when the theme is not loaded (design time, tests),
    /// which leaves the control on its default style rather than failing.
    /// </summary>
    public static Style? Style(string key) =>
        WpfApplication.Current?.TryFindResource(key) as Style;
}
