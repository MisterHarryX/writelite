using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WriteLite.UiaStress;

/// <summary>
/// The running WriteLite window, seen from outside the process.
/// </summary>
/// <remarks>
/// Deliberately out-of-process. A test that drives controls on the same dispatcher can
/// never observe a hang — the hang would be its own thread. Everything here talks to
/// the application through UI Automation and Win32, so "did the window stop answering"
/// is a question that can actually be asked, and asked while the application is busy.
/// </remarks>
internal sealed class Ui(Process process) : IDisposable
{
    private const int SmtoAbortIfHung = 0x0002;
    private const int WmNull = 0x0000;

    private readonly Process _process = process;
    private IntPtr _shell;

    public Process Process => _process;

    /// <summary>The longest single stretch the window failed to answer a message.</summary>
    public TimeSpan WorstStall { get; private set; }

    /// <summary>
    /// The shell window's handle, found through the rail rather than through the process.
    /// </summary>
    /// <remarks>
    /// <see cref="Process.MainWindowHandle"/> is whichever top-level window the process
    /// happened to own when it was last refreshed, and a WPF tooltip is a top-level
    /// window: hovering the reader's type buttons for a few minutes is enough for it to
    /// latch onto a 160×28 tooltip. Everything downstream then measures and resizes the
    /// wrong thing — the window "shrinks to nothing", "fails to go fullscreen" and "has
    /// no visible canvas", none of which the application did.
    ///
    /// The rail is only ever in the shell, so the window that contains it is the shell.
    /// </remarks>
    public IntPtr ShellHandle
    {
        get
        {
            if (_shell != IntPtr.Zero && IsWindow(_shell))
            {
                return _shell;
            }

            foreach (var window in Windows())
            {
                try
                {
                    if (window.FindFirst(
                            TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.NameProperty, "Главная")) is null)
                    {
                        continue;
                    }

                    _shell = new IntPtr(window.Current.NativeWindowHandle);
                    return _shell;
                }
                catch (ElementNotAvailableException)
                {
                    // Closed while being inspected.
                }
            }

            _process.Refresh();
            return _process.MainWindowHandle;
        }
    }

    public AutomationElement Window
    {
        get
        {
            var element = FindWindow();
            return element ?? throw new StressException("The WriteLite window is gone.");
        }
    }

    // ── Finding things ───────────────────────────────────────────────────────

    private AutomationElement? FindWindow()
    {
        try
        {
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, _process.Id),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, condition);

            // The shell is the one with the navigation rail in it; dialogs are windows too.
            for (var index = 0; index < windows.Count; index++)
            {
                var candidate = windows[index];
                if (candidate.Current.ClassName.Contains("Window", StringComparison.OrdinalIgnoreCase)
                    || candidate.Current.Name.Contains("WriteLite", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return windows.Count > 0 ? windows[0] : null;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>Every window this process currently owns, shell and dialogs alike.</summary>
    public IReadOnlyList<AutomationElement> Windows()
    {
        var result = new List<AutomationElement>();

        try
        {
            var found = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, _process.Id));

            for (var index = 0; index < found.Count; index++)
            {
                result.Add(found[index]);
            }
        }
        catch (ElementNotAvailableException)
        {
            // The process went away mid-enumeration; the caller's liveness check will say so.
        }

        return result;
    }

    /// <summary>
    /// Finds a named element anywhere this process is showing it.
    /// </summary>
    /// <remarks>
    /// Searches every window the process owns, not only the shell. Half of what the
    /// scenarios press — the note editor, the card editor, the annotation editor — lives
    /// in a separate top-level window, and scoping the search to the main window makes
    /// those look absent rather than unreachable.
    /// </remarks>
    public AutomationElement? ByName(string name, AutomationElement? scope = null, int timeoutMs = 4000)
    {
        var deadline = Stopwatch.StartNew();
        var condition = new PropertyCondition(AutomationElement.NameProperty, name);

        do
        {
            var roots = scope is not null ? [scope] : Windows();

            foreach (var root in roots)
            {
                try
                {
                    if (root.FindFirst(TreeScope.Descendants, condition) is { } match)
                    {
                        return match;
                    }
                }
                catch (ElementNotAvailableException)
                {
                    // Torn down between the find and the read. Try the next one.
                }
            }

            Thread.Sleep(40);
        }
        while (deadline.ElapsedMilliseconds < timeoutMs);

        return null;
    }

    public AutomationElement Require(string name, AutomationElement? scope = null, int timeoutMs = 6000) =>
        ByName(name, scope, timeoutMs)
        ?? throw new StressException($"«{name}» was not on screen within {timeoutMs} ms.");

    // ── Doing things ─────────────────────────────────────────────────────────

    public static void Invoke(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return;
        }

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
        {
            ((SelectionItemPattern)select).Select();
            return;
        }

        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
        {
            ((TogglePattern)toggle).Toggle();
            return;
        }

        throw new StressException(
            $"«{element.Current.Name}» ({element.Current.ControlType.ProgrammaticName}) cannot be activated.");
    }

    public void Click(string name, AutomationElement? scope = null) => Invoke(Require(name, scope));

    // ── Liveness ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Waits for the window to answer a message, and records how long that took.
    /// </summary>
    /// <remarks>
    /// <c>SendMessageTimeout</c> with a null message is the standard question "is your
    /// message loop running": it does no work in the target and returns only once the
    /// loop has picked it up. <see cref="Process.Responding"/> alone is a coarser signal
    /// that Windows only flips after five seconds, which is far past the point where a
    /// person has decided the application is broken.
    /// </remarks>
    public TimeSpan Ping(int timeoutMs = 15_000)
    {
        _process.Refresh();
        if (_process.HasExited)
        {
            throw new StressException($"WriteLite exited with code {_process.ExitCode}.");
        }

        var handle = ShellHandle;
        if (handle == IntPtr.Zero)
        {
            return TimeSpan.Zero;
        }

        var clock = Stopwatch.StartNew();
        var answered = SendMessageTimeout(handle, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, (uint)timeoutMs, out _);
        clock.Stop();

        if (answered == IntPtr.Zero)
        {
            throw new StressException(
                $"The window stopped answering for at least {timeoutMs} ms — this is a hang.");
        }

        if (clock.Elapsed > WorstStall)
        {
            WorstStall = clock.Elapsed;
        }

        return clock.Elapsed;
    }

    public bool IsAlive
    {
        get
        {
            _process.Refresh();
            return !_process.HasExited;
        }
    }

    public long WorkingSetMb
    {
        get
        {
            _process.Refresh();
            return _process.WorkingSet64 / 1024 / 1024;
        }
    }

    // ── Window geometry ──────────────────────────────────────────────────────

    public void Resize(int width, int height)
    {
        var handle = ShellHandle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(handle, IntPtr.Zero, 0, 0, width, height, 0x0004 | 0x0010 | 0x0002);
    }

    /// <summary>
    /// The part of an element that is actually on screen inside the window.
    /// </summary>
    /// <remarks>
    /// A scrolled document reports the bounds of the whole document, not of the part
    /// being shown: the reading canvas of a three-thousand-paragraph book comes back
    /// eight hundred thousand pixels tall, starting far above the top of the screen.
    /// Clicking at "thirty pixels below its top" therefore aims at nothing at all —
    /// which looks exactly like the application ignoring the gesture. Intersecting with
    /// the window is what turns an element's bounds into somewhere a person could click.
    /// </remarks>
    public System.Windows.Rect VisibleBounds(AutomationElement element)
    {
        var handle = ShellHandle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var frame))
        {
            return element.Current.BoundingRectangle;
        }

        var window = new System.Windows.Rect(
            frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);

        var bounds = element.Current.BoundingRectangle;
        bounds.Intersect(window);

        return bounds;
    }

    public (int Width, int Height) Bounds()
    {
        var handle = ShellHandle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
        {
            return (0, 0);
        }

        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public void Focus()
    {
        var handle = ShellHandle;
        if (handle != IntPtr.Zero)
        {
            SetForegroundWindow(handle);
        }
    }

    public void SendKey(ushort virtualKey)
    {
        Focus();
        Thread.Sleep(30);

        keybd_event((byte)virtualKey, 0, 0, UIntPtr.Zero);
        Thread.Sleep(20);
        keybd_event((byte)virtualKey, 0, 2, UIntPtr.Zero);
    }

    public void Dispose()
    {
        if (_process.HasExited)
        {
            return;
        }

        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5_000);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);
}

internal sealed class StressException(string message) : Exception(message);
