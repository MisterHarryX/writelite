using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace WriteLite.Services;

/// <summary>
/// Win32 Edit/RichEdit helpers: EM_REPLACESEL preserves undo better than ValuePattern.SetValue
/// on classic Edit / RichEdit20W. On Windows 11 Notepad (RichEditD2DPT) undo may still fail —
/// callers should not claim universal Ctrl+Z support.
/// </summary>
internal static class Win32TextEdit
{
    private const uint MessageTimeoutMs = 300;
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int EmGetSel = 0x00B0;
    private const int EmSetSel = 0x00B1;
    private const int EmReplaceSel = 0x00C2;
    private const int EmGetFirstVisibleLine = 0x00CE;
    private const int EmLineScroll = 0x00B6;
    private const int EmCanUndo = 0x00C6;
    private const int WmGetTextLength = 0x000E;
    private const int WmGetText = 0x000D;

    public static bool TryGetHwnd(AutomationElement element, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        try
        {
            var handle = element.Current.NativeWindowHandle;
            if (handle == 0)
            {
                return false;
            }

            hwnd = new IntPtr(handle);
            if (!IsWindow(hwnd))
            {
                hwnd = IntPtr.Zero;
                return false;
            }

            // Only target known edit classes. Zero/empty class → reject.
            var cls = GetClassName(hwnd);
            if (!IsSupportedEditClass(cls))
            {
                hwnd = IntPtr.Zero;
                return false;
            }

            return true;
        }
        catch
        {
            hwnd = IntPtr.Zero;
            return false;
        }
    }

    public static bool IsSupportedEditClass(string className)
    {
        if (string.IsNullOrEmpty(className))
        {
            return false;
        }

        // Classic Edit, RichEdit20W/A, RICHEDIT50W, RichEditD2DPT (Win11 Notepad), etc.
        return className.Contains("Edit", StringComparison.OrdinalIgnoreCase)
               || className.Contains("RichEdit", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when Win32 replace is available but Ctrl+Z is known-unreliable (e.g. RichEditD2DPT).
    /// </summary>
    public static bool IsUndoUnreliable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return true;
        }

        var cls = GetClassName(hwnd);
        // Windows 11 Notepad host — EM_REPLACESEL often does not create a standard undo unit.
        return cls.Contains("RichEditD2D", StringComparison.OrdinalIgnoreCase)
               || cls.Contains("D2D", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryReplaceAll(IntPtr hwnd, string newText, int caretIndex)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var firstLine = GetFirstVisibleLine(hwnd);
            NativeSetFocus(hwnd);

            if (!TrySend(hwnd, EmSetSel, IntPtr.Zero, new IntPtr(-1), out _)
                || !TrySend(hwnd, EmReplaceSel, new IntPtr(1), newText ?? string.Empty, out _))
            {
                return false;
            }

            if (!TrySend(hwnd, WmGetTextLength, IntPtr.Zero, IntPtr.Zero, out var lengthResult))
            {
                return false;
            }

            var len = (int)lengthResult;
            var caret = Math.Clamp(caretIndex < 0 ? len : caretIndex, 0, Math.Max(0, len));
            if (!TrySend(hwnd, EmSetSel, new IntPtr(caret), new IntPtr(caret), out _))
            {
                return false;
            }

            RestoreFirstVisibleLine(hwnd, firstLine);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReplaceRange(IntPtr hwnd, int start, int length, string replacement)
    {
        if (hwnd == IntPtr.Zero || start < 0 || length < 0)
        {
            return false;
        }

        try
        {
            if (!TrySend(hwnd, WmGetTextLength, IntPtr.Zero, IntPtr.Zero, out var lengthResult))
            {
                return false;
            }

            var textLen = (int)lengthResult;
            if (start > textLen || start + length > textLen)
            {
                return false;
            }

            var firstLine = GetFirstVisibleLine(hwnd);
            NativeSetFocus(hwnd);

            var end = start + length;
            if (!TrySend(hwnd, EmSetSel, new IntPtr(start), new IntPtr(end), out _)
                || !TrySend(hwnd, EmReplaceSel, new IntPtr(1), replacement ?? string.Empty, out _))
            {
                return false;
            }

            var caret = start + (replacement?.Length ?? 0);
            if (!TrySend(hwnd, EmSetSel, new IntPtr(caret), new IntPtr(caret), out _))
            {
                return false;
            }

            RestoreFirstVisibleLine(hwnd, firstLine);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static int GetFirstVisibleLine(IntPtr hwnd)
    {
        try
        {
            return TrySend(hwnd, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero, out var result)
                ? (int)result
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static void RestoreFirstVisibleLine(IntPtr hwnd, int desiredFirstLine)
    {
        try
        {
            var current = GetFirstVisibleLine(hwnd);
            var delta = desiredFirstLine - current;
            if (delta != 0)
            {
                _ = TrySend(hwnd, EmLineScroll, IntPtr.Zero, new IntPtr(delta), out _);
            }
        }
        catch
        {
            // best effort
        }
    }

    public static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        _ = GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    private static void NativeSetFocus(IntPtr hwnd)
    {
        try
        {
            _ = SetFocus(hwnd);
        }
        catch
        {
            // ignore
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private static bool TrySend(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        out IntPtr result)
        => SendMessageTimeout(
               hwnd,
               message,
               wParam,
               lParam,
               SmtoBlock | SmtoAbortIfHung,
               MessageTimeoutMs,
               out result) != IntPtr.Zero;

    private static bool TrySend(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        string lParam,
        out IntPtr result)
        => SendMessageTimeout(
               hwnd,
               message,
               wParam,
               lParam,
               SmtoBlock | SmtoAbortIfHung,
               MessageTimeoutMs,
               out result) != IntPtr.Zero;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out IntPtr result);
}
