using System.Runtime.InteropServices;

namespace WriteLite.Interop;

/// <summary>The Win32 POINT structure, in physical device pixels.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

/// <summary>The Win32 RECT structure, in physical device pixels.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

/// <summary>The Win32 MONITORINFO structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MonitorInfo
{
    public int cbSize;
    public NativeRect rcMonitor;
    public NativeRect rcWork;
    public int dwFlags;
}