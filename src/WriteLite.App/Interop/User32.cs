using System.Runtime.InteropServices;

namespace WriteLite.Interop;

/// <summary>
/// The window-manager calls shared by the popup, overlay and shell windows.
///
/// Previously every consumer redeclared the same structs, constants and
/// <c>DllImport</c> bindings, in two incompatible flavours: <c>CorrectionPopupWindow</c>
/// bound GetWindowLong/SetWindowLong with a manual <c>IntPtr.Size</c> split (works on
/// 32- and 64-bit), while <c>InlineErrorOverlayWindow</c> bound <c>GetWindowLongPtrW</c>
/// directly, which only exists in the 64-bit user32.dll. The dual binding is the
/// correct one and lives here.
/// </summary>
internal static class User32
{
    public const int GwlExStyle = -20;

    public const long WsExTransparent = 0x00000020L;
    public const long WsExNoActivate = 0x08000000L;

    public const uint MonitorDefaultToNearest = 2;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpFrameChanged = 0x0020;
    public const uint SwpShowWindow = 0x0040;

    /// <summary>HWND_TOPMOST (-1).</summary>
    public static readonly IntPtr HwndTopmost = new(-1);

    /// <summary>
    /// Reads an extended window style. Binds the correct export: "GetWindowLongPtr"
    /// is a macro, not a real user32.dll symbol, so the 32-bit export is
    /// GetWindowLong and the 64-bit one is GetWindowLongPtr.
    /// </summary>
    public static long GetWindowLongPtr(IntPtr hwnd, int index)
        => IntPtr.Size == 8 ? GetWindowLongNative64(hwnd, index).ToInt64() : GetWindowLongNative32(hwnd, index);

    public static void SetWindowLongPtr(IntPtr hwnd, int index, long value)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongNative64(hwnd, index, new IntPtr(value));
        }
        else
        {
            SetWindowLongNative32(hwnd, index, unchecked((int)value));
        }
    }

    /// <summary>Finds the monitor nearest to a point (<c>MONITOR_DEFAULTTONEAREST</c>).</summary>
    public static IntPtr MonitorFromPoint(NativePoint point)
        => MonitorFromPointNative(point, MonitorDefaultToNearest);

    /// <summary>Finds the monitor nearest to a window (<c>MONITOR_DEFAULTTONEAREST</c>).</summary>
    public static IntPtr MonitorFromWindow(IntPtr hwnd)
        => MonitorFromWindowNative(hwnd, unchecked((int)MonitorDefaultToNearest));

    /// <summary>
    /// Retrieves MONITORINFO with cbSize pre-initialized, which the API insists on.
    /// Returns false when the handle is not a monitor.
    /// </summary>
    public static bool TryGetMonitorInfo(IntPtr monitor, out MonitorInfo info)
    {
        info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfoNative(monitor, ref info);
    }

    public static bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags)
        => SetWindowPosNative(hwnd, hwndInsertAfter, x, y, cx, cy, flags);

    public static bool ClientToScreen(IntPtr hwnd, ref NativePoint point)
        => ClientToScreenNative(hwnd, ref point);

    // ── DllImport bindings ─────────────────────────────────────────────

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLongNative32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongNative64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLongNative32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongNative64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    private static extern IntPtr MonitorFromPointNative(NativePoint point, uint flags);

    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    private static extern IntPtr MonitorFromWindowNative(IntPtr hwnd, int flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfo", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoNative(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPosNative(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "ClientToScreen", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreenNative(IntPtr hwnd, ref NativePoint point);
}