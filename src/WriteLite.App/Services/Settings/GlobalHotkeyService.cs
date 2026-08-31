using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WriteLite.Services.Settings;

/// <summary>
/// Registers WriteLite's system-wide shortcuts with Windows.
/// </summary>
/// <remarks>
/// <para><b>Why <c>RegisterHotKey</c> and not the mouse hook this application already has.</b>
/// A keyboard hook would see every keystroke on the desktop, including into password fields
/// and other people's messages, to answer one question about one combination. Registering the
/// combination asks Windows to tell WriteLite when that specific chord is pressed and nothing
/// else — the same answer with none of the access. For an application whose entire premise is
/// that text stays on the machine, that difference is the whole argument.</para>
///
/// <para><b>Failure is normal and is not an error.</b> A combination another application
/// registered first is refused, and the honest response is to say so in the log and carry on:
/// the shortcut simply does not work, everything else does, and the settings page can be used
/// to choose another. Treating it as fatal would let any background application prevent
/// WriteLite from starting.</para>
///
/// <para>The message hook lives on a <see cref="HwndSource"/> belonging to a window the
/// application owns for its lifetime. <c>RegisterHotKey</c> binds to a thread and a window
/// handle, so a handle that comes and goes with a window the user can close would take the
/// registration with it.</para>
/// </remarks>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;

    [Flags]
    private enum HotkeyModifiers : uint
    {
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Windows = 0x0008,

        /// <summary>Stops key repeat from firing the shortcut once per repeat.</summary>
        NoRepeat = 0x4000
    }

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, string> _registered = [];
    private readonly Dictionary<string, Action> _handlers = new(StringComparer.Ordinal);

    private HwndSource? _source;
    private int _nextId = 0xB100;
    private bool _disposed;

    public GlobalHotkeyService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>What to run when a given shortcut fires.</summary>
    public void Handle(string shortcutId, Action action) => _handlers[shortcutId] = action;

    /// <summary>
    /// Registers every global shortcut in the registry, replacing any previous registration.
    /// </summary>
    /// <returns>How many were accepted by Windows.</returns>
    public int Apply(ShortcutRegistry registry)
    {
        if (_disposed) return 0;

        EnsureWindow();
        if (_source?.Handle is not { } handle || handle == IntPtr.Zero)
        {
            return 0;
        }

        UnregisterAll(handle);

        var accepted = 0;
        foreach (var definition in ShortcutRegistry.Definitions)
        {
            if (definition.Scope != ShortcutScope.Global) continue;
            if (!ShortcutGesture.TryParse(registry.GestureOf(definition.Id), out var key, out var modifiers))
            {
                continue;
            }

            var id = _nextId++;
            var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey == 0) continue;

            if (RegisterHotKey(handle, id, (uint)(Translate(modifiers) | HotkeyModifiers.NoRepeat), virtualKey))
            {
                _registered[id] = definition.Id;
                accepted++;
            }
            else
            {
                // Almost always "another application got there first". Named in the log so a
                // reader who says the shortcut does nothing can be answered from it.
                CompatibilityLogger.Technical(
                    "global-hotkey-rejected",
                    $"shortcut={definition.Id} gesture={registry.GestureOf(definition.Id)}");
            }
        }

        CompatibilityLogger.Technical("global-hotkeys-registered", $"count={accepted}");
        return accepted;
    }

    /// <summary>True when this shortcut is currently live system-wide.</summary>
    public bool IsRegistered(string shortcutId) =>
        _registered.ContainsValue(shortcutId);

    private static HotkeyModifiers Translate(ModifierKeys modifiers)
    {
        var value = default(HotkeyModifiers);
        if (modifiers.HasFlag(ModifierKeys.Control)) value |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) value |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) value |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) value |= HotkeyModifiers.Windows;
        return value;
    }

    /// <summary>
    /// A message-only window to receive <c>WM_HOTKEY</c>.
    /// </summary>
    /// <remarks>
    /// Never shown and never in the taskbar: it exists to own a handle. Borrowing the main
    /// window's handle instead would tie every global shortcut to whether that window has
    /// been created yet, and WriteLite starts into the tray with no window at all.
    /// </remarks>
    private void EnsureWindow()
    {
        if (_source is not null) return;

        _source = new HwndSource(new HwndSourceParameters("WriteLite hotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3)   // HWND_MESSAGE
        });

        _source.AddHook(OnMessage);
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey || !_registered.TryGetValue(wParam.ToInt32(), out var shortcutId))
        {
            return IntPtr.Zero;
        }

        handled = true;

        if (!_handlers.TryGetValue(shortcutId, out var action))
        {
            return IntPtr.Zero;
        }

        // Posted rather than run here: this is inside the window procedure, and opening a
        // window from inside one is how a shell integration deadlocks.
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Input, action);
        return IntPtr.Zero;
    }

    private void UnregisterAll(IntPtr handle)
    {
        foreach (var id in _registered.Keys)
        {
            UnregisterHotKey(handle, id);
        }

        _registered.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_source is { Handle: var handle } && handle != IntPtr.Zero)
        {
            UnregisterAll(handle);
            _source.RemoveHook(OnMessage);
        }

        _source?.Dispose();
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
