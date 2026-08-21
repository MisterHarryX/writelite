using System.Windows;
using System.Windows.Documents;
using TextBlock = System.Windows.Controls.TextBlock;
using MediaFontFamily = System.Windows.Media.FontFamily;

namespace WriteLite.Controls;

/// <summary>
/// Letter-spacing for <see cref="TextBlock"/>.
/// </summary>
/// <remarks>
/// WPF has no <c>LetterSpacing</c>, but the WriteLite brand voice depends on the
/// widely tracked uppercase mono eyebrows used across the website
/// (<c>tracking: 0.18–0.22em</c>). Setting <see cref="TrackedProperty"/> instead of
/// <see cref="TextBlock.Text"/> rebuilds the inline run list with a thin space
/// (U+2009 ≈ 0.2 em) between characters.
///
/// The spacer is pinned to a proportional family on purpose: in a monospaced font
/// every glyph — including the thin space — advances a full em, which would space
/// the label out roughly five times too far.
///
/// The untracked string is mirrored into <see cref="AutomationProperties.NameProperty"/>
/// so assistive technology reads the real word rather than the spaced glyphs.
/// </remarks>
public static class Type
{
    private const string ThinSpace = " ";

    /// <summary>Neutral proportional family for the spacer glyph only.</summary>
    private static readonly MediaFontFamily SpacerFont = new("Segoe UI");

    /// <summary>Source text to render with wide tracking.</summary>
    public static readonly DependencyProperty TrackedProperty =
        DependencyProperty.RegisterAttached(
            "Tracked",
            typeof(string),
            typeof(Type),
            new PropertyMetadata(null, OnTrackedChanged));

    public static void SetTracked(DependencyObject element, string? value) =>
        element.SetValue(TrackedProperty, value);

    public static string? GetTracked(DependencyObject element) =>
        (string?)element.GetValue(TrackedProperty);

    private static void OnTrackedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        var text = e.NewValue as string;
        block.Inlines.Clear();

        if (string.IsNullOrEmpty(text))
        {
            block.SetCurrentValue(System.Windows.Automation.AutomationProperties.NameProperty, string.Empty);
            return;
        }

        // Enumerate by text element so surrogate pairs and combining marks stay whole.
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        var first = true;
        while (enumerator.MoveNext())
        {
            if (!first)
            {
                block.Inlines.Add(new Run(ThinSpace) { FontFamily = SpacerFont });
            }

            block.Inlines.Add(new Run((string)enumerator.Current));
            first = false;
        }

        block.SetCurrentValue(System.Windows.Automation.AutomationProperties.NameProperty, text);
    }
}
