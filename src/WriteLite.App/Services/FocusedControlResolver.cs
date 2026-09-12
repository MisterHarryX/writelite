using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WriteLite.Services;

/// <summary>
/// Works out which control the keyboard is really typing into.
/// </summary>
/// <remarks>
/// <para><b>Why <c>AutomationElement.FocusedElement</c> is not enough.</b> It reports
/// whichever provider most recently claimed focus, and providers get that wrong. Measured on
/// a developer machine with Telegram, Discord and an Xbox input overlay running: keystrokes
/// went to Notepad and were visible in Notepad, while <c>FocusedElement</c> reported a
/// Telegram edit control for minutes at a time. WriteLite followed the claim, analysed the
/// wrong field, and decorated nothing the user could see.</para>
///
/// <para>The window manager cannot be wrong about this in the same way:
/// <c>GetForegroundWindow</c> is the window receiving input, and
/// <c>GetGUIThreadInfo(threadOf(foreground)).hwndFocus</c> is the control inside it with the
/// caret. Those are the same facts <c>SendInput</c> obeys, so they agree with where the
/// user's typing actually lands.</para>
///
/// <para>UIA is still asked first, because it resolves the *element* — a Chromium input has
/// no window handle of its own, and only the accessibility tree can name it. The window
/// manager is used as a cross-check: when the two disagree about which <em>process</em> has
/// the keyboard, the window manager wins and the element is resolved from its handle
/// instead.</para>
/// </remarks>
public static class FocusedControlResolver
{
    /// <summary>Which source produced the element, for diagnostics.</summary>
    public enum Source
    {
        /// <summary>UIA and the window manager agreed.</summary>
        Uia,

        /// <summary>UIA named a process that does not have the keyboard; the window won.</summary>
        ForegroundWindow,

        /// <summary>Neither could name a control.</summary>
        None,
    }

    /// <summary>The control the keyboard is typing into, and where the answer came from.</summary>
    public static (AutomationElement? Element, Source From) Resolve()
    {
        AutomationElement? claimed = null;
        try
        {
            claimed = AutomationElement.FocusedElement;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException
                                              or InvalidOperationException)
        {
            // Fall through to the window manager.
        }

        var focusedWindow = FocusedWindow();
        if (focusedWindow == IntPtr.Zero)
        {
            return (claimed, claimed is null ? Source.None : Source.Uia);
        }

        _ = GetWindowThreadProcessId(focusedWindow, out var keyboardProcessId);
        if (claimed is not null && ProcessIdOf(claimed) == keyboardProcessId)
        {
            return (claimed, Source.Uia);
        }

        AutomationElement? fromWindow;
        try
        {
            fromWindow = AutomationElement.FromHandle(focusedWindow);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException
                                              or InvalidOperationException
                                              or ArgumentException)
        {
            fromWindow = null;
        }

        if (fromWindow is null)
        {
            return (claimed, claimed is null ? Source.None : Source.Uia);
        }

        CompatibilityLogger.Technical(
            "focus-claim-overridden",
            $"claimed={(claimed is null ? 0 : ProcessIdOf(claimed))} keyboard={keyboardProcessId}");
        return (fromWindow, Source.ForegroundWindow);
    }

    private static int ProcessIdOf(AutomationElement element)
    {
        try { return element.Current.ProcessId; }
        catch { return 0; } // element may be gone between the caller's check and this property read
    }

    /// <summary>
    /// The window with the caret, according to the window manager.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately the <em>focused</em> window rather than the foreground one. For a
    /// packaged application the foreground window is an <c>ApplicationFrameWindow</c> owned
    /// by <c>ApplicationFrameHost</c>, while the control the user types into belongs to the
    /// application's own process. Comparing UIA's answer against the foreground process
    /// would call every one of those a mismatch and override a perfectly correct answer with
    /// a frame that contains no text.</para>
    ///
    /// <para><c>GetGUIThreadInfo(...).hwndFocus</c> is the control with the caret inside that
    /// frame, and it belongs to the same process UIA names — so packaged applications agree
    /// and no override happens, while a provider claiming focus in a different process is
    /// still caught.</para>
    /// </remarks>
    private static IntPtr FocusedWindow()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return IntPtr.Zero;

        var thread = GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (GetGUIThreadInfo(thread, ref info) && info.Focus != IntPtr.Zero)
        {
            return info.Focus;
        }

        return foreground;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public int CaretLeft;
        public int CaretTop;
        public int CaretRight;
        public int CaretBottom;
    }
}
