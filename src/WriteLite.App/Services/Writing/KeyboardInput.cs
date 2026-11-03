using System.Runtime.InteropServices;

namespace WriteLite.Services.Writing;

/// <summary>
/// The one keystroke WriteLite ever synthesises.
/// </summary>
/// <remarks>
/// <para>Deliberately not a general "send keys" helper. Synthesised input goes to whatever
/// has the keyboard, and a general facility invites a caller to send something that commits,
/// navigates or destroys — an Enter in a messenger composer sends the message. There is
/// exactly one gesture here, it pastes, and there is no way to ask this type for anything
/// else.</para>
///
/// <para>The modifier is released in the reverse order it was pressed and released even if
/// the paste itself fails, so a stuck Ctrl cannot outlive the call and turn the user's next
/// keystroke into a shortcut.</para>
/// </remarks>
internal static class KeyboardInput
{
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    /// <summary>Presses Ctrl+V against the foreground window.</summary>
    public static void SendPaste()
    {
        var inputs = new[]
        {
            Key(VkControl, down: true),
            Key(VkV, down: true),
            Key(VkV, down: false),
            Key(VkControl, down: false),
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length) return;

        CompatibilityLogger.Technical("send-input-partial", $"sent={sent} expected={inputs.Length}");

        // Whatever went through, make sure Ctrl is not left down.
        var release = new[] { Key(VkV, down: false), Key(VkControl, down: false) };
        _ = SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
    }

    /// <summary>
    /// Types a word completion the user accepted with Tab, as Unicode characters.
    /// </summary>
    /// <remarks>
    /// The second and last gesture. It carries letters, digits, spaces and ordinary punctuation
    /// only: control characters — Enter, Tab, Backspace — are refused outright, so a completion
    /// can never send a message, move focus or delete anything. Typing (rather than writing the
    /// field's value) is what keeps the caret after the inserted text and the host's own undo
    /// working, in every kind of control.
    /// </remarks>
    public static bool TypeText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Any(char.IsControl)) return false;

        var inputs = new List<Input>(text.Length * 2);
        foreach (var c in text)
        {
            inputs.Add(Unicode(c, down: true));
            inputs.Add(Unicode(c, down: false));
        }

        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
        if (sent != inputs.Count)
        {
            CompatibilityLogger.Technical("send-input-partial", $"sent={sent} expected={inputs.Count} kind=unicode");
            return false;
        }

        return true;
    }

    private static Input Unicode(char c, bool down) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = 0,
                ScanCode = c,
                Flags = KeyEventUnicode | (down ? 0 : KeyEventKeyUp),
                Time = 0,
                ExtraInfo = WriteLiteInputMarker,
            },
        },
    };

    private const uint KeyEventUnicode = 0x0004;

    /// <summary>Marks keystrokes WriteLite typed itself, so its own keyboard watch can tell them apart.</summary>
    public static readonly IntPtr WriteLiteInputMarker = new(0x57524C54);

    private static Input Key(ushort virtualKey, bool down) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = virtualKey,
                ScanCode = 0,
                Flags = down ? 0 : KeyEventKeyUp,
                Time = 0,
                ExtraInfo = IntPtr.Zero,
            },
        },
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

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
