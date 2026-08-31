using System.Runtime.InteropServices;

namespace CardProbe;

/// <summary>
/// Synthesised mouse and keyboard, because WriteLite only shows its chrome for real editing.
/// </summary>
/// <remarks>
/// The interaction state machine opens the overlay after it has observed a user editing the
/// field — a programmatic <c>ValuePattern.SetValue</c> is classified as an automation change
/// and deliberately does not count. A probe that set the value directly would be measuring a
/// path no user takes, so everything here goes in as real input.
/// </remarks>
internal static class Input
{
    private const uint MouseMove = 0x0001;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;

    private const uint InputKeyboard = 1;
    private const uint KeyEventUnicode = 0x0004;
    private const uint KeyEventKeyUp = 0x0002;

    public static void MoveTo(int x, int y)
    {
        var width = GetSystemMetrics(0);
        var height = GetSystemMetrics(1);
        mouse_event(
            MouseMove | MouseAbsolute,
            (uint)(x * 65535 / Math.Max(1, width - 1)),
            (uint)(y * 65535 / Math.Max(1, height - 1)),
            0,
            UIntPtr.Zero);
    }

    public static void Click(int x, int y)
    {
        MoveTo(x, y);
        Thread.Sleep(40);
        mouse_event(MouseLeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        mouse_event(MouseLeftUp, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
    }

    public static void DoubleClick(int x, int y)
    {
        MoveTo(x, y);
        Thread.Sleep(40);
        mouse_event(MouseLeftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseLeftUp, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        mouse_event(MouseLeftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseLeftUp, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
    }

    /// <summary>Types a string as Unicode keystrokes, so any layout produces Cyrillic.</summary>
    public static void Type(string text, int perCharacterDelayMs = 18)
    {
        foreach (var character in text)
        {
            SendUnicode(character, up: false);
            SendUnicode(character, up: true);
            Thread.Sleep(perCharacterDelayMs);
        }
    }

    public static void PressVirtualKey(ushort virtualKey, int repeat = 1)
    {
        for (var i = 0; i < repeat; i++)
        {
            SendVirtual(virtualKey, up: false);
            SendVirtual(virtualKey, up: true);
            Thread.Sleep(12);
        }
    }

    public const ushort VkBack = 0x08;
    public const ushort VkEnd = 0x23;
    public const ushort VkHome = 0x24;

    private static void SendUnicode(char character, bool up) => Send(new NativeInput
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = 0,
                ScanCode = character,
                Flags = KeyEventUnicode | (up ? KeyEventKeyUp : 0)
            }
        }
    });

    private static void SendVirtual(ushort virtualKey, bool up) => Send(new NativeInput
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = virtualKey,
                Flags = up ? KeyEventKeyUp : 0
            }
        }
    });

    private static void Send(NativeInput input)
    {
        var sent = SendInput(1, [input], Marshal.SizeOf<NativeInput>());
        if (sent == 0)
        {
            throw new InvalidOperationException(
                $"SendInput delivered nothing (INPUT measured {Marshal.SizeOf<NativeInput>()} bytes).");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Data;
    }

    /// <summary>
    /// The <c>INPUT</c> union.
    /// </summary>
    /// <remarks>
    /// <c>MouseInputData</c> is declared even though no mouse event is sent through
    /// <c>SendInput</c>, because it is the largest member and therefore the one that sizes the
    /// union. Without it the struct measures 32 bytes on x64 instead of the 40
    /// <c>SendInput</c> expects, the <c>cbSize</c> check fails, and the call returns zero
    /// having delivered nothing — a silent no-op rather than an error.
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
