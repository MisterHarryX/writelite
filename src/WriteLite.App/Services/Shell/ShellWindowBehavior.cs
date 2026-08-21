using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WriteLite.Services.Shell;

/// <summary>
/// Makes a chromeless window size itself correctly when maximised, and gives it a
/// real fullscreen mode.
/// </summary>
/// <remarks>
/// Both jobs are the same job. A window with <c>WindowStyle=None</c> and a
/// <see cref="System.Windows.Shell.WindowChrome"/> still carries
/// <c>WS_THICKFRAME</c>, so Windows maximises it to the monitor inflated by the
/// resize frame rather than to the work area — and because the chrome has already
/// answered <c>WM_NCCALCSIZE</c> with "no non-client area", every one of those extra
/// pixels is live UI sitting under the taskbar. Answering <c>WM_GETMINMAXINFO</c>
/// decides how large "maximised" is, so the one handler both fixes the clipping and
/// implements fullscreen: work area normally, whole monitor when
/// <see cref="IsFullScreen"/> is set.
///
/// Nothing here uses a fixed offset or a negative margin. Every number comes from
/// <c>GetMonitorInfo</c> for the monitor the window is actually on, in physical
/// pixels, which is what makes it correct on a second screen and at every DPI — a DIP
/// constant would have been wrong at 125 %, 150 % and 175 % in three different ways.
///
/// A note on what the process actually declares, because this file used to claim
/// otherwise: WriteLite ships no application manifest, so it runs
/// <c>SYSTEM_AWARE</c> — measured, not assumed. Everything here is correct under that,
/// and correct under per-monitor awareness too, because it asks the monitor rather
/// than assuming a scale. What system awareness means in practice is that moving the
/// window to a screen with different scaling gets a bitmap stretch from Windows rather
/// than a re-layout, and that a scale change while running is not picked up until the
/// next start. The <c>WM_DPICHANGED</c> case below is therefore dormant on a
/// single-scale desktop; it is kept because it is the correct answer if the manifest
/// is ever added, and because it costs nothing when the message never arrives.
/// See FUTURE_WORK.md.
/// </remarks>
public sealed class ShellWindowBehavior : IDisposable
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmDpiChanged = 0x02E0;
    private const int MonitorDefaultToNearest = 0x00000002;

    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpFrameChanged = 0x0020;

    private const uint AbmGetState = 0x00000004;
    private const uint AbmGetAutoHideBarEx = 0x0000000B;
    private const int AbsAutoHide = 0x00000001;

    private readonly Window _window;
    private HwndSource? _source;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    private Rect _boundsBeforeFullScreen = Rect.Empty;
    private bool _disposed;

    public ShellWindowBehavior(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));

        if (new WindowInteropHelper(_window).Handle != IntPtr.Zero)
        {
            Attach();
        }
        else
        {
            _window.SourceInitialized += OnSourceInitialized;
        }
    }

    /// <summary>True while the window owns the whole display.</summary>
    public bool IsFullScreen { get; private set; }

    /// <summary>Raised after fullscreen is entered or left, so the shell can hide or restore its title bar.</summary>
    public event Action<bool>? FullScreenChanged;

    /// <summary>
    /// Enters or leaves fullscreen, restoring the exact previous geometry on the way out.
    /// </summary>
    public void ToggleFullScreen()
    {
        if (IsFullScreen)
        {
            ExitFullScreen();
        }
        else
        {
            EnterFullScreen();
        }
    }

    public void EnterFullScreen()
    {
        if (IsFullScreen || _disposed)
        {
            return;
        }

        // Remembered before anything moves. RestoreBounds is empty for a window that
        // has never been maximised, in which case the live rectangle is the truth.
        _stateBeforeFullScreen = _window.WindowState;
        _boundsBeforeFullScreen = _window.WindowState == WindowState.Normal
            ? new Rect(_window.Left, _window.Top, _window.Width, _window.Height)
            : _window.RestoreBounds;

        IsFullScreen = true;

        // Maximised already? The size is only recomputed when the window *becomes*
        // maximised, so it has to leave that state for the new answer to be asked for.
        if (_window.WindowState == WindowState.Maximized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.WindowState = WindowState.Maximized;
        FullScreenChanged?.Invoke(true);
    }

    public void ExitFullScreen()
    {
        if (!IsFullScreen || _disposed)
        {
            return;
        }

        IsFullScreen = false;
        _window.WindowState = WindowState.Normal;

        if (_boundsBeforeFullScreen is { Width: > 0, Height: > 0 })
        {
            _window.Left = _boundsBeforeFullScreen.Left;
            _window.Top = _boundsBeforeFullScreen.Top;
            _window.Width = _boundsBeforeFullScreen.Width;
            _window.Height = _boundsBeforeFullScreen.Height;
        }

        if (_stateBeforeFullScreen == WindowState.Maximized)
        {
            _window.WindowState = WindowState.Maximized;
        }

        FullScreenChanged?.Invoke(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _source?.RemoveHook(OnWindowMessage);
        _source = null;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _window.SourceInitialized -= OnSourceInitialized;
        Attach();
    }

    private void Attach()
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmGetMinMaxInfo:
                ApplyMaximizedBounds(hwnd, lParam);
                handled = true;
                break;

            case WmDpiChanged when _window.WindowState == WindowState.Maximized:
                // Dragging a maximised window to a screen with different scaling gives
                // it a suggested rectangle computed from the old monitor. Re-asking
                // for the frame makes Windows send a fresh WM_GETMINMAXINFO, which is
                // the only value that is right for the monitor it has landed on.
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNoZOrder | SwpNoActivate | SwpFrameChanged | 0x0001 /* SWP_NOSIZE */ | 0x0002 /* SWP_NOMOVE */);
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>Answers <c>WM_GETMINMAXINFO</c> with the work area, or the whole monitor in fullscreen.</summary>
    private void ApplyMaximizedBounds(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var monitorRect = info.rcMonitor.ToPixelRect();
        var workRect = info.rcWork.ToPixelRect();

        var placement = IsFullScreen
            ? WindowPlacementMath.ForFullScreen(monitorRect)
            : WindowPlacementMath.ForWorkArea(monitorRect, workRect, FindAutoHideEdge(info.rcMonitor));

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMax.ptMaxPosition = new Point(placement.X, placement.Y);
        minMax.ptMaxSize = new Point(placement.Width, placement.Height);

        // The tracking maximum has to be raised as well: it defaults to the work area
        // of the *primary* monitor, which would cap a fullscreen window on a taller
        // secondary screen at the primary's height.
        minMax.ptMaxTrackSize = new Point(
            Math.Max(minMax.ptMaxTrackSize.x, placement.Width),
            Math.Max(minMax.ptMaxTrackSize.y, placement.Height));

        Marshal.StructureToPtr(minMax, lParam, fDeleteOld: true);
    }

    /// <summary>Which edge of this monitor holds an auto-hiding taskbar, if any.</summary>
    private static ScreenEdge FindAutoHideEdge(Rect32 monitor)
    {
        try
        {
            var state = new AppBarData { cbSize = (uint)Marshal.SizeOf<AppBarData>() };
            if ((SHAppBarMessage(AbmGetState, ref state).ToInt64() & AbsAutoHide) == 0)
            {
                return ScreenEdge.None;
            }

            for (uint edge = 0; edge <= 3; edge++)
            {
                var data = new AppBarData
                {
                    cbSize = (uint)Marshal.SizeOf<AppBarData>(),
                    uEdge = edge,
                    rc = monitor
                };

                if (SHAppBarMessage(AbmGetAutoHideBarEx, ref data) != IntPtr.Zero)
                {
                    // ABE_LEFT 0, ABE_TOP 1, ABE_RIGHT 2, ABE_BOTTOM 3.
                    return edge switch
                    {
                        0 => ScreenEdge.Left,
                        1 => ScreenEdge.Top,
                        2 => ScreenEdge.Right,
                        _ => ScreenEdge.Bottom
                    };
                }
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // Shell not available (a session without Explorer). No taskbar to dodge.
        }

        return ScreenEdge.None;
    }

    // ── Interop ──────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int x;
        public int y;

        public Point(int x, int y)
        {
            this.x = x;
            this.y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point ptReserved;
        public Point ptMaxSize;
        public Point ptMaxPosition;
        public Point ptMinTrackSize;
        public Point ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int left;
        public int top;
        public int right;
        public int bottom;

        public PixelRect ToPixelRect() => new(left, top, right, bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect32 rcMonitor;
        public Rect32 rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Rect32 rc;
        public IntPtr lParam;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, int flags);

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr SHAppBarMessage(uint message, ref AppBarData data);
}
