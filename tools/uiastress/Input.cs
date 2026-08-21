using System.Runtime.InteropServices;

namespace WriteLite.UiaStress;

/// <summary>
/// Real mouse and keyboard input, for the gestures UI Automation cannot express.
/// </summary>
/// <remarks>
/// Dragging across text to select it, and right-clicking to raise a context menu, are
/// both physical gestures: there is no automation pattern that produces the same event
/// sequence, and a test that calls the handler directly proves nothing about whether the
/// gesture reaches it. Everything else in this driver goes through UI Automation, which
/// is more robust; this is the exception where the real thing is the point.
/// </remarks>
internal static class Input
{
    private const uint MouseeventfMove = 0x0001;
    private const uint MouseeventfAbsolute = 0x8000;
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private const uint MouseeventfRightdown = 0x0008;
    private const uint MouseeventfRightup = 0x0010;

    public static void ClickScreen(int x, int y)
    {
        MoveTo(x, y);
        Thread.Sleep(30);
        mouse_event(MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30);
        mouse_event(MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
    }

    public static void RightClickScreen(int x, int y)
    {
        MoveTo(x, y);
        Thread.Sleep(30);
        mouse_event(MouseeventfRightdown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30);
        mouse_event(MouseeventfRightup, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>Presses at one point, drags to another, releases — a text selection.</summary>
    public static void Drag(int fromX, int fromY, int toX, int toY)
    {
        MoveTo(fromX, fromY);
        Thread.Sleep(40);
        mouse_event(MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);

        // Stepped rather than jumped: a single teleport between press and release does
        // not always register as a drag in a text control.
        const int Steps = 12;
        for (var step = 1; step <= Steps; step++)
        {
            MoveTo(
                fromX + ((toX - fromX) * step / Steps),
                fromY + ((toY - fromY) * step / Steps));
            Thread.Sleep(12);
        }

        Thread.Sleep(40);
        mouse_event(MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
    }

    public static void MoveTo(int x, int y)
    {
        var width = GetSystemMetrics(0);
        var height = GetSystemMetrics(1);

        mouse_event(
            MouseeventfMove | MouseeventfAbsolute,
            (uint)(x * 65535 / Math.Max(1, width - 1)),
            (uint)(y * 65535 / Math.Max(1, height - 1)),
            0,
            UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
