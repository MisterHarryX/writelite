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
    public static readonly Color Orthography = WriteLiteDefaults.ParseColor(WriteLiteDefaults.UnderlineTheme.OrthographyColor);
    // Amber — grammar
    public static readonly Color Grammar = WriteLiteDefaults.ParseColor(WriteLiteDefaults.UnderlineTheme.GrammarColor);
    // Warm sand — punctuation
    public static readonly Color Punctuation = WriteLiteDefaults.ParseColor(WriteLiteDefaults.UnderlineTheme.PunctuationColor);
    // Muted ochre — style / readability, the quietest of the four
    public static readonly Color Style = WriteLiteDefaults.ParseColor(WriteLiteDefaults.UnderlineTheme.StyleColor);

    public static readonly double DefaultThickness = WriteLiteDefaults.UnderlineTheme.DefaultThickness;
    public static readonly double DefaultWaveHeight = WriteLiteDefaults.UnderlineTheme.DefaultWaveHeight;
    public static readonly double DefaultOpacity = WriteLiteDefaults.UnderlineTheme.DefaultOpacity;
    public static readonly double SuggestionOpacity = WriteLiteDefaults.UnderlineTheme.SuggestionOpacity;
    public static readonly double WaveStep = WriteLiteDefaults.UnderlineTheme.WaveStep;

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
            IssueSeverity.Warning => WriteLiteDefaults.UnderlineTheme.WarningOpacity,
            _ => DefaultOpacity
        };

        var thickness = DefaultThickness;
        var wave = DefaultWaveHeight;
        if (MinimizeDuplication)
        {
            thickness = WriteLiteDefaults.UnderlineTheme.MinimizeDuplicationThickness;
            wave = WriteLiteDefaults.UnderlineTheme.MinimizeDuplicationWaveHeight;
            opacity *= WriteLiteDefaults.UnderlineTheme.MinimizeDuplicationOpacityFactor;
        }

        return new IssueUnderlineStyle(color, thickness, wave, opacity);
    }

    public static IssueUnderlineStyle ForIssue(TextIssue issue)
        => ForCategory(issue.Category, issue.Severity);
}
