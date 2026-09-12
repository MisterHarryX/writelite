using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WriteLite.Interop;
using WriteLite.Language.Core;
using WriteLite.Models;
using WriteLite.Resources;
using WriteLite.Services;
using static WriteLite.Services.PopupChrome;
using Button = System.Windows.Controls.Button;
using ProgressBar = System.Windows.Controls.ProgressBar;
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
/// <para>Deliberately independent of the target app's typography and colours: it carries the
/// WriteLite surface so a correction looks the same in Notepad, Word and a browser field.
/// The hierarchy matches the correction cards elsewhere in the product — category, then
/// the change, then the rule, then the actions.</para>
///
/// <para><b>The window holds no presentation state of its own.</b> Every element below is
/// shown or hidden by <see cref="Render"/> from a single <see cref="CorrectionCardState"/>,
/// and every public method is a transition that produces one. Nothing toggles a control
/// individually, because that is what made the reported defect possible: a failure row was
/// laid over a finished result, a result over a check in flight, and the card ended up
/// asserting four incompatible things at once. If a combination is not expressible as a
/// phase, it cannot be drawn.</para>
///
/// <para><b>Ordering.</b> <see cref="Token"/> is monotonic and every transition bumps it.
/// An asynchronous caller captures the token it started with and passes it back; a render
/// carrying a superseded token is discarded rather than drawn, so a slow analysis cannot
/// repaint the card that belongs to newer text — §5.</para>
/// </remarks>
public sealed class CorrectionPopupWindow : Window
{
    private static readonly MediaBrush ErrorBrush = ThemeResource.Brush("WlDanger", Brushes.OrangeRed);
    private static readonly MediaBrush LineBrush = ThemeResource.Brush("WlLine", Brushes.DimGray);
    private static readonly FontFamily MonoFont = ThemeResource.Font("WlFontMono", "Cascadia Mono, Consolas");

