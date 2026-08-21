using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Runtime.InteropServices;
using WriteLite.Language.Core;
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
using Run = System.Windows.Documents.Run;

namespace WriteLite.Views;

/// <summary>
/// The click-to-correct popover shown next to an underlined word in another application.
/// </summary>
/// <remarks>
/// Deliberately independent of the target app's typography and colours: it carries the
/// WriteLite surface so a correction looks the same in Notepad, Word and a browser field.
/// The hierarchy matches the correction cards elsewhere in the product \u2014 category, then
/// the change, then the rule, then the actions.
/// </remarks>
public sealed class CorrectionPopupWindow : Window
{
    private static readonly MediaBrush CardBackground = ThemeResource.Brush("WlSurface", Brushes.Black);
    private static readonly MediaBrush RaisedBackground = ThemeResource.Brush("WlRaised", Brushes.DarkSlateGray);
    private static readonly MediaBrush CardBorderBrush = ThemeResource.Brush("WlLineStrong", Brushes.DimGray);
    private static readonly MediaBrush TextBrush = ThemeResource.Brush("WlText", Brushes.White);
    private static readonly MediaBrush MutedBrush = ThemeResource.Brush("WlTextSecondary", Brushes.LightGray);
    private static readonly MediaBrush FaintBrush = ThemeResource.Brush("WlTextMuted", Brushes.Gray);
    private static readonly MediaBrush AccentBrush = ThemeResource.Brush("WlBrand", Brushes.Orange);
    private static readonly MediaBrush OnAccentBrush = ThemeResource.Brush("WlOnBrand", Brushes.Black);
    private static readonly MediaBrush ErrorBrush = ThemeResource.Brush("WlDanger", Brushes.OrangeRed);
    private static readonly FontFamily MonoFont = ThemeResource.Font("WlFontMono", "Cascadia Mono, Consolas");

