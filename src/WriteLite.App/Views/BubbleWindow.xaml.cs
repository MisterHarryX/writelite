using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WriteLite.Models;
using WriteLite.Resources;
using WriteLite.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Size = System.Windows.Size;

namespace WriteLite.Views;

public partial class BubbleWindow : Window
{
    private Storyboard? _spinStoryboard;
    private bool _userHidden;
    private bool _showIndicator = true;
    private bool _showGreenWhenClean = true;
    private System.Windows.Point? _lastPlacement;
    private AnalysisIndicatorState _lastState = AnalysisIndicatorState.Hidden;
    /// <summary>
    /// True while the indicator is suppressed because no safe exterior slot
    /// exists. Latching it keeps the "hidden" line to one entry per transition
    /// instead of one per snapshot.
    /// </summary>
    private bool _placementSuppressed;

    public BubbleWindow()
    {
        InitializeComponent();
        AppIconLoader.ApplyTo(this);
        SetupSpinAnimation();
        CompatibilityLogger.Technical("indicator-created", "size=76x40");
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? ApplyAllRequested;
    public event EventHandler? OpenMainWindowRequested;
    public event EventHandler? HideIndicatorRequested;

    public void ApplyDisplaySettings(bool showIndicator, bool showGreenWhenClean)
    {
        _showIndicator = showIndicator;
        _showGreenWhenClean = showGreenWhenClean;
        if (!_showIndicator)
        {
            Hide();
        }
    }

    public void ShowSnapshot(TextSnapshot snapshot)
    {
        if (_userHidden || !_showIndicator)
        {
            Hide();
            return;
        }

        ApplyVisualState(snapshot);

        var dpi = VisualTreeHelper.GetDpi(this);
        var field = OverlayPlacementService.Scale(snapshot.Target.Bounds, dpi.DpiScaleX, dpi.DpiScaleY);
        var placement = OverlayPlacementService.PlaceIndicator(
            field,
            SystemParameters.WorkArea,
            new Size(Width, Height));

        // Placement is allowed only in a dedicated exterior slot.  A constrained
        // work area can force a clamped fallback into the editor; hiding the
        // accessory is preferable to covering text, a chart, or a host control.
        //
        // This decision is made BEFORE Show(). Showing first and hiding a few
        // lines later meant that on any field with no safe exterior slot every
        // single snapshot produced a visible flash followed by a hide, and each
        // hide notified the monitor, which resumed analysis, which produced the
        // next snapshot. One session logged 26 275 panel hides and rotated three
        // 2 MB log files; the window was flip-flopping every couple of
        // milliseconds on one unchanged generation.
        if (new Rect(placement.Location, placement.Size).IntersectsWith(field))
        {
            // Log only on the transition. Repeating the same line thousands of
            // times per second is what buried the real diagnostics.
            if (IsVisible || !_placementSuppressed)
            {
                CompatibilityLogger.Technical("indicator-hidden", "reason=no-safe-exterior-placement");
            }
            _placementSuppressed = true;
            Hide();
            return;
        }

        _placementSuppressed = false;
        CompatibilityLogger.Technical("indicator-reused", $"generation={snapshot.GenerationId}");

        if (!IsVisible)
        {
            Show();
        }

        // UIA bounds can jitter by a pixel while typing. Keep the one window in place
        // unless the target actually moved, avoiding visible indicator vibration.
        if (_lastPlacement is null
            || IndicatorPresentation.ShouldUpdatePosition(_lastPlacement.Value.X, _lastPlacement.Value.Y, placement.Location.X, placement.Location.Y))
        {
            Left = placement.Location.X;
            Top = placement.Location.Y;
            _lastPlacement = placement.Location;
            CompatibilityLogger.Technical(
                "indicator-position-updated",
                $"changed=1 fieldX={(int)Math.Round(field.X)} fieldY={(int)Math.Round(field.Y)} positionX={(int)Math.Round(placement.Location.X)} positionY={(int)Math.Round(placement.Location.Y)}");
        }
        else CompatibilityLogger.Technical("indicator-position-skipped", "reason=subpixel-jitter");
    }

    public void ClearUserHide() => _userHidden = false;

    public void SuppressUntilNextShow()
    {
        _userHidden = true;
        Hide();
    }

    private void ApplyVisualState(TextSnapshot snapshot)
    {
        var count = snapshot.Issues.Count;
        var state = snapshot.IndicatorState;
        if (state != _lastState)
        {
            CompatibilityLogger.Technical("indicator-state-changed", $"from={_lastState} to={state}");
            _lastState = state;
        }

        // Fallback when IndicatorState not populated by monitor.
        if (state is AnalysisIndicatorState.Hidden or AnalysisIndicatorState.Typing)
        {
            state = count > 0
                ? AnalysisIndicatorState.IssuesFound
                : snapshot.IsFullDictionaryLoaded
                    ? AnalysisIndicatorState.NoIssuesFullCheck
                    : AnalysisIndicatorState.NoIssuesLimitedCheck;
        }

        CountText.Visibility = Visibility.Collapsed;
        OkIcon.Visibility = Visibility.Collapsed;
        AnalyzingIcon.Visibility = Visibility.Collapsed;
        DictDot.Visibility = Visibility.Collapsed;
        StopSpin();

        CountButton.Padding = new Thickness(0);
        CountButton.BorderThickness = new Thickness(2);
        CountButton.BorderBrush = Brushes.Transparent;
        CountButton.Background = (Brush)FindResource("WlBrand");
        CountButton.Effect = null;

        switch (state)
        {
            case AnalysisIndicatorState.Analyzing:
            case AnalysisIndicatorState.FastAnalyzing:
            case AnalysisIndicatorState.DeepAnalyzing:
                AnalyzingIcon.Visibility = Visibility.Visible;
                CountButton.Background = Brushes.Transparent;
                CountButton.BorderBrush = (Brush)FindResource("WlBrand");
                CountButton.ToolTip = Strings.Bubble_Checking;
                StartSpin();
                break;

            case AnalysisIndicatorState.NoIssuesLimitedCheck:
                DictDot.Visibility = Visibility.Visible;
                CountButton.Background = Brushes.Transparent;
                CountButton.BorderBrush = (Brush)FindResource("WlBrand");
                // Dashed look approximated with thicker transparent fill + brand border
                CountButton.BorderThickness = new Thickness(2);
                CountButton.ToolTip = AnalysisIndicatorResolver.ResolveTooltip(state)
                    ?? Strings.Dict_LoadingTitle;
                break;

            case AnalysisIndicatorState.NoIssuesFullCheck:
            case AnalysisIndicatorState.NoErrors:
                OkIcon.Visibility = Visibility.Visible;
                CountButton.Background = (Brush)FindResource("WlSuccess");
                CountButton.ToolTip = Strings.Bubble_NoErrors;
                break;

            case AnalysisIndicatorState.ReadOnlyAnalysis:
            case AnalysisIndicatorState.Error:
            case AnalysisIndicatorState.BackendUnavailable:
            case AnalysisIndicatorState.IssuesFound:
            case AnalysisIndicatorState.HasErrors:
            default:
                if (count <= 0)
                {
                    if (!snapshot.IsFullDictionaryLoaded)
                    {
                        DictDot.Visibility = Visibility.Visible;
                        CountButton.Background = Brushes.Transparent;
                        CountButton.BorderBrush = (Brush)FindResource("WlBrand");
                        CountButton.ToolTip = Strings.Dict_LoadingTitle;
                    }
                    else
                    {
                        OkIcon.Visibility = Visibility.Visible;
                        CountButton.Background = (Brush)FindResource("WlSuccess");
                        CountButton.ToolTip = Strings.Bubble_NoErrors;
                    }
                }
                else
                {
                    CountText.Visibility = Visibility.Visible;
                    CountText.Text = IndicatorPresentation.BadgeText(count);
                    CountButton.Background = (Brush)FindResource("WlBrand");
                    CountButton.ToolTip = string.Format(Strings.Bubble_FoundCount, count);
                }

                if (state == AnalysisIndicatorState.ReadOnlyAnalysis)
                {
                    CountButton.ToolTip = AnalysisIndicatorResolver.ResolveTooltip(state)
                        ?? CountButton.ToolTip;
                }

                break;
        }
    }

    private void SetupSpinAnimation()
    {
        var animation = new DoubleAnimation(0, 360, WriteLiteDefaults.Motion.SpinnerRotationDuration)
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        _spinStoryboard = new Storyboard();
        Storyboard.SetTarget(animation, AnalyzingIcon);
        Storyboard.SetTargetProperty(animation, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
        _spinStoryboard.Children.Add(animation);
    }

    private void StartSpin() => _spinStoryboard?.Begin();

    private void StopSpin() => _spinStoryboard?.Stop();

    private void CountButton_Click(object sender, RoutedEventArgs e)
    {
        OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PenButton_Click(object sender, RoutedEventArgs e)
    {
        if (PenButton.ContextMenu is null) return;
        PenButton.ContextMenu.PlacementTarget = PenButton;
        PenButton.ContextMenu.IsOpen = true;
    }

    private void ApplyAllMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyAllRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenPanelMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenMainMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenMainWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void HideIndicatorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SuppressUntilNextShow();
        HideIndicatorRequested?.Invoke(this, EventArgs.Empty);
    }
}
