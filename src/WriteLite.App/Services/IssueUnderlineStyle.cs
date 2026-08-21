using WriteLite.Models;
using Color = System.Windows.Media.Color;

namespace WriteLite.Services;

/// <summary>Typed underline style for a single inline issue decoration.</summary>
public sealed record IssueUnderlineStyle(
    Color Color,
    double Thickness,
    double WaveHeight,
    double Opacity);

/// <summary>
/// Central theme for WriteLite underlines. Independent of host-app spellcheck colours.
/// Tuned for soft visibility on both light and dark backgrounds.
/// </summary>
public static class IssueUnderlineTheme
{
    // One warm family, taken from the wavy marks on writelite-web.vercel.app.
    // Categories are told apart by warmth, not by hue jumps: a blue or green
    // underline would read as another product's spellchecker, and a rainbow of
    // error colours is exactly what the WriteLite visual language avoids.

    // Brand orange — orthography, the most common and most certain correction
    public static readonly Color Orthography = Color.FromArgb(0xFF, 0xEC, 0x6C, 0x08);
    // Amber — grammar
    public static readonly Color Grammar = Color.FromArgb(0xFF, 0xE0, 0xA6, 0x4B);
    // Warm sand — punctuation
    public static readonly Color Punctuation = Color.FromArgb(0xFF, 0xE8, 0x96, 0x45);
    // Muted ochre — style / readability, the quietest of the four
    public static readonly Color Style = Color.FromArgb(0xFF, 0xA4, 0x84, 0x47);

    public const double DefaultThickness = 1.25;
    public const double DefaultWaveHeight = 1.35;
    public const double DefaultOpacity = 0.88;
    public const double SuggestionOpacity = 0.62;
    public const double WaveStep = 3.25;

    /// <summary>
    /// When true, WriteLite keeps underlines thinner/softer to reduce visual clash
    /// with host-app spellcheck (cannot be disabled globally from WriteLite).
    /// </summary>
    public static bool MinimizeDuplication { get; set; }

    public static IssueUnderlineStyle ForCategory(IssueCategory category, IssueSeverity severity = IssueSeverity.Error)
    {
        var color = category switch
        {
            IssueCategory.Orthography => Orthography,
            IssueCategory.Grammar => Grammar,
            IssueCategory.Punctuation => Punctuation,
            IssueCategory.Style => Style,
            IssueCategory.Readability => Style,
            _ => Orthography
        };

        var opacity = severity switch
        {
            IssueSeverity.Suggestion => SuggestionOpacity,
            IssueSeverity.Warning => 0.78,
            _ => DefaultOpacity
        };

        var thickness = DefaultThickness;
        var wave = DefaultWaveHeight;
        if (MinimizeDuplication)
        {
            thickness = 1.0;
            wave = 1.1;
            opacity *= 0.85;
        }

        return new IssueUnderlineStyle(color, thickness, wave, opacity);
    }

    public static IssueUnderlineStyle ForIssue(TextIssue issue)
        => ForCategory(issue.Category, issue.Severity);
}
