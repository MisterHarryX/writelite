using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WriteLite.Services.FieldActions;

/// <summary>
/// A low-level keyboard hook that exists only while a prediction chip is on screen.
/// </summary>
/// <remarks>
/// <para>WriteLite deliberately has no permanent keyboard hook. This one is installed when the chip
/// appears and removed when it goes, and it reads key codes only — never characters, never text.
/// Tab is swallowed and reported (accept); any other key is passed through untouched and reported
/// (the chip is stale the moment the user types on), and the owner removes the hook.</para>
///
/// <para>The callback must return quickly (Windows drops slow hooks), so it only raises events;
/// the owner does the work after the callback returns.</para>
/// </remarks>
public sealed class TemporaryKeyboardWatch : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkTab = 0x09;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLShift = 0xA0;
    private const int VkRMenu = 0xA5;
    private const int LlkhfInjected = 0x10;

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook;

    public TemporaryKeyboardWatch()
    {
        _proc = Callback;
    }

    /// <summary>Tab was pressed (and swallowed).</summary>
    public event Action? AcceptPressed;

    /// <summary>Any other key went down; it reached the application as usual.</summary>
    public event Action? OtherKeyPressed;

    public bool IsActive => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero) CompatibilityLogger.Technical("prediction-hook-failed", $"error={Marshal.GetLastWin32Error()}");
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown))
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            // Only WriteLite's own typing is ignored; other injected input (remote desktop,
            // accessibility tools, automation) is a user pressing keys like any other.
            var injected = (data.Flags & LlkhfInjected) != 0 && data.ExtraInfo == Writing.KeyboardInput.WriteLiteInputMarker;
            var vk = (int)data.VkCode;
            if (!injected && vk is not (VkShift or VkControl or VkMenu) && vk is not (>= VkLShift and <= VkRMenu))
            {
                var modifiers = (GetKeyState(VkControl) & 0x8000) != 0 || (GetKeyState(VkMenu) & 0x8000) != 0
                                || (GetKeyState(VkShift) & 0x8000) != 0;
                if (vk == VkTab && !modifiers)
                {
                    AcceptPressed?.Invoke();
                    return 1;
                }

                OtherKeyPressed?.Invoke();
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => Stop();

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
