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
    private const int EmExGetSel = 0x0400 + 52;

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
            // The window may have been destroyed between calls; there is nothing to write to.
            return false;
        }
    }

    /// <summary>
    /// Replaces a range only after proving the window agrees about where that range is.
    /// </summary>
    /// <remarks>
    /// <para>Three things have to line up before a character is written: the window has to
    /// hand back its text, the offsets computed against the text provider have to be
    /// translatable into the window's own selection coordinates, and the text sitting at
    /// those coordinates has to be the word the correction says it is replacing. Any one of
    /// them failing returns a reason rather than a write, and the caller moves on to the next
    /// strategy.</para>
    ///
    /// <para>The middle step is the one that used to be missing. <c>EM_SETSEL</c> was handed
    /// the analyzer's offsets directly, which is right for a classic multiline <c>EDIT</c>
    /// and wrong for RichEdit, where a line break costs one selection character and two text
    /// characters. Instead of guessing from the window class, the convention is measured:
    /// selecting everything and reading the end offset back says exactly how this window
    /// counts, whatever it is called. The probe only runs when the text contains a line
    /// break, so a single-line field pays nothing for it.</para>
    /// </remarks>
    public static Win32RangeWriteOutcome TryReplaceVerifiedRange(
        IntPtr hwnd,
        int start,
        int length,
        string expectedOriginal,
        string replacement)
    {
        if (hwnd == IntPtr.Zero || start < 0 || length < 0)
        {
            return Win32RangeWriteOutcome.BadArguments;
        }

        try
        {
            if (!TryGetWindowText(hwnd, out var windowText))
            {
                return Win32RangeWriteOutcome.TextUnavailable;
            }

            if (start + length > windowText.Length)
            {
                return Win32RangeWriteOutcome.RangeOutsideText;
            }

            if (!string.Equals(
                    windowText.Substring(start, length),
                    expectedOriginal ?? string.Empty,
                    StringComparison.Ordinal))
            {
                return Win32RangeWriteOutcome.OriginalMismatch;
            }

            var convention = DetectSelectionConvention(hwnd, windowText);
            if (!Win32SelectionOffsets.TryMap(windowText, start, convention, out var selectionStart)
                || !Win32SelectionOffsets.TryMap(windowText, start + length, convention, out var selectionEnd))
            {
                return Win32RangeWriteOutcome.OffsetsNotMappable;
            }

            var firstLine = GetFirstVisibleLine(hwnd);
            NativeSetFocus(hwnd);

            if (!TrySend(hwnd, EmSetSel, new IntPtr(selectionStart), new IntPtr(selectionEnd), out _)
                || !TrySend(hwnd, EmReplaceSel, new IntPtr(1), replacement ?? string.Empty, out _))
            {
                return Win32RangeWriteOutcome.MessageRejected;
            }

            var caret = selectionStart + (replacement?.Length ?? 0);
            _ = TrySend(hwnd, EmSetSel, new IntPtr(caret), new IntPtr(caret), out _);
            RestoreFirstVisibleLine(hwnd, firstLine);
            return Win32RangeWriteOutcome.Written;
        }
        catch
        {
            // A vanished or broken window means the replace could not be verified; report rejection.
            return Win32RangeWriteOutcome.MessageRejected;
        }
    }

    /// <summary>Reads the window's text through <c>WM_GETTEXT</c>.</summary>
    public static bool TryGetWindowText(IntPtr hwnd, out string text)
    {
        text = string.Empty;
        if (!TrySend(hwnd, WmGetTextLength, IntPtr.Zero, IntPtr.Zero, out var lengthResult))
        {
            return false;
        }

        var length = (int)lengthResult;
        if (length < 0 || length > 4_000_000) return false;
        if (length == 0) return true;

        var buffer = new StringBuilder(length + 1);
        if (SendMessageTimeout(
                hwnd, WmGetText, new IntPtr(buffer.Capacity), buffer,
                SmtoBlock | SmtoAbortIfHung, MessageTimeoutMs, out _) == IntPtr.Zero)
        {
            return false;
        }

        text = buffer.ToString();
        return true;
    }

    /// <summary>
    /// Asks the window how it counts characters, by selecting everything and reading back
    /// where the selection ends.
    /// </summary>
    private static Win32SelectionConvention DetectSelectionConvention(IntPtr hwnd, string windowText)
    {
        if (!windowText.Contains('\n'))
        {
            // No line breaks: every model agrees, and the probe would disturb the selection
            // for nothing.
            return Win32SelectionConvention.MatchesWindowText;
        }

        var haveSelection = TryGetSelection(hwnd, out var restoreStart, out var restoreEnd);

        if (!TrySend(hwnd, EmSetSel, IntPtr.Zero, new IntPtr(-1), out _)
            || !TryGetSelection(hwnd, out _, out var end))
        {
            return Win32SelectionConvention.Unknown;
        }

        if (haveSelection)
        {
            _ = TrySend(hwnd, EmSetSel, new IntPtr(restoreStart), new IntPtr(restoreEnd), out _);
        }

        var convention = Win32SelectionOffsets.DetectConvention(windowText, end);
        CompatibilityLogger.Technical(
            "win32-selection-convention",
            $"convention={convention} textLength={windowText.Length} selectAllEnd={end}");
        return convention;
    }

    private static bool TryGetSelection(IntPtr hwnd, out int start, out int end)
    {
        start = 0;
        end = 0;
        var range = new CharRange();
        if (SendMessageTimeout(
                hwnd, EmExGetSel, IntPtr.Zero, ref range,
                SmtoBlock | SmtoAbortIfHung, MessageTimeoutMs, out _) != IntPtr.Zero
            && (range.Min != 0 || range.Max != 0))
        {
            start = range.Min;
            end = range.Max;
            return true;
        }

        if (!TrySend(hwnd, EmGetSel, IntPtr.Zero, IntPtr.Zero, out var packed))
        {
            return false;
        }

        var value = (long)packed;
        start = (int)(value & 0xFFFF);
        end = (int)((value >> 16) & 0xFFFF);
        return true;
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
            // Window may have gone away between the checks and the send; the write did not happen.
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
            // Best effort for an already-gone window; the default line is fine.
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        StringBuilder lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        ref CharRange lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [StructLayout(LayoutKind.Sequential)]
    private struct CharRange
    {
        public int Min;
        public int Max;
    }
}

/// <summary>Why a verified Win32 range replacement did or did not happen.</summary>
internal enum Win32RangeWriteOutcome
{
    Written,
    BadArguments,
    TextUnavailable,
    RangeOutsideText,
    OriginalMismatch,
    OffsetsNotMappable,
    MessageRejected,
}
