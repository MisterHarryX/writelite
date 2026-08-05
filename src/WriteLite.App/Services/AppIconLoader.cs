using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace WriteLite.Services;

public static class AppIconLoader
{
    private const string RelativeIconPath = "assets\\WriteLite.ico";

    public static Icon CreateNotifyIcon()
    {
        var path = ResolveIconPath();
        return File.Exists(path) ? new Icon(path) : SystemIcons.Application;
    }

    public static void ApplyTo(Window window)
    {
        var path = ResolveIconPath();
        if (!File.Exists(path)) return;

        using var icon = new Icon(path, 32, 32);
        window.Icon = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(32, 32));
    }

    private static string ResolveIconPath()
    {
        return Path.Combine(AppContext.BaseDirectory, RelativeIconPath);
    }
}
