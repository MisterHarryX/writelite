using System.Windows;
using System.Windows.Media.Animation;

namespace WriteLite.Services;

/// <summary>
/// The website's easing curve, <c>cubic-bezier(0.22, 1, 0.36, 1)</c>, as an
/// <see cref="IEasingFunction"/>.
/// </summary>
/// <remarks>
/// WPF ships no cubic-bezier easing. Its built-in curves are all noticeably weaker:
/// <c>CubicEase</c> with <c>EaseOut</c> is roughly
/// <c>cubic-bezier(0.215, 0.61, 0.355, 1)</c>, which starts slower and lacks the
/// snap that makes the site's transitions feel intentional. <see cref="KeySpline"/>
/// describes the exact curve, so this adapts one into an easing function.
///
/// It exists as a public class rather than as a resource in <c>Animations.xaml</c>
/// because an animation inside a control template cannot reference a resource in a
/// sibling merged dictionary: <c>StaticResource</c> resolves during development and
/// throws the first time the control is actually shown, and <c>DynamicResource</c>
/// is not available on a <see cref="Freezable"/>. Each dictionary declares its own
/// <c>&lt;wl:EditorialEase x:Key="WlEase" /&gt;</c> instead — one line, no
/// cross-dictionary reference, and the curve itself is defined once, here.
/// </remarks>
public sealed class EditorialEase : EasingFunctionBase
{
    private static readonly KeySpline Curve = new(0.22, 1.0, 0.36, 1.0);

    public EditorialEase()
    {
        // The curve already describes an ease-out. Left on the default EaseOut mode,
        // EasingFunctionBase would apply its own 1 - core(1 - t) transform on top and
        // mirror it into an ease-in — the one shape UI motion must never have, since
        // it delays movement at exactly the moment the user is watching for it.
        EasingMode = EasingMode.EaseIn;
    }

    protected override double EaseInCore(double normalizedTime) => Curve.GetSplineProgress(normalizedTime);

    protected override Freezable CreateInstanceCore() => new EditorialEase();
}
