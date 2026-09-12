using System.Windows;
using WriteLite.Interop;

namespace WriteLite.Services;

/// <summary>
/// Resolves the work area of the monitor under a physical-screen anchor, scaled
/// back into the caller's DPI space. Shared by the correction and lexical popups,
/// which previously each carried their own copy of the Win32 round-trip.
/// </summary>
internal static class PopupWorkArea
{
    public static Rect GetCurrent(Rect physicalAnchor, double dpiX, double dpiY)
    {
        var point = new NativePoint
        {
            X = (int)Math.Round(physicalAnchor.Left),
            Y = (int)Math.Round(physicalAnchor.Top)
        };
        var monitor = User32.MonitorFromPoint(point);
        if (monitor != IntPtr.Zero && User32.TryGetMonitorInfo(monitor, out var info))
        {
            var work = new Rect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top);
            return SmartPopupPlacementService.Scale(work, dpiX, dpiY);
        }

        return SystemParameters.WorkArea;
    }
}