    /// <summary>How this window identifies itself on the accessibility tree.</summary>
    public const string AutomationId = "WriteLiteCorrectionCard";

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
    private readonly TextBlock _arrow = new() { Text = "→", FontSize = 12, Foreground = FaintBrush, Margin = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _replacement = new() { FontSize = 13, FontFamily = MonoFont, Foreground = AccentBrush, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>The title of an informational card — there is no change to draw above it.</summary>
    private readonly TextBlock _headline = new()
    {
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 12, 0, 0)
    };

    private readonly TextBlock _explanation = new() { FontSize = 12.5, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), LineHeight = 19 };
    private readonly TextBlock _status = new()
    {
        FontSize = 12,
        Foreground = ErrorBrush,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 0)
    };

    private readonly WrapPanel _changeRow = new() { Margin = new Thickness(0, 12, 0, 0) };

    /// <summary>
    /// The progress state: a hairline indeterminate bar and a caption.
    /// </summary>
    /// <remarks>
    /// §7: a check in flight is not an action, so it is never drawn as a button. The bar is
    /// two pixels tall and the caption is secondary text — the card must not appear to be
    /// doing something dramatic while it re-reads one word.
    /// </remarks>
    private readonly StackPanel _progress = new() { Margin = new Thickness(0, 14, 0, 2) };
    private readonly TextBlock _progressLabel = new()
    {
        FontSize = 12,
        Foreground = MutedBrush,
        Margin = new Thickness(0, 8, 0, 0)
    };

    private readonly Button _primary;
    private readonly StackPanel _primaryHost = new() { Margin = new Thickness(0, 13, 0, 0) };

    /// <summary>What the user can still do when the correction could not be written.</summary>
    private readonly StackPanel _recovery = new()
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 8, 0, 0)
    };

    private readonly Button _copy;
    private readonly Button _retry;
    private readonly Button _dictionary;
    private readonly Button _ignore;
    private readonly DockPanel _footer = new() { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };

    private CorrectionCardState _state = CorrectionCardState.Hidden;
    private long _token;
    private Rect _pendingPhysicalAnchor;
    private bool _repositionPending;
    private System.Windows.Threading.DispatcherTimer? _recheckDeadline;

    public CorrectionPopupWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 360;
        MinHeight = 120;
        MaxHeight = 360;
        SizeToContent = SizeToContent.Height;
        FontFamily = ThemeResource.Font("WlFont", "Segoe UI Variable Text, Segoe UI");

        // A titleless, chromeless window is otherwise indistinguishable from the suggestions
        // panel to anything reading the desktop through accessibility — a screen reader, or
        // the live probe that verifies this card's phases against a real external field.
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, AutomationId);
        System.Windows.Automation.AutomationProperties.SetName(this, Strings.Corr_CardAutomationName);

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
        var close = CreateGhostButton("×", 22, 22, 15);
        close.ToolTip = Strings.Common_Close;
        close.Click += (_, _) => Dismiss();
        Grid.SetColumn(close, 1);
        header.Children.Add(categoryRow);
        header.Children.Add(close);

        // становиться → становится
        _changeRow.Children.Add(_original);
        _changeRow.Children.Add(_arrow);
        _changeRow.Children.Add(_replacement);

        _progress.Children.Add(new ProgressBar
        {
            Height = 2,
            IsIndeterminate = true,
            BorderThickness = new Thickness(0),
            Foreground = AccentBrush,
            Background = LineBrush
        });
        _progress.Children.Add(_progressLabel);

        _primary = CreatePrimaryAction();
        _primary.Click += (_, _) => RequestApply();
        _primaryHost.Children.Add(_primary);

        _copy = CreateGhostButton(Strings.EditorAi_Copy, double.NaN, 26, 12);
        _copy.Click += (_, _) => { if (_state.Issue is { } issue) CopyRequested?.Invoke(this, issue); };
        _retry = CreateGhostButton(Strings.Corr_Retry, double.NaN, 26, 12);
        _retry.Click += (_, _) => RequestApply();
        _recovery.Children.Add(_copy);
        _recovery.Children.Add(_retry);

        _ignore = CreateGhostButton(Strings.Common_Ignore, double.NaN, 26, 12);
        _ignore.Click += (_, _) => { if (_state.Issue is { } issue) IgnoreRequested?.Invoke(this, issue); };
        _dictionary = CreateGhostButton(Strings.Common_AddToDictionary, double.NaN, 26, 12);
        _dictionary.Click += (_, _) => { if (_state.Issue is { } issue) AddToDictionaryRequested?.Invoke(this, issue); };
        DockPanel.SetDock(_ignore, Dock.Right);
        _footer.Children.Add(_ignore);
        DockPanel.SetDock(_dictionary, Dock.Left);
        _footer.Children.Add(_dictionary);

        root.Children.Add(header);
        root.Children.Add(_changeRow);
        root.Children.Add(_headline);
        root.Children.Add(_explanation);
        root.Children.Add(_progress);
        root.Children.Add(_status);
        root.Children.Add(_recovery);
        root.Children.Add(_primaryHost);
        root.Children.Add(_footer);
        card.Child = root;
        Content = card;

        Render(CorrectionCardState.Hidden);
    }

    public event EventHandler<TextIssue>? ApplyRequested;
    public event EventHandler<TextIssue>? IgnoreRequested;
    public event EventHandler<TextIssue>? AddToDictionaryRequested;

    /// <summary>Raised when the user asks for the replacement on the clipboard instead.</summary>
    public event EventHandler<TextIssue>? CopyRequested;

    /// <summary>The finding the card currently belongs to, or null when it shows nothing.</summary>
    public TextIssue? CurrentIssue => _state.Issue;

    public CorrectionCardPhase Phase => _state.Phase;

    /// <summary>The ordering token of the transition currently on screen.</summary>
    public long Token => Interlocked.Read(ref _token);

    /// <summary>True while <paramref name="token"/> is still the newest transition.</summary>
    public bool IsCurrent(long token) => Token == token;

    /// <summary>
    /// Puts the card into its progress state and returns the token that owns the refresh.
    /// </summary>
    /// <remarks>
    /// §4: this is what a text change does. It runs synchronously, so the previous result's
    /// apply action, explanation and any failure row are off the card before the call
    /// returns — the user cannot press an action belonging to text that is gone. The caller
    /// then re-analyses and comes back through <see cref="ShowForIssue(TextIssue, Rect, long)"/>
    /// with this token, or closes the card with <see cref="Dismiss"/>.
    /// </remarks>
    public long BeginRecheck()
        => Dispatcher.InvokeIfNeeded(() =>
        {
            var token = Interlocked.Increment(ref _token);
            Render(_state.Recheck());
            ArmRecheckDeadline(token);
            return token;
        });

    /// <summary>How long a refresh may stay on screen before the card closes instead.</summary>
    /// <remarks>
    /// The guarantee that makes <see cref="CorrectionCardPhase.Checking"/> safe to enter. A
    /// refresh depends on an analysis pass arriving, and a pass can fail to arrive — the
    /// target stopped responding, the monitor lost the field, the user switched away
    /// mid-edit. Without a deadline the card would sit spinning over text it no longer
    /// describes, which is the same dead end as the «Обновите предложение» card it replaced,
    /// with a nicer animation. A card that closes is always recoverable: the underline is
    /// still there to click.
    /// </remarks>
    public static readonly TimeSpan RecheckDeadline = TimeSpan.FromMilliseconds(1200);

    private void ArmRecheckDeadline(long token)
    {
        _recheckDeadline?.Stop();
        _recheckDeadline = new System.Windows.Threading.DispatcherTimer(
            RecheckDeadline,
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                _recheckDeadline?.Stop();
                if (!IsCurrent(token) || _state.Phase != CorrectionCardPhase.Checking) return;

                CompatibilityLogger.Technical("correction-card-recheck-timeout", $"token={token}");
                Dismiss();
            },
            Dispatcher);
    }

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
    ///
    /// <para>Reaching this method replaces the card rather than annotating it: the phase
    /// becomes <see cref="CorrectionCardPhase.Failed"/>, which has no primary action. A
    /// success message is not a failure and closes the card instead — a non-error string
    /// here used to leave an accent status line under a live apply button, which reads as a
    /// correction that is both done and still pending.</para>
    /// </remarks>
    public void ShowApplyFeedback(string message, bool isError, bool offerCopy, bool offerRetry)
        => ShowApplyFeedback(message, isError, offerCopy, offerRetry, Token);

    /// <summary>
    /// As above, but only while <paramref name="token"/> is still the card on screen.
    /// </summary>
    /// <remarks>
    /// A write outcome arrives after the write, by which time the text may have changed and
    /// the card may already be refreshing for it. Reporting the old attempt then would put a
    /// failure about text the user has left back onto a card about text they are in.
    /// </remarks>
    public void ShowApplyFeedback(string message, bool isError, bool offerCopy, bool offerRetry, long token)
    {
        if (Dispatcher.InvokeIfNeeded(() => ShowApplyFeedback(message, isError, offerCopy, offerRetry, token))) return;

        if (_state.Issue is not { } issue) return;
        if (!IsCurrent(token))
        {
            CompatibilityLogger.Technical("correction-card-feedback-discarded", $"token={token} current={Token}");
            return;
        }

        Interlocked.Increment(ref _token);
        if (!isError)
        {
            // Nothing is pending and nothing failed; there is no card left to show.
            Render(CorrectionCardState.Hidden);
            return;
        }

        Render(CorrectionCardState.Failed(issue, message, offerCopy, offerRetry));
    }

    public void ShowForIssue(TextIssue issue, Rect physicalScreenAnchor)
        => ShowForIssue(issue, physicalScreenAnchor, Interlocked.Increment(ref _token));

    /// <summary>
    /// Draws the finished analysis for <paramref name="issue"/>, if this render is still the
    /// newest one.
    /// </summary>
    /// <remarks>
    /// §5: results arrive out of order. Four selections in a row can finish C, A, D, B, and
    /// only D describes the text in front of the user. A render whose token has been
    /// superseded is dropped here, at the one place that draws, rather than being defended
    /// against separately at each call site.
    /// </remarks>
    public void ShowForIssue(TextIssue issue, Rect physicalScreenAnchor, long token)
    {
        if (Dispatcher.InvokeIfNeeded(() => ShowForIssue(issue, physicalScreenAnchor, token))) return;

        if (!IsCurrent(token))
        {
            CompatibilityLogger.Technical("correction-card-render-discarded", $"token={token} current={Token}");
            return;
        }

        _pendingPhysicalAnchor = physicalScreenAnchor;
        Render(CorrectionCardState.ForIssue(issue));
    }

    public void ShowIssue(TextIssue issue, Rect physicalScreenAnchor) => ShowForIssue(issue, physicalScreenAnchor);

    /// <summary>Closes the card and voids every render still in flight for it.</summary>
    public void Dismiss()
    {
        if (Dispatcher.InvokeIfNeeded(Dismiss)) return;

        Interlocked.Increment(ref _token);
        Render(CorrectionCardState.Hidden);
    }

    /// <summary>
    /// The one place any part of this card becomes visible or invisible.
    /// </summary>
    private void Render(CorrectionCardState state)
    {
        _state = state;
        if (state.Phase != CorrectionCardPhase.Checking) _recheckDeadline?.Stop();

        if (!state.IsVisible)
        {
            if (IsVisible) Hide();
            return;
        }

        var categoryBrush = CorrectionCardText.CategoryBrushes(state.Category).Foreground;
        _categoryMark.Background = categoryBrush;
        _category.Foreground = categoryBrush;
        Controls.Type.SetTracked(_category, state.CategoryLabel);

        if (state.ShowChange)
        {
            RenderChange(state.OriginalDisplay, state.ReplacementDisplay);
        }

        _changeRow.Visibility = Show(state.ShowChange);
        _headline.Text = state.Headline;
        _headline.Foreground = categoryBrush;
        _headline.Visibility = Show(state.ShowHeadline);
        _explanation.Text = state.Explanation;
        _explanation.Visibility = Show(state.ShowExplanation);
        _progressLabel.Text = state.ProgressLabel;
        _progress.Visibility = Show(state.ShowProgress);
        _status.Text = state.StatusMessage;
        _status.Visibility = Show(state.ShowStatus);
        _copy.Visibility = Show(state.ShowCopy);
        _retry.Visibility = Show(state.ShowRetry);
        _recovery.Visibility = Show(state.ShowCopy || state.ShowRetry);
        _primary.Content = state.PrimaryActionLabel;
        _primaryHost.Visibility = Show(state.ShowPrimaryAction);
        _dictionary.Visibility = Show(state.ShowDictionary);
        _ignore.Visibility = Show(state.ShowIgnore);
        _footer.Visibility = Show(state.ShowDictionary || state.ShowIgnore);

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

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Hands the correction to the apply path and puts the card into its write-in-flight
    /// phase, which has no clickable primary action to press twice.
    /// </summary>
    private void RequestApply()
    {
        if (_state.Issue is not { } issue) return;
        if (_state.Phase is not (CorrectionCardPhase.Result or CorrectionCardPhase.Failed)) return;

        Interlocked.Increment(ref _token);
        Render(CorrectionCardState.Applying(issue));
        ApplyRequested?.Invoke(this, issue);
    }

    /// <summary>
    /// Draws both sides of the change, emphasising only the characters that differ.
    /// </summary>
    /// <remarks>
    /// <para>The runs are decoration over strings that are already complete. Each side is
    /// split by <see cref="CorrectionDiff"/> purely to decide which characters get the
    /// stronger brush, and the fragments are re-appended in order, so the line the user reads
    /// is character for character the display string it was given. Nothing here is ever read
    /// back — the correction that gets applied is the <see cref="TextIssue"/> held in the
    /// card's state, which the diff never touches.</para>
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

        var before = User32.GetWindowLongPtr(handle, User32.GwlExStyle);
        var after = before | User32.WsExNoActivate;
        if (after != before)
        {
            User32.SetWindowLongPtr(handle, User32.GwlExStyle, after);
            CompatibilityLogger.Technical("correction-popup-style", "noActivate=1");
        }
    }

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
        var workArea = PopupWorkArea.GetCurrent(physicalScreenAnchor, dpi.DpiScaleX, dpi.DpiScaleY);
        var placement = CalculatePlacement(anchor, workArea, popupSize);
        Left = placement.Location.X;
        Top = placement.Location.Y;
    }

    /// <summary>Primary action: the accent surface with the near-black brand foreground.</summary>
    private static Button CreatePrimaryAction()
    {
        var button = new Button
        {
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = OnAccentBrush,
            Background = AccentBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
            // Keep mouse events on the button (do not let transparent host steal MouseUp).
            Focusable = false,
            IsHitTestVisible = true
        };
        button.Template = RoundedButtonTemplate(new CornerRadius(5), Brushes.Transparent);
        button.PreviewMouseLeftButtonDown += (_, e) => e.Handled = false;
        button.PreviewMouseLeftButtonUp += (_, e) => e.Handled = false;
        return button;
    }
}
