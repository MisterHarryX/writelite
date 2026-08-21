using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace FieldMatrix;

/// <summary>
/// Brings a target application up, puts the caret in the field under test, and types the
/// probe sentence — so that a matrix row is produced the same way every time it is run.
/// </summary>
/// <remarks>
/// Everything here is ordinary user input and ordinary accessibility navigation. Nothing
/// reaches into WriteLite, and nothing reaches into the application under test through a
/// private channel: if the probe can find and fill a field this way, so can a person.
/// </remarks>
internal static class Driver
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventUnicode = 0x0004;
    private const uint KeyEventKeyUp = 0x0002;

    /// <summary>Launches a process and waits for it to put a window up.</summary>
    public static Process? Launch(string path, string? arguments = null, int waitMs = 6000)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo(path, arguments ?? string.Empty)
            {
                UseShellExecute = true,
            });
            if (process is null) return null;

            var deadline = Environment.TickCount64 + waitMs;
            while (Environment.TickCount64 < deadline)
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero) break;
                Thread.Sleep(200);
            }

            Thread.Sleep(1200);
            return process;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"  launch failed: {exception.GetType().Name} {exception.Message}");
            return null;
        }
    }

    /// <summary>Finds a descendant by automation id, waiting for it to appear.</summary>
    public static AutomationElement? FindByAutomationId(int processId, string automationId, int waitMs = 8000)
        => FindBy(processId, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId), waitMs);

    /// <summary>Finds the first descendant of a control type, waiting for it to appear.</summary>
    public static AutomationElement? FindByControlType(int processId, ControlType controlType, int waitMs = 8000)
        => FindBy(processId, new PropertyCondition(AutomationElement.ControlTypeProperty, controlType), waitMs);

    /// <summary>
    /// Finds a top-level window by class name, for applications whose launcher process is not
    /// the process that ends up owning the window.
    /// </summary>
    public static AutomationElement? FindTopLevelByClassName(
        IReadOnlyList<string> classNames, string? nameContains = null, int waitMs = 10000)
    {
        var deadline = Environment.TickCount64 + waitMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                var windows = AutomationElement.RootElement.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

                foreach (AutomationElement window in windows)
                {
                    var className = window.Current.ClassName ?? string.Empty;
                    if (!classNames.Any(c => className.Contains(c, StringComparison.OrdinalIgnoreCase))) continue;
                    if (nameContains is not null
                        && !(window.Current.Name ?? string.Empty)
                            .Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return window;
                }
            }
            catch (Exception exception) when (exception is ElementNotAvailableException
                                                  or InvalidOperationException)
            {
                // The desktop tree changes while applications start; try again.
            }

            Thread.Sleep(400);
        }

        return null;
    }

    private static AutomationElement? FindBy(int processId, Condition condition, int waitMs)
    {
        var scoped = new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
            condition);

        var deadline = Environment.TickCount64 + waitMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                var found = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, scoped);
                if (found is not null) return found;
            }
            catch (Exception exception) when (exception is ElementNotAvailableException
                                                  or InvalidOperationException)
            {
                // The tree is still being built; try again.
            }

            Thread.Sleep(300);
        }

        return null;
    }

    /// <summary>
    /// The main window of a running process, found by process name.
    /// </summary>
    /// <remarks>
    /// Used for applications whose launcher is not the process that owns the window
    /// (Windows 11 Notepad) and for applications that are already open. Matching on the
    /// window title does not work across locales — the Russian Notepad is «Блокнот» — so the
    /// process is the identity.
    /// </remarks>
    public static AutomationElement? MainWindowOf(string processName, int waitMs = 10000)
    {
        var deadline = Environment.TickCount64 + waitMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero) continue;
                    return AutomationElement.FromHandle(process.MainWindowHandle);
                }
                catch (Exception exception) when (exception is ElementNotAvailableException
                                                      or InvalidOperationException
                                                      or ArgumentException)
                {
                    // Window is still coming up.
                }
            }

            Thread.Sleep(400);
        }

        return null;
    }

    /// <summary>The first editable-looking descendant of a window.</summary>
    public static AutomationElement? FirstTextField(AutomationElement window)
    {
        try
        {
            return window.FindFirst(
                       TreeScope.Descendants,
                       new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document))
                   ?? window.FindFirst(
                       TreeScope.Descendants,
                       new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        }
        catch (Exception exception) when (exception is ElementNotAvailableException
                                              or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Brings a window forward and gives the element the caret.</summary>
    public static bool Focus(AutomationElement element)
    {
        try
        {
            var hwnd = new IntPtr(element.Current.NativeWindowHandle);
            if (hwnd == IntPtr.Zero)
            {
                var top = TreeWalker.ControlViewWalker.GetParent(element);
                while (top is not null && top.Current.NativeWindowHandle == 0)
                {
                    top = TreeWalker.ControlViewWalker.GetParent(top);
                }

                hwnd = top is null ? IntPtr.Zero : new IntPtr(top.Current.NativeWindowHandle);
            }

            if (hwnd != IntPtr.Zero) ForceForeground(hwnd);
            Thread.Sleep(300);
            element.SetFocus();
            Thread.Sleep(400);

            // Synthesised keystrokes go to whatever actually has the keyboard, so the probe
            // has to know that its own console did not keep it.
            try
            {
                if (!AutomationElement.FocusedElement.Current.HasKeyboardFocus)
                {
                    Console.WriteLine("  warning: focus did not settle on the target");
                }
            }
            catch
            {
                // Focus state is advisory here; the probe reports what it measures either way.
            }

            return true;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"  focus failed: {exception.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Takes the foreground, working around the shell's foreground lock.
    /// </summary>
    /// <remarks>
    /// A console process is not allowed to raise another window over the one the user last
    /// interacted with. Attaching this thread's input queue to the current foreground
    /// window's for the duration of the call makes the two threads one input context, which
    /// is the sanctioned way for a test harness to hand focus over.
    /// </remarks>
    private static void ForceForeground(IntPtr hwnd)
    {
        ShowWindow(hwnd, SwRestore);
        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var thisThread = GetCurrentThreadId();

        if (foregroundThread != 0 && foregroundThread != thisThread)
        {
            AttachThreadInput(thisThread, foregroundThread, true);
            SetForegroundWindow(hwnd);
            AttachThreadInput(thisThread, foregroundThread, false);
        }
        else
        {
            SetForegroundWindow(hwnd);
        }
    }

    private const int SwRestore = 9;

    /// <summary>Clears the focused field and types <paramref name="text"/> as a person would.</summary>
    public static void TypeIntoFocused(string text, bool clearFirst = true)
    {
        if (clearFirst)
        {
            SendVirtualKey(0x11, 0x41); // Ctrl+A
            Thread.Sleep(80);
            SendVirtualKey(0, 0x2E);    // Delete
            Thread.Sleep(120);
        }

        foreach (var character in text)
        {
            SendUnicode(character);
            Thread.Sleep(12);
        }

        Thread.Sleep(400);
    }

    private static void SendUnicode(char character)
    {
        var inputs = new[]
        {
            UnicodeInput(character, down: true),
            UnicodeInput(character, down: false),
        };
        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static void SendVirtualKey(ushort modifier, ushort key)
    {
        var list = new List<Input>();
        if (modifier != 0) list.Add(KeyInput(modifier, down: true));
        list.Add(KeyInput(key, down: true));
        list.Add(KeyInput(key, down: false));
        if (modifier != 0) list.Add(KeyInput(modifier, down: false));
        var inputs = list.ToArray();
        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static Input UnicodeInput(char character, bool down) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = 0,
                ScanCode = character,
                Flags = KeyEventUnicode | (down ? 0 : KeyEventKeyUp),
            },
        },
    };

    private static Input KeyInput(ushort virtualKey, bool down) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = virtualKey,
                Flags = down ? 0 : KeyEventKeyUp,
            },
        },
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    /// <summary>
    /// The <c>INPUT</c> union.
    /// </summary>
    /// <remarks>
    /// <c>MouseInputData</c> is declared even though no mouse event is ever sent, because it
    /// is the largest member and therefore the one that sizes the union. Without it the
    /// struct measures 32 bytes on x64 instead of the 40 <c>SendInput</c> expects, the
    /// <c>cbSize</c> check fails, and the call returns zero having delivered nothing — a
    /// silent no-op rather than an error.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInputData Keyboard;
        [FieldOffset(0)] public MouseInputData Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
