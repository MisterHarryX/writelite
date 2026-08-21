namespace WriteLite.Services;

/// <summary>How a window counts characters when it is asked to select a range.</summary>
public enum Win32SelectionConvention
{
    /// <summary>Selection offsets index the text exactly as <c>WM_GETTEXT</c> returns it.</summary>
    MatchesWindowText,

    /// <summary>
    /// Selection offsets count each line break as one character, while <c>WM_GETTEXT</c>
    /// returns two. The RichEdit convention.
    /// </summary>
    LineBreaksCountOnce,

    /// <summary>Neither model fits what the window reported. Nothing may be written by offset.</summary>
    Unknown,
}

/// <summary>
/// Translates an offset into a window's own selection coordinates.
/// </summary>
/// <remarks>
/// <para><b>The bug this exists to prevent.</b> WriteLite analyses the string a text provider
/// hands it, and that string carries <c>\r\n</c> for a line break. It then replaced a range
/// by sending <c>EM_SETSEL</c> with the same numbers. On a classic multiline <c>EDIT</c> that
/// is correct, because the window counts <c>\r\n</c> as two characters too. On RichEdit it is
/// not: the selection model counts a line break once, so every line above the correction
/// shifted the selection one character to the right and the replacement landed inside a
/// neighbouring word. The failure is silent, grows with the size of the document, and cannot
/// be seen at all in a single-line field — which is where all the testing happens.</para>
///
/// <para>Rather than deciding per control class — a list that would be wrong for the next
/// edit control Microsoft ships — the convention is <em>measured</em> from the window itself
/// by <c>Win32TextEdit</c> and passed here. This type is the pure arithmetic, so the mapping
/// is testable without a window.</para>
/// </remarks>
public static class Win32SelectionOffsets
{
    /// <summary>
    /// Works out which convention a window uses, given the text it returned and the end
    /// offset it reports for a select-all.
    /// </summary>
    /// <param name="windowText">Exactly what <c>WM_GETTEXT</c> produced.</param>
    /// <param name="selectAllEnd">The end offset the window reported after selecting everything.</param>
    public static Win32SelectionConvention DetectConvention(string windowText, int selectAllEnd)
    {
        windowText ??= string.Empty;
        if (selectAllEnd == windowText.Length) return Win32SelectionConvention.MatchesWindowText;

        var carriageReturns = CountCarriageReturnsBefore(windowText, windowText.Length);
        return selectAllEnd == windowText.Length - carriageReturns
            ? Win32SelectionConvention.LineBreaksCountOnce
            : Win32SelectionConvention.Unknown;
    }

    /// <summary>
    /// Converts an offset into <paramref name="windowText"/> to the window's selection space.
    /// </summary>
    /// <returns>False when the offset cannot be mapped, in which case nothing may be written.</returns>
    public static bool TryMap(
        string windowText,
        int offset,
        Win32SelectionConvention convention,
        out int selectionOffset)
    {
        selectionOffset = 0;
        windowText ??= string.Empty;
        if (offset < 0 || offset > windowText.Length) return false;

        switch (convention)
        {
            case Win32SelectionConvention.MatchesWindowText:
                selectionOffset = offset;
                return true;

            case Win32SelectionConvention.LineBreaksCountOnce:
                selectionOffset = offset - CountCarriageReturnsBefore(windowText, offset);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// The number of carriage returns in <paramref name="text"/> strictly before
    /// <paramref name="offset"/> that are the first half of a <c>\r\n</c> pair.
    /// </summary>
    /// <remarks>
    /// Only paired returns collapse. A lone <c>\r</c> is already one character in both models,
    /// so counting it would over-correct and move the selection left of where it belongs.
    /// </remarks>
    private static int CountCarriageReturnsBefore(string text, int offset)
    {
        var count = 0;
        var limit = Math.Min(offset, text.Length);
        for (var i = 0; i < limit; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') count++;
        }

        return count;
    }
}
