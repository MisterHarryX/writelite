using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
// UseWindowsForms is on for the tray icon, so these names are ambiguous project-wide.
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace WriteLite.Services;

/// <summary>
/// The one place motion is defined.
/// </summary>
/// <remarks>
/// The website animates on <c>cubic-bezier(0.22, 1, 0.36, 1)</c>: a fast start that
/// settles without overshoot. Everything here uses that curve so the desktop moves
/// the way the page does, and every duration stays inside the 90–220 ms band where
/// a transition reads as feedback rather than as waiting.
///
/// Three rules the rest of the app relies on:
///
/// <list type="bullet">
/// <item>Only <c>Opacity</c> and <c>RenderTransform</c> are animated. Both are
/// composited, so no animation in the product triggers a layout or measure pass —
/// which matters because this app has a UI-responsiveness watchdog.</item>
/// <item><see cref="IsEnabled"/> is checked by every entry point. When Windows
/// reports that animation is off, transitions do not run at reduced speed: they
/// are skipped and the final value is set directly.</item>
/// <item>Nothing loops. The equalizer in the player is the single exception and it
/// stops the moment playback pauses.</item>
/// </list>
/// </remarks>
public static class Motion
{
    // ── Duration tokens ──────────────────────────────────────────────────────
    // Mirrors Themes/Animations.xaml. Templates use the XAML tokens; code uses
    // these. Keep the two in step.

    /// <summary>Press feedback. Short enough to feel like contact, not a transition.</summary>
    public static readonly Duration Press = WriteLiteDefaults.Motion.Press;

    /// <summary>Release from press. Deliberately slower than the press itself.</summary>
    public static readonly Duration Release = WriteLiteDefaults.Motion.Release;

    /// <summary>Hover and focus colour changes.</summary>
    public static readonly Duration Hover = WriteLiteDefaults.Motion.Hover;

    /// <summary>Popovers, dropdowns and tooltips.</summary>
    public static readonly Duration Popover = WriteLiteDefaults.Motion.Popover;

    /// <summary>Content reveals, page changes and the navigation indicator.</summary>
    public static readonly Duration Micro = WriteLiteDefaults.Motion.Micro;

    /// <summary>Gap between staggered siblings. Long enough to read as a cascade.</summary>
    public static readonly TimeSpan StaggerStep = WriteLiteDefaults.Motion.StaggerStep;

    /// <summary>Distance a revealing element rises through. Small on purpose.</summary>
    public static readonly double RevealOffset = WriteLiteDefaults.Motion.RevealOffset;

    /// <summary>
    /// The website's editorial curve. Frozen and shared: one instance serves every
    /// animation in the product.
    /// </summary>
    public static IEasingFunction Editorial { get; } = CreateEditorialEase();

    private static IEasingFunction CreateEditorialEase()
    {
        var ease = new EditorialEase();
        ease.Freeze();
        return ease;
    }

    /// <summary>
    /// False when Windows is set to minimise animation.
    /// </summary>
    /// <remarks>
    /// <c>SystemParameters.ClientAreaAnimation</c> is the managed face of
    /// <c>SPI_GETCLIENTAREAANIMATION</c>, which is what Windows 11's "Animation
    /// effects" switch and the accessibility "Show animations" setting both write.
    /// Read once: it is a per-session preference and re-reading it per animation
    /// would put a P/Invoke on the press path.
    /// </remarks>
    public static bool IsEnabled { get; } = ReadSystemPreference();

    private static bool ReadSystemPreference()
    {
        try
        {
            return SystemParameters.ClientAreaAnimation;
        }
        catch
        {
            // Non-interactive session (the render smoke tests run headless).
            return false;
        }
    }

    // ── Press feedback ───────────────────────────────────────────────────────

    /// <summary>
    /// Gives a control the 0.985 dip on press.
    /// </summary>
    /// <remarks>
    /// Set through a style setter rather than copied into each control template.
    /// The previous system repeated the same two storyboards inside every button
    /// template, which is why only two of the eight button styles had press
    /// feedback at all — the other six were simply never given a copy.
    ///
    /// The scale is applied to the control rather than to a named element inside
    /// its template, so it works for any template without one being written for it.
    /// </remarks>
    public static readonly DependencyProperty PressProperty =
        DependencyProperty.RegisterAttached(
            "Press", typeof(bool), typeof(Motion),
            new PropertyMetadata(false, OnPressChanged));

