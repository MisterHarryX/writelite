using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WriteLite.Models;
using WriteLite.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace WriteLite.Views;

/// <summary>
/// A single non-activating transparent drawing surface. WM_NCHITTEST keeps every
/// pixel click-through except the small underline hit areas.
/// </summary>
public sealed class InlineErrorOverlayWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int HtClient = 1;
    private const int HtTransparent = -1;
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);
    private readonly InlineIssueSurface _surface = new();
    private HwndSource? _source;
    private IReadOnlyList<InlineIssueGeometry> _issues = [];
    private double _dpiX = 1;
    private double _dpiY = 1;
    private TextIssue? _mouseDownIssue;

    public InlineErrorOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        IsHitTestVisible = true;
        Content = _surface;
        SourceInitialized += OnSourceInitialized;
        CompatibilityLogger.Technical("inline-overlay-created", "instance=1");
    }

    public event EventHandler<InlineIssueClickedEventArgs>? IssueClicked;

    public void ShowIssues(Rect fieldBounds, IReadOnlyList<InlineIssueGeometry> issues)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _dpiX = dpi.DpiScaleX;
        _dpiY = dpi.DpiScaleY;
        var field = OverlayPlacementService.Scale(fieldBounds, _dpiX, _dpiY);

        Left = Math.Round(field.Left);
        Top = Math.Round(field.Top);
        Width = Math.Max(1, Math.Ceiling(field.Width));
        Height = Math.Max(1, Math.Ceiling(field.Height));
        _issues = issues;
        _surface.UpdateIssues(issues, fieldBounds, _dpiX, _dpiY);

        if (!IsVisible)
        {
            Show();
            CompatibilityLogger.Technical("inline-overlay-shown", $"geometryCount={issues.Count}");
        }
        EnsureSelectiveHitTestStyle();
        CompatibilityLogger.Technical("inline-overlay-reused", $"issueCount={issues.Count}");
    }

    public new void Hide()
    {
        if (IsVisible)
        {
            base.Hide();
            CompatibilityLogger.Technical("inline-overlay-hidden", "instance=retained");
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = PresentationSource.FromVisual(this) as HwndSource;
        if (_source is null) return;

        // AllowsTransparency can leave WS_EX_TRANSPARENT on some WPF configurations.
        // That style bypasses WM_NCHITTEST entirely, so remove it and perform selective
        // click-through ourselves below.
        _source.AddHook(WndProc);
        EnsureSelectiveHitTestStyle();
    }

    private void EnsureSelectiveHitTestStyle()
    {
        if (_source is null || _source.Handle == IntPtr.Zero)
        {
            return;
        }

        // WPF may reapply layered-window extended styles when a transparent
        // Window is shown again. Reassert the selective policy after every Show:
        // the HWND itself must be eligible for WM_NCHITTEST, while NOACTIVATE
        // preserves the caret and focus in the host editor.
        var before = GetWindowLongPtr(_source.Handle, GwlExStyle).ToInt64();
        var after = (before & ~WsExTransparent) | WsExNoActivate;
        if (after != before)
        {
            SetWindowLongPtr(_source.Handle, GwlExStyle, new IntPtr(after));
        }

        SetWindowPos(
            _source.Handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        CompatibilityLogger.Technical(
            "inline-overlay-style",
            $"transparent={((after & WsExTransparent) != 0 ? 1 : 0)} noActivate={((after & WsExNoActivate) != 0 ? 1 : 0)}");
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is not (WmNcHitTest or WmLButtonDown or WmLButtonUp))
        {
            return IntPtr.Zero;
        }

        // WM_NCHITTEST contains screen pixels, while button messages contain client
        // pixels.  Treating both as screen coordinates was the reason an apparently
        // visible underline could never be clicked unless the overlay happened to be at
        // the origin of the primary monitor.
        var point = message == WmNcHitTest
            ? ScreenPointFromLParam(lParam)
            : ScreenPointFromClientLParam(hwnd, lParam);
        var issue = _surface.HitTestScreen(point);
        if (message == WmNcHitTest)
        {
            handled = true;
            CompatibilityLogger.Technical("inline-hit-test", $"x={point.X:F0} y={point.Y:F0}");
            CompatibilityLogger.Technical(issue is null ? "inline-hit-miss" : "inline-hit-match",
                $"x={point.X:F0} y={point.Y:F0}");
            return new IntPtr(ResolveNcHitTest(issue is not null));
        }

        if (message == WmLButtonDown)
        {
            _mouseDownIssue = issue;
            CompatibilityLogger.Technical("inline-left-button-down", $"matched={(_mouseDownIssue is null ? 0 : 1)}");
            handled = issue is not null;
            return IntPtr.Zero;
        }

        CompatibilityLogger.Technical("inline-left-button-up", $"matched={(issue is null ? 0 : 1)}");
        if (issue is not null && ReferenceEquals(issue, _mouseDownIssue) && _surface.TryGetScreenAnchor(issue, out var anchor))
        {
            CompatibilityLogger.Technical("inline-issue-clicked", $"rule={issue.RuleId}");
            IssueClicked?.Invoke(this, new InlineIssueClickedEventArgs(issue, anchor));
            handled = true;
        }
        _mouseDownIssue = null;
        return IntPtr.Zero;
    }

    public static int ResolveNcHitTest(bool isOverIssue) => isOverIssue ? HtClient : HtTransparent;
    public static Rect CreateHitArea(Rect line, bool insertion) => InlineIssueSurface.CreateHitArea(line, insertion);

    private static Point ScreenPointFromLParam(IntPtr lParam) => new(
        unchecked((short)(long)lParam),
        unchecked((short)((long)lParam >> 16)));

    private static Point ScreenPointFromClientLParam(IntPtr hwnd, IntPtr lParam)
    {
        var point = new NativePoint
        {
            X = unchecked((short)(long)lParam),
            Y = unchecked((short)((long)lParam >> 16))
        };
        ClientToScreen(hwnd, ref point);
        return new Point(point.X, point.Y);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private sealed class InlineIssueSurface : FrameworkElement
    {
        private readonly List<(TextIssue Issue, Rect Hit, Rect Anchor)> _hits = [];
        private readonly List<(TextIssue Issue, Rect Rect)> _lines = [];

        public void UpdateIssues(IReadOnlyList<InlineIssueGeometry> issues, Rect fieldBounds, double dpiX, double dpiY)
        {
            _hits.Clear();
            _lines.Clear();
            foreach (var geometry in issues)
            {
                foreach (var screenRect in geometry.Rectangles)
                {
                    var local = new Rect(
                        screenRect.X / dpiX - fieldBounds.X / dpiX,
                        screenRect.Y / dpiY - fieldBounds.Y / dpiY,
                        screenRect.Width / dpiX,
                        screenRect.Height / dpiY);
                    if (local.Width <= 0 || local.Height <= 0) continue;
                    _lines.Add((geometry.Issue, local));
                    _hits.Add((geometry.Issue, CreateHitArea(local, geometry.Issue.Length == 0), local));
                }
            }

            InvalidateVisual();
        }

        public TextIssue? HitTestScreen(Point screenPoint)
        {
            // Let WPF perform the screen-pixel to local-DIP conversion.  This is
            // per-monitor-DPI aware and correctly handles monitors at negative origins.
            var local = PointFromScreen(screenPoint);
            return _hits.FirstOrDefault(hit => hit.Hit.Contains(local)).Issue;
        }

        public bool TryGetScreenAnchor(TextIssue issue, out Rect anchor)
        {
            var hit = _hits.FirstOrDefault(candidate => ReferenceEquals(candidate.Issue, issue));
            if (hit.Issue is null) { anchor = Rect.Empty; return false; }
            var topLeft = PointToScreen(hit.Anchor.TopLeft);
            var dpi = VisualTreeHelper.GetDpi(this);
            anchor = new Rect(topLeft, new Size(hit.Anchor.Width * dpi.DpiScaleX, hit.Anchor.Height * dpi.DpiScaleY));
            return true;
        }

        public static Rect CreateHitArea(Rect line, bool insertion) => insertion
            ? new Rect(line.Left - 3, line.Bottom - 11, 10, 12)
            : new Rect(line.Left - 2, line.Top - 2, Math.Max(8, line.Width) + 4, line.Height + 4);

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            // A per-pixel-alpha layered window does not receive mouse input on
            // completely transparent pixels. Alpha=1 is visually imperceptible
            // but keeps the exact word rectangles eligible for WM_NCHITTEST.
            var hitBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));
            hitBrush.Freeze();
            foreach (var hit in _hits)
            {
                drawingContext.DrawRectangle(hitBrush, null, hit.Hit);
            }

            foreach (var (issue, rect) in _lines)
            {
                var style = InlineIssuePresentation.StyleFor(issue);
                var brush = new SolidColorBrush(style.Color) { Opacity = style.Opacity };
                brush.Freeze();
                var pen = new Pen(brush, style.Thickness)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                pen.Freeze();

                if (issue.Length == 0)
                {
                    // Quiet insertion caret marker — short vertical tick, not a full-field line.
                    drawingContext.DrawGeometry(null, pen, IssueWaveGeometry.BuildInsertionTick(rect));
                    continue;
                }

                // Never stretch a decoration across the whole field when geometry is missing:
                // lines are only added when UIA returned a non-empty range rectangle.
                if (rect.Width < 1 || rect.Height < 1) continue;

                drawingContext.DrawGeometry(null, pen, IssueWaveGeometry.BuildWave(rect, style.WaveHeight));
            }
        }
    }
}
