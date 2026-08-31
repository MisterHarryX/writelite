using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CardProbe;

/// <summary>
/// Refuses to synthesise input unless it is provably going to a window this probe owns.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> The first version of this probe assumed that launching an
/// application and calling <c>SetForegroundWindow</c> put its field under the cursor. Windows
/// refuses foreground activation requested by a background process, so the launch succeeded,
/// the target stayed behind whatever was already on top, and the click at the target's screen
/// coordinates landed on the window covering it. The probe then typed a Russian test sentence
/// into a browser message composer that happened to be there.</para>
///
/// <para>Synthesised input goes to whoever has focus, and a probe cannot know who that is by
/// assuming. So it asks, every time, immediately before acting: the foreground window and the
/// window under the click point must both belong to the process this probe started. Anything
/// else aborts the target rather than guessing — a skipped row costs a measurement, and a
/// wrong guess costs somebody their draft.</para>
/// </remarks>
internal static class Guard
{
    /// <summary>
    /// Raises <paramref name="host"/> above everything so a click can reach it.
    /// </summary>
    /// <remarks>
    /// <para>Windows will not hand the foreground to a process that did not receive the last
    /// input event, and on this machine several applications hold it persistently — the probe
    /// has been refused by WriteLite's own window, by an editor and by a chat client on
    /// consecutive runs. Arguing with the foreground lock is the wrong fight: the probe does
    /// not need focus, it needs the target to be the window under the cursor, and a real
    /// click will then grant focus the ordinary way.</para>
    ///
    /// <para>Topmost is dropped again as soon as the click lands, because WriteLite's own
    /// underline overlay and correction card are topmost windows too and the probe has to be
    /// able to click those next.</para>
    /// </remarks>
    public static bool Raise(Process host, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            host.Refresh();
            var handle = host.MainWindowHandle;
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, SwRestore);
                SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
                Thread.Sleep(200);
                if (GetWindowRect(handle, out var rect)
                    && OwnsPoint(host.Id, (rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2))
                {
                    return true;
                }
            }

            Thread.Sleep(250);
        }

        return false;
    }

    /// <summary>Puts the window back into the normal z-order.</summary>
    public static void Lower(Process host)
    {
        host.Refresh();
        var handle = host.MainWindowHandle;
        if (handle != IntPtr.Zero)
        {
            SetWindowPos(handle, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
        }
    }

    /// <summary>Brings <paramref name="host"/> forward and confirms it actually got there.</summary>
    public static bool TakeForeground(Process host, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            host.Refresh();
            var handle = host.MainWindowHandle;
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, SwRestore);

                // Foreground activation from a background process is refused unless the
                // caller shares an input queue with the current foreground thread.
                var foreground = GetForegroundWindow();
                var foregroundThread = GetWindowThreadProcessId(foreground, out _);
                var targetThread = GetWindowThreadProcessId(handle, out _);
                var attached = foregroundThread != targetThread
                               && AttachThreadInput(foregroundThread, targetThread, true);
                SetForegroundWindow(handle);
                if (attached) AttachThreadInput(foregroundThread, targetThread, false);

                Thread.Sleep(250);
                if (OwnsForeground(host)) return true;
            }

            Thread.Sleep(250);
        }

        return false;
    }

    public static bool OwnsForeground(Process host)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid == host.Id;
    }

    /// <summary>Whether the window under this screen point belongs to the probe's own target.</summary>
    public static bool OwnsPoint(Process host, int x, int y) => OwnsPoint(host.Id, x, y);

    public static bool OwnsPoint(int processId, int x, int y)
    {
        var window = WindowFromPoint(new NativePoint { X = x, Y = y });
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out var pid);
        return pid == processId;
    }

    /// <summary>
    /// The name of whatever currently has focus, for a report that says where input would go.
    /// </summary>
    public static string DescribeForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        try
        {
            var process = Process.GetProcessById((int)pid);
            return $"{process.ProcessName} (pid {pid})";
        }
        catch (ArgumentException)
        {
            return $"pid {pid}";
        }
    }

    private const int SwRestore = 9;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
