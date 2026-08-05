using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Runtime.InteropServices;
using WriteLite.Models;
using WriteLite.Services;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Size = System.Windows.Size;
using MediaBrush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using Cursors = System.Windows.Input.Cursors;

namespace WriteLite.Views;

/// <summary>A quiet, reusable correction menu. It is intentionally independent from the target app's typography.</summary>
public sealed class CorrectionPopupWindow : Window
{
    private static readonly MediaBrush CardBackground = ThemeResource.Brush("WlSurface", Brushes.Black);
    private static readonly MediaBrush RaisedBackground = ThemeResource.Brush("WlRaised", Brushes.DarkSlateGray);
    private static readonly MediaBrush CardBorderBrush = ThemeResource.Brush("WlBorder", Brushes.DimGray);
    private static readonly MediaBrush TextBrush = ThemeResource.Brush("WlText", Brushes.White);
    private static readonly MediaBrush MutedBrush = ThemeResource.Brush("WlTextSecondary", Brushes.LightGray);
    private static readonly MediaBrush AccentBrush = ThemeResource.Brush("WlBrand", Brushes.Orange);
    private static readonly MediaBrush ErrorBrush = ThemeResource.Brush("WlDanger", Brushes.OrangeRed);
    private readonly TextBlock _category = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = TextBrush };
    private readonly TextBlock _change = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = TextBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 2) };
    private readonly TextBlock _explanation = new() { FontSize = 13, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 9), LineHeight = 19 };
    private readonly TextBlock _status = new()
    {
        FontSize = 12,
        Foreground = AccentBrush,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 6),
        Visibility = Visibility.Collapsed
    };
    private readonly StackPanel _suggestions = new() { Margin = new Thickness(0, 0, 0, 9) };
    private readonly Button _dictionary;
    private TextIssue? _issue;
    private Rect _pendingPhysicalAnchor;
    private bool _repositionPending;

    public CorrectionPopupWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 360;
        MinHeight = 148;
        MaxHeight = 360;
        SizeToContent = SizeToContent.Height;
        FontFamily = ThemeResource.Font("WlFont", "Segoe UI Variable Text, Segoe UI");

        var card = new Border
        {
            Background = CardBackground,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 14, 16, 14),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Direction = 270, Opacity = .55, Color = Colors.Black }
        };
        card.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == card) DragMove(); };

        var root = new StackPanel();
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(7), Background = AccentBrush,
            Child = new TextBlock { Text = "W", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        var title = new TextBlock { Text = "WriteLite", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(brand);
        titleRow.Children.Add(title);
        var close = CreateTextButton("\u00d7", 24, 24, 17);
        close.ToolTip = "\u0417\u0430\u043a\u0440\u044b\u0442\u044c";
        close.Click += (_, _) => Hide();
        Grid.SetColumn(close, 1); header.Children.Add(titleRow); header.Children.Add(close);

        var categoryRow = new StackPanel { Orientation = Orientation.Horizontal };
        categoryRow.Children.Add(new Border { Width = 3, Height = 18, CornerRadius = new CornerRadius(2), Background = AccentBrush, Margin = new Thickness(0, 0, 8, 0) });
        categoryRow.Children.Add(_category);
        _dictionary = CreateTextButton("\u0412 \u0441\u043b\u043e\u0432\u0430\u0440\u044c", double.NaN, 26, 12);
        _dictionary.Margin = new Thickness(6, 0, 0, 0);
        _dictionary.Click += (_, _) => { if (_issue is not null) AddToDictionaryRequested?.Invoke(this, _issue); };

        var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 1, 0, 0) };
        var ignore = CreateTextButton("\u0418\u0433\u043d\u043e\u0440\u0438\u0440\u043e\u0432\u0430\u0442\u044c", double.NaN, 27, 12);
        ignore.Click += (_, _) => { if (_issue is not null) IgnoreRequested?.Invoke(this, _issue); };
        DockPanel.SetDock(ignore, Dock.Right); footer.Children.Add(ignore);
        DockPanel.SetDock(_dictionary, Dock.Left); footer.Children.Add(_dictionary);

        root.Children.Add(header); root.Children.Add(categoryRow); root.Children.Add(_change); root.Children.Add(_explanation); root.Children.Add(_status); root.Children.Add(_suggestions); root.Children.Add(footer);
        card.Child = root; Content = card;
    }

    public event EventHandler<TextIssue>? ApplyRequested;
    public event EventHandler<TextIssue>? IgnoreRequested;
    public event EventHandler<TextIssue>? AddToDictionaryRequested;

    /// <summary>Shows a non-empty failure or progress message so the card never fails silently.</summary>
    public void ShowApplyFeedback(string message, bool isError = true)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowApplyFeedback(message, isError));
            return;
        }

        _status.Text = message;
        _status.Foreground = isError ? ThemeResource.Brush("WlDanger", Brushes.OrangeRed) : AccentBrush;
        _status.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ShowForIssue(TextIssue issue, Rect physicalScreenAnchor)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowForIssue(issue, physicalScreenAnchor));
            return;
        }

        var displayIssue = CorrectionPresentation.NormalizeForApply(issue);
        _issue = displayIssue;
        _category.Text = CategoryName(displayIssue.Category);
        _change.Text = CorrectionPresentation.FormatChange(displayIssue);
        _explanation.Text = displayIssue.Explanation;
        _status.Text = string.Empty;
        _status.Visibility = Visibility.Collapsed;
        _suggestions.Children.Clear();
        if (!string.IsNullOrWhiteSpace(displayIssue.Replacement))
        {
            // Chip applies immediately; label is human-friendly for punctuation inserts.
            var chip = CreateSuggestionChip(
                $"Исправить: {CorrectionPresentation.FormatChipLabel(displayIssue)}");
            chip.IsEnabled = true;
            chip.Click += async (_, _) =>
            {
                chip.IsEnabled = false;
                _status.Text = "Применение…";
                _status.Visibility = Visibility.Visible;
                try
                {
                    ApplyRequested?.Invoke(this, displayIssue);
                }
                finally
                {
                    // Re-enable after a short delay if still open (host will hide on success).
                    await Task.Delay(1200);
                    if (IsVisible) chip.IsEnabled = true;
                }
            };
            _suggestions.Children.Add(chip);
            _suggestions.Children.Add(new TextBlock
            {
                Text = "Один клик — применить исправление",
                FontSize = 11,
                Foreground = MutedBrush,
                Margin = new Thickness(2, 6, 0, 0)
            });
        }

        _dictionary.Visibility = issue.Category == IssueCategory.Orthography ? Visibility.Visible : Visibility.Collapsed;
        _pendingPhysicalAnchor = physicalScreenAnchor;
        // Use a conservative provisional size before the first WPF measure.  The final
        // placement below uses ActualHeight, so a wrapped explanation cannot be clipped
        // or placed beyond the current monitor's working area.
        PositionForAnchor(_pendingPhysicalAnchor, new Size(Width, Math.Max(MinHeight, 184)));
        // A correction popup must not steal focus from the edited field: stealing it
        // makes the monitor clear the active target and immediately closes the popup.
        if (!IsVisible)
        {
            Show();
        }

        ScheduleMeasuredReposition();
    }

    public void ShowIssue(TextIssue issue, Rect physicalScreenAnchor) => ShowForIssue(issue, physicalScreenAnchor);

    /// <summary>Places the popup using the measured card size. Exposed for deterministic placement tests.</summary>
    public static OverlayPlacementResult CalculatePlacement(Rect anchor, Rect workArea, Size popupSize) =>
        OverlayPlacementService.PlaceCorrectionPopup(anchor, workArea, popupSize);

    private void ScheduleMeasuredReposition()
    {
        if (_repositionPending) return;
        _repositionPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            _repositionPending = false;
            if (!IsVisible) return;
            UpdateLayout();
            PositionForAnchor(_pendingPhysicalAnchor, new Size(ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : MinHeight));
        }));
    }

    private void PositionForAnchor(Rect physicalScreenAnchor, Size popupSize)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var anchor = OverlayPlacementService.Scale(physicalScreenAnchor, dpi.DpiScaleX, dpi.DpiScaleY);
        var workArea = GetCurrentWorkArea(physicalScreenAnchor, dpi.DpiScaleX, dpi.DpiScaleY);
        var placement = CalculatePlacement(anchor, workArea, popupSize);
        Left = placement.Location.X;
        Top = placement.Location.Y;
    }

    private static Rect GetCurrentWorkArea(Rect physicalAnchor, double dpiX, double dpiY)
    {
        var point = new NativePoint
        {
            X = (int)Math.Round(physicalAnchor.Left),
            Y = (int)Math.Round(physicalAnchor.Top)
        };
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            var work = new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
            return OverlayPlacementService.Scale(work, dpiX, dpiY);
        }

        return SystemParameters.WorkArea;
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private static Button CreateSuggestionChip(string text)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.Black,
            Background = AccentBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 9, 14, 9),
            Margin = new Thickness(0, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
            // Keep mouse events on the chip (do not let transparent host steal MouseUp).
            Focusable = false,
            IsHitTestVisible = true
        };
        button.Template = RoundedTemplate(new CornerRadius(10));
        button.PreviewMouseLeftButtonDown += (_, e) => e.Handled = false;
        button.PreviewMouseLeftButtonUp += (_, e) => e.Handled = false;
        return button;
    }

    private static Button CreateTextButton(string text, double width, double height, double fontSize)
    {
        var button = new Button
        {
            Content = text, Width = width, Height = height, FontSize = fontSize,
            Foreground = MutedBrush, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(7, 2, 7, 2), Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Template = RoundedTemplate(new CornerRadius(6));
        return button;
    }

    private static ControlTemplate RoundedTemplate(CornerRadius radius)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "border";
        border.SetValue(Border.CornerRadiusProperty, radius);
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private static string CategoryName(IssueCategory category) => category switch
    {
        IssueCategory.Orthography => "\u041e\u0440\u0444\u043e\u0433\u0440\u0430\u0444\u0438\u044f",
        IssueCategory.Punctuation => "\u041f\u0443\u043d\u043a\u0442\u0443\u0430\u0446\u0438\u044f",
        IssueCategory.Grammar => "\u0413\u0440\u0430\u043c\u043c\u0430\u0442\u0438\u043a\u0430",
        _ => "\u0421\u0442\u0438\u043b\u044c"
    };

}