    private readonly Border _categoryMark = new()
    {
        Width = 2,
        Height = 10,
        CornerRadius = new CornerRadius(1),
        Background = AccentBrush,
        Margin = new Thickness(0, 0, 9, 0),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _category = new() { FontSize = 9, FontFamily = MonoFont, Foreground = AccentBrush, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _original = new() { FontSize = 13, FontFamily = MonoFont, Foreground = FaintBrush, TextDecorations = TextDecorations.Strikethrough, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _arrow = new() { Text = "\u2192", FontSize = 12, Foreground = FaintBrush, Margin = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _replacement = new() { FontSize = 13, FontFamily = MonoFont, Foreground = AccentBrush, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _explanation = new() { FontSize = 12.5, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), LineHeight = 19 };
    private readonly TextBlock _status = new()
    {
        FontSize = 12,
        Foreground = AccentBrush,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 0),
        Visibility = Visibility.Collapsed
    };

    /// <summary>
    /// Alternative replacements. The analyzer currently produces a single one, so this
    /// holds one chip today; the layout takes a list so richer suggestions need no redesign.
    /// </summary>
    private readonly StackPanel _suggestions = new() { Margin = new Thickness(0, 13, 0, 0) };

    /// <summary>What the user can still do when the correction could not be written.</summary>
    private readonly StackPanel _recovery = new()
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 8, 0, 0),
        Visibility = Visibility.Collapsed
    };

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
        MinHeight = 140;
        MaxHeight = 360;
        SizeToContent = SizeToContent.Height;
        FontFamily = ThemeResource.Font("WlFont", "Segoe UI Variable Text, Segoe UI");

        var card = new Border
        {
            Background = CardBackground,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 14, 16, 14),
            Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 8, Direction = 270, Opacity = .6, Color = Colors.Black }
        };
        card.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == card) DragMove(); };

        var root = new StackPanel();

        // Header: the category leads, exactly as on the website's correction card.
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var categoryRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        categoryRow.Children.Add(_categoryMark);
        categoryRow.Children.Add(_category);
        var close = CreateGhostButton("\u00d7", 22, 22, 15);
        close.ToolTip = "\u0417\u0430\u043a\u0440\u044b\u0442\u044c";
        close.Click += (_, _) => Hide();
        Grid.SetColumn(close, 1);
        header.Children.Add(categoryRow);
        header.Children.Add(close);

        // \u0441\u0442\u0430\u043d\u043e\u0432\u0438\u0442\u044c\u0441\u044f \u2192 \u0441\u0442\u0430\u043d\u043e\u0432\u0438\u0442\u0441\u044f
        var changeRow = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        changeRow.Children.Add(_original);
        changeRow.Children.Add(_arrow);
        changeRow.Children.Add(_replacement);

        var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };
        var ignore = CreateGhostButton("\u0418\u0433\u043d\u043e\u0440\u0438\u0440\u043e\u0432\u0430\u0442\u044c", double.NaN, 26, 12);
        ignore.Click += (_, _) => { if (_issue is not null) IgnoreRequested?.Invoke(this, _issue); };
        _dictionary = CreateGhostButton("\u0412 \u0441\u043b\u043e\u0432\u0430\u0440\u044c", double.NaN, 26, 12);
        _dictionary.Click += (_, _) => { if (_issue is not null) AddToDictionaryRequested?.Invoke(this, _issue); };
        DockPanel.SetDock(ignore, Dock.Right);
        footer.Children.Add(ignore);
        DockPanel.SetDock(_dictionary, Dock.Left);
        footer.Children.Add(_dictionary);

        root.Children.Add(header);
        root.Children.Add(changeRow);
        root.Children.Add(_explanation);
        root.Children.Add(_status);
        root.Children.Add(_recovery);
        root.Children.Add(_suggestions);
        root.Children.Add(footer);
        card.Child = root;
        Content = card;
    }

    public event EventHandler<TextIssue>? ApplyRequested;
    public event EventHandler<TextIssue>? IgnoreRequested;
    public event EventHandler<TextIssue>? AddToDictionaryRequested;

    /// <summary>Raised when the user asks for the replacement on the clipboard instead.</summary>
    public event EventHandler<TextIssue>? CopyRequested;

    /// <summary>Shows a non-empty failure or progress message so the card never fails silently.</summary>
    public void ShowApplyFeedback(string message, bool isError = true)
        => ShowApplyFeedback(message, isError, offerCopy: false, offerRetry: false);

    /// <summary>
    /// Reports a failed correction on the card, with whatever the user can still do about it.
    /// </summary>
    /// <remarks>
    /// <para>§12: an ordinary correction failure is not a modal event. It used to raise a
    /// <c>MessageBox</c>, which stops the world, takes focus away from the field the user was
    /// editing, and has to be dismissed before they can even see the word it is talking
    /// about. The card is already on screen and already points at the right word, so the
    /// failure belongs on the card.</para>
    ///
    /// <para>The offered actions follow the reason rather than the failure. A genuinely
    /// read-only control gets «Скопировать», because copying is the only thing left. A field
    /// that should have been writable also gets «Повторить», because the usual causes — a
    /// window that just changed, a provider that was busy — pass.</para>
    /// </remarks>
    public void ShowApplyFeedback(string message, bool isError, bool offerCopy, bool offerRetry)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowApplyFeedback(message, isError, offerCopy, offerRetry));
            return;
        }

        _status.Text = message;
        _status.Foreground = isError ? ThemeResource.Brush("WlDanger", Brushes.OrangeRed) : AccentBrush;
        _status.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;

        _recovery.Children.Clear();
        _recovery.Visibility = offerCopy || offerRetry ? Visibility.Visible : Visibility.Collapsed;

        if (offerCopy)
        {
            var copy = CreateGhostButton("Скопировать", double.NaN, 26, 12);
            copy.Click += (_, _) => { if (_issue is not null) CopyRequested?.Invoke(this, _issue); };
            _recovery.Children.Add(copy);
        }

        if (offerRetry)
        {
            var retry = CreateGhostButton("Повторить", double.NaN, 26, 12);
            retry.Click += (_, _) =>
            {
                if (_issue is null) return;
                _status.Text = "Применение…";
                _recovery.Visibility = Visibility.Collapsed;
                ApplyRequested?.Invoke(this, _issue);
            };
            _recovery.Children.Add(retry);
        }
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

        var categoryBrush = CorrectionCardText.CategoryBrushes(displayIssue.Category).Foreground;
        _categoryMark.Background = categoryBrush;
        _category.Foreground = categoryBrush;
        Controls.Type.SetTracked(_category, CorrectionCardText.CategoryLabelUpper(displayIssue.Category));

        var originalDisplay = CorrectionCardText.OriginalDisplay(displayIssue);
        var replacementDisplay = CorrectionCardText.ReplacementDisplay(displayIssue);
        RenderChange(originalDisplay, replacementDisplay);
        var hasReplacement = !string.IsNullOrWhiteSpace(displayIssue.Replacement);
        _arrow.Visibility = hasReplacement ? Visibility.Visible : Visibility.Collapsed;
        _replacement.Visibility = hasReplacement ? Visibility.Visible : Visibility.Collapsed;

        _explanation.Text = displayIssue.Explanation;
        _status.Text = string.Empty;
        _status.Visibility = Visibility.Collapsed;
        _recovery.Children.Clear();
        _recovery.Visibility = Visibility.Collapsed;
        _suggestions.Children.Clear();

        if (hasReplacement)
        {
            // The chip applies immediately; the label stays human-readable for
            // punctuation inserts, where there is no "before" word to swap.
            var chip = CreateSuggestionChip(CorrectionPresentation.FormatChipLabel(displayIssue));
            chip.IsEnabled = true;
            chip.Click += (_, _) =>
            {
                chip.IsEnabled = false;
                _status.Text = "Применение…";
                _status.Visibility = Visibility.Visible;
                ApplyRequested?.Invoke(this, displayIssue);
            };
            _suggestions.Children.Add(chip);
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

        KeepOutOfFocusChain();
        ScheduleMeasuredReposition();
    }

    /// <summary>
    /// Draws both sides of the change, emphasising only the characters that differ.
    /// </summary>
    /// <remarks>
    /// <para>The runs are decoration over strings that are already complete. Each side is
    /// split by <see cref="CorrectionDiff"/> purely to decide which characters get the
    /// stronger brush, and the fragments are re-appended in order, so the line the user reads
    /// is character for character the display string it was given. Nothing here is ever read
    /// back — the correction that gets applied is the <see cref="TextIssue"/> held in
    /// <c>_issue</c>, which the diff never touches.</para>
    ///
    /// <para>Diffing the display strings rather than the raw ones matters: the card may mark
    /// an invisible space, and a boundary computed against the unmarked text would land in
    /// the wrong place on the marked text.</para>
    /// </remarks>
    private void RenderChange(string originalDisplay, string replacementDisplay)
    {
        var diff = CorrectionDiff.Compute(originalDisplay, replacementDisplay);
        Fill(_original, diff.Prefix, diff.ChangedOriginal, diff.Suffix, FaintBrush, ErrorBrush);
        Fill(_replacement, diff.Prefix, diff.ChangedReplacement, diff.Suffix, MutedBrush, AccentBrush);

        // Runs carry no automation name of their own, and a TextBlock filled through
        // Inlines reports an empty one — so a screen reader (and the GUI test harness)
        // would hear nothing at all where the correction is. State it explicitly.
        System.Windows.Automation.AutomationProperties.SetName(_original, originalDisplay);
        System.Windows.Automation.AutomationProperties.SetName(_replacement, replacementDisplay);
    }

    private static void Fill(
        TextBlock target,
        string prefix,
        string changed,
        string suffix,
        MediaBrush sharedBrush,
        MediaBrush changedBrush)
    {
        target.Inlines.Clear();
        if (prefix.Length > 0) target.Inlines.Add(new Run(prefix) { Foreground = sharedBrush });
        if (changed.Length > 0)
        {
            target.Inlines.Add(new Run(changed) { Foreground = changedBrush, FontWeight = FontWeights.SemiBold });
        }

        if (suffix.Length > 0) target.Inlines.Add(new Run(suffix) { Foreground = sharedBrush });
    }

    public void ShowIssue(TextIssue issue, Rect physicalScreenAnchor) => ShowForIssue(issue, physicalScreenAnchor);

    /// <summary>
    /// Marks the popup as a window that never takes the keyboard.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect this fixes.</b> <c>ShowActivated = false</c> keeps the popup from
    /// stealing focus when it appears, and that is all it does. Clicking a button inside it
    /// activates the window like any other — so the moment the user pressed «Исправить», the
    /// field they were editing lost keyboard focus. The write path then asked "does the
    /// target have keyboard focus?", was told no, and reported «Поле только для чтения» for a
    /// field the user had been typing into a second earlier. The write-time policy no longer
    /// asks that question, and this makes sure the situation stops arising at all: with
    /// <c>WS_EX_NOACTIVATE</c> the popup receives the click, the target keeps the caret, and
    /// the correction lands where the user is looking.</para>
    ///
    /// <para>Reapplied after every <c>Show</c> because WPF reasserts extended styles on a
    /// transparent window each time it becomes visible.</para>
    /// </remarks>
    private void KeepOutOfFocusChain()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var before = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        var after = before | WsExNoActivate;
        if (after != before)
        {
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(after));
            CompatibilityLogger.Technical("correction-popup-style", "noActivate=1");
        }
    }

    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    private static void SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, index, value);
        else SetWindowLong32(hwnd, index, value.ToInt32());
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

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

    /// <summary>Primary action: the accent surface with the near-black brand foreground.</summary>
    private static Button CreateSuggestionChip(string text)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = OnAccentBrush,
            Background = AccentBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
            // Keep mouse events on the chip (do not let transparent host steal MouseUp).
            Focusable = false,
            IsHitTestVisible = true
        };
        button.Template = RoundedTemplate(new CornerRadius(5), Brushes.Transparent);
        button.PreviewMouseLeftButtonDown += (_, e) => e.Handled = false;
        button.PreviewMouseLeftButtonUp += (_, e) => e.Handled = false;
        return button;
    }

    private static Button CreateGhostButton(string text, double width, double height, double fontSize)
    {
        var button = new Button
        {
            Content = text,
            Width = width,
            Height = height,
            FontSize = fontSize,
            Foreground = MutedBrush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2, 8, 2),
            Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Template = RoundedTemplate(new CornerRadius(5), ThemeResource.Brush("WlHover", Brushes.DimGray));
        return button;
    }

    /// <summary>
    /// Minimal rounded button template. Built in code because this window is created
    /// without XAML, but the shape and hover surface follow the same tokens as the
    /// styles in Themes/Buttons.xaml.
    /// </summary>
    private static ControlTemplate RoundedTemplate(CornerRadius radius, MediaBrush hoverBrush)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "border";
        border.SetValue(Border.CornerRadiusProperty, radius);
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, hoverBrush, "border"));
        template.Triggers.Add(hover);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "border"));
        template.Triggers.Add(disabled);

        return template;
    }
}
