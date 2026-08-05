using System.Runtime.InteropServices;

namespace WriteLite.Services;

/// <summary>
/// Lightweight recent-input probe (keyboard / mouse wheel excluded).
/// Used together with text deltas: focus alone is never enough to show UI.
/// Does not log key content — only timestamps.
/// </summary>
public sealed class UserInputActivityTracker
{
    private static readonly TimeSpan DefaultRecency = TimeSpan.FromMilliseconds(900);

    private readonly TimeSpan _recency;
    private DateTimeOffset _lastActivityUtc = DateTimeOffset.MinValue;

    public UserInputActivityTracker(TimeSpan? recency = null)
    {
        _recency = recency ?? DefaultRecency;
    }

    public DateTimeOffset LastActivityUtc => _lastActivityUtc;

    public void RecordActivity(DateTimeOffset? now = null)
    {
        _lastActivityUtc = now ?? DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Best-effort: if any non-modifier key is currently down, treat as activity.
    /// Called from the monitor poll on the UI thread; avoids a permanent low-level hook.
    /// </summary>
    public void SampleKeyboardState(DateTimeOffset? now = null)
    {
        // Printable / edit keys commonly used while typing. No text is captured.
        ReadOnlySpan<int> keys =
        [
            0x08, // VK_BACK
            0x09, // VK_TAB
            0x0D, // VK_RETURN
            0x2E, // VK_DELETE
            0x20  // VK_SPACE
        ];

        foreach (var vk in keys)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                RecordActivity(now);
                return;
            }
        }

        // A–Z / 0–9 / numpad — any down bit is enough to mark recency.
        for (var vk = 0x30; vk <= 0x5A; vk++)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                RecordActivity(now);
                return;
            }
        }

        for (var vk = 0x60; vk <= 0x69; vk++)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                RecordActivity(now);
                return;
            }
        }
    }

    public bool WasRecentlyActive(DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.UtcNow;
        return _lastActivityUtc > DateTimeOffset.MinValue && stamp - _lastActivityUtc <= _recency;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
