using System.Windows;
using System.Windows.Interop;
using WriteLite.Interop;

namespace WriteLite.Services;

/// <summary>
/// Marks a WriteLite overlay as a window that never takes the keyboard from the field under it.
/// </summary>
/// <remarks>
/// <c>ShowActivated = false</c> only covers the moment a window appears; the first click on it
/// still activates it, and the field the user was typing in loses its caret. Every overlay that
/// sits on top of another application's text — the indicator, the panel, the action card — has to
/// carry <c>WS_EX_NOACTIVATE</c>, reapplied on every show because WPF rewrites the extended style
/// of a transparent window when it becomes visible.
/// </remarks>
public static class NoActivateWindow
{
    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window);
        window.IsVisibleChanged += (_, _) =>
        {
            if (window.IsVisible) Apply(window);
        };
    }

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var before = User32.GetWindowLongPtr(handle, User32.GwlExStyle);
        var after = before | User32.WsExNoActivate;
        if (after != before) User32.SetWindowLongPtr(handle, User32.GwlExStyle, after);
    }
}
