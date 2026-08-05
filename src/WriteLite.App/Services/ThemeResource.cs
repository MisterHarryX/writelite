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
}