    public static void SetPress(DependencyObject element, bool value) => element.SetValue(PressProperty, value);

    public static bool GetPress(DependencyObject element) => (bool)element.GetValue(PressProperty);

    /// <summary>How far a pressed control dips. 0.985 at 32 px reads as contact, not as a bounce.</summary>
    public static readonly DependencyProperty PressScaleProperty =
        DependencyProperty.RegisterAttached(
            "PressScale", typeof(double), typeof(Motion),
            new PropertyMetadata(WriteLiteDefaults.Motion.PressScale));

    public static void SetPressScale(DependencyObject element, double value) => element.SetValue(PressScaleProperty, value);

    public static double GetPressScale(DependencyObject element) => (double)element.GetValue(PressScaleProperty);

    private static void OnPressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.PreviewMouseLeftButtonDown -= OnPressDown;
        element.PreviewMouseLeftButtonUp -= OnPressUp;
        element.MouseLeave -= OnPressUp;
        element.LostMouseCapture -= OnPressUp;
        element.KeyDown -= OnPressKeyDown;
        element.KeyUp -= OnPressKeyUp;

        if (e.NewValue is not true)
        {
            return;
        }

        // Instance events rather than a DependencyPropertyDescriptor on IsPressed:
        // descriptor subscriptions root the element for the lifetime of the
        // property, and the correction popovers create and destroy their controls
        // continuously while the user types in another application.
        element.PreviewMouseLeftButtonDown += OnPressDown;
        element.PreviewMouseLeftButtonUp += OnPressUp;
        element.MouseLeave += OnPressUp;
        element.LostMouseCapture += OnPressUp;
        element.KeyDown += OnPressKeyDown;
        element.KeyUp += OnPressKeyUp;
    }

    private static void OnPressDown(object sender, MouseButtonEventArgs e) => Depress((FrameworkElement)sender, true);

    private static void OnPressUp(object sender, EventArgs e) => Depress((FrameworkElement)sender, false);

    // Keyboard activation gets the same feedback, otherwise a control operated from
    // the keyboard looks like it did not register the key.
    private static void OnPressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            Depress((FrameworkElement)sender, true);
        }
    }

    private static void OnPressKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            Depress((FrameworkElement)sender, false);
        }
    }

    private static void Depress(FrameworkElement element, bool pressed)
    {
        var scale = EnsureScaleTransform(element);
        var target = pressed ? GetPressScale(element) : 1.0;

        if (!IsEnabled)
        {
            scale.ScaleX = target;
            scale.ScaleY = target;
            return;
        }

        // Press is faster than release: the user is waiting on the press and is
        // already looking elsewhere by the release.
        var animation = new DoubleAnimation(target, pressed ? Press : Release)
        {
            EasingFunction = Editorial,
            FillBehavior = FillBehavior.HoldEnd
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    /// <summary>
    /// Returns the element's scale transform, adding one without discarding a
    /// transform the template already set.
    /// </summary>
    private static ScaleTransform EnsureScaleTransform(FrameworkElement element)
    {
        element.RenderTransformOrigin = new Point(0.5, 0.5);

        switch (element.RenderTransform)
        {
            case ScaleTransform existing when !existing.IsFrozen:
                return existing;

            case TransformGroup group:
            {
                foreach (var child in group.Children)
                {
                    if (child is ScaleTransform found && !found.IsFrozen)
                    {
                        return found;
                    }
                }

                var added = new ScaleTransform(1, 1);
                group.Children.Add(added);
                return added;
            }

            case { } other when other != Transform.Identity:
            {
                var scale = new ScaleTransform(1, 1);
                var group = new TransformGroup();
                group.Children.Add(other);
                group.Children.Add(scale);
                element.RenderTransform = group;
                return scale;
            }

            default:
            {
                var scale = new ScaleTransform(1, 1);
                element.RenderTransform = scale;
                return scale;
            }
        }
    }

    // ── Settled state ────────────────────────────────────────────────────────

    /// <summary>
    /// Opts a control into <see cref="IsSettledProperty"/>.
    /// </summary>
    /// <remarks>
    /// A control that is already selected when its page opens has not changed state —
    /// it simply is that way — so animating it is wrong twice over. Visually, opening
    /// Settings would replay twenty toggle switches at once, and the word manager
    /// would draw its tab underline as though the user had just clicked it. And
    /// mechanically, a storyboard that starts during initialization is still running
    /// when the page is torn down, which is how a state animation ends up throwing on
    /// a dispatcher that is going away.
    ///
    /// Templates therefore pair a plain trigger carrying the resting value with a
    /// <c>MultiTrigger</c> that also requires <c>Motion.IsSettled</c>, and put the
    /// storyboard on the second one. Animations use <c>FillBehavior="Stop"</c> so that
    /// when one finishes the property hands back to the trigger's setter rather than
    /// holding a value the template no longer controls.
    /// </remarks>
    public static readonly DependencyProperty SettleProperty =
        DependencyProperty.RegisterAttached(
            "Settle", typeof(bool), typeof(Motion),
            new PropertyMetadata(false, OnSettleChanged));

    public static void SetSettle(DependencyObject element, bool value) => element.SetValue(SettleProperty, value);

    public static bool GetSettle(DependencyObject element) => (bool)element.GetValue(SettleProperty);

    /// <summary>False until the control has been through its first layout pass.</summary>
    public static readonly DependencyProperty IsSettledProperty =
        DependencyProperty.RegisterAttached(
            "IsSettled", typeof(bool), typeof(Motion),
            new PropertyMetadata(false));

    public static void SetIsSettled(DependencyObject element, bool value) => element.SetValue(IsSettledProperty, value);

    public static bool GetIsSettled(DependencyObject element) => (bool)element.GetValue(IsSettledProperty);

    private static void OnSettleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnSettleLoaded;
        element.Unloaded -= OnSettleUnloaded;

        if (e.NewValue is not true)
        {
            return;
        }

        element.Loaded += OnSettleLoaded;
        element.Unloaded += OnSettleUnloaded;

        if (element.IsLoaded)
        {
            OnSettleLoaded(element, new RoutedEventArgs());
        }
    }

    private static void OnSettleLoaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;

        // One dispatcher hop past Loaded. Bindings and the page's own Bind* calls
        // land during and just after loading, and every value they set counts as
        // initial state rather than as something the user did.
        element.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => SetIsSettled(element, true)));
    }

    // Reset on unload so a page that is navigated away from and back to does not
    // animate its restored state on the way in.
    private static void OnSettleUnloaded(object sender, RoutedEventArgs e) =>
        SetIsSettled((FrameworkElement)sender, false);

    // ── Reveal ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Fades an element in with a short rise.
    /// </summary>
    /// <param name="element">The element to reveal. Made visible if it is not already.</param>
    /// <param name="delay">Stagger offset for siblings revealed together.</param>
    /// <param name="offset">Rise distance; pass 0 for a pure fade.</param>
    public static void Reveal(FrameworkElement element, TimeSpan delay = default, double? offset = null)
    {
        element.Visibility = Visibility.Visible;
        element.Opacity = 1;

        if (!IsEnabled)
        {
            element.RenderTransform = Transform.Identity;
            return;
        }

        // The element's own values stay at the finished state and the animation
        // supplies the starting point through From. Parking the element at opacity 0
        // and animating up would mean that anything which stops the clock — a
        // rendering surface that never appears, an element detached mid-flight —
        // leaves the content permanently invisible. This way the worst case is that
        // the reveal is not seen, never that the article is not.
        var riseOffset = offset ?? RevealOffset;
        var translate = new TranslateTransform(0, 0);
        element.RenderTransform = translate;

        var fade = new DoubleAnimation(0, 1, Micro)
        {
            BeginTime = delay,
            EasingFunction = Editorial
        };
        var rise = new DoubleAnimation(riseOffset, 0, Micro)
        {
            BeginTime = delay,
            EasingFunction = Editorial
        };

        element.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    /// <summary>
    /// Reveals a sequence of elements as a cascade, skipping collapsed ones.
    /// </summary>
    /// <remarks>
    /// The stagger is capped: past about six steps the last element arrives late
    /// enough that the screen reads as slow rather than as considered.
    /// </remarks>
    public static void RevealSequence(IEnumerable<FrameworkElement> elements, int maxSteps = 6)
    {
        var step = 0;
        foreach (var element in elements)
        {
            if (element.Visibility != Visibility.Visible)
            {
                continue;
            }

            Reveal(element, TimeSpan.FromTicks(StaggerStep.Ticks * Math.Min(step, maxSteps)));
            step++;
        }
    }

    /// <summary>
    /// Fades an element out, then runs <paramref name="onDone"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="onDone"/> is <c>Completed</c> on the animation clock, and WPF
    /// raises that whenever the clock stops — including when a later
    /// <c>BeginAnimation</c> on the same property replaces it, or when the animation is
    /// removed with a null timeline. It therefore means "this fade is over", not "this
    /// fade reached zero".
    ///
    /// A caller that tears something down in the callback must be able to tell the two
    /// apart itself, by checking that the state it is about to act on is still the state
    /// it started the fade for. Assuming natural completion is how a cancelled fade ends
    /// up detaching an element that has just been brought back.
    /// </remarks>
    public static void FadeOut(FrameworkElement element, Action? onDone = null)
    {
        if (!IsEnabled)
        {
            element.Opacity = 0;
            onDone?.Invoke();
            return;
        }

        var fade = new DoubleAnimation(element.Opacity, 0, Popover) { EasingFunction = Editorial };
        if (onDone is not null)
        {
            fade.Completed += (_, _) => onDone();
        }

        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // ── Movement ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Slides a transform to a new offset — the navigation indicator moving between
    /// items, rather than disappearing from one and appearing at the next.
    /// </summary>
    public static void SlideTo(TranslateTransform transform, double y, Duration? duration = null)
    {
        if (!IsEnabled)
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = y;
            return;
        }

        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(y, duration ?? Micro) { EasingFunction = Editorial });
    }

    /// <summary>Animates a height, used only for the indicator matching item heights.</summary>
    public static void ResizeTo(FrameworkElement element, double height, Duration? duration = null)
    {
        if (!IsEnabled)
        {
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.Height = height;
            return;
        }

        element.BeginAnimation(
            FrameworkElement.HeightProperty,
            new DoubleAnimation(height, duration ?? Micro) { EasingFunction = Editorial });
    }

    // ── Page transition ──────────────────────────────────────────────────────

    /// <summary>
    /// Swaps the content of a host with a fade and a short rise.
    /// </summary>
    /// <remarks>
    /// There is no exit animation. The outgoing page is replaced immediately and
    /// only the incoming one animates: navigation is keyboard- and click-initiated
    /// and happens constantly, so waiting out a fade before the new page even
    /// starts would make the whole rail feel slower than the instant swap it
    /// replaces.
    /// </remarks>
    public static void TransitionContent(ContentControl host, object content)
    {
        host.Content = content;

        if (!IsEnabled || content is not FrameworkElement)
        {
            host.Opacity = 1;
            host.RenderTransform = Transform.Identity;
            return;
        }

        Reveal(host, offset: RevealOffset);
    }

    // ── Colour ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Transitions a brush's colour in place.
    /// </summary>
    /// <remarks>
    /// The brush must be a local, unfrozen <see cref="SolidColorBrush"/>. A template
    /// that writes <c>Background="{DynamicResource WlSurface}"</c> shares the theme's
    /// frozen brush, and animating it would throw — so templates that want an
    /// animated hover declare their own brush instance.
    /// </remarks>
    public static void ToColor(SolidColorBrush? brush, Color color, Duration? duration = null)
    {
        if (brush is null || brush.IsFrozen)
        {
            return;
        }

        if (!IsEnabled)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = color;
            return;
        }

        brush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            new ColorAnimation(color, duration ?? Hover) { EasingFunction = Editorial });
    }
}
