using WriteLite.Models;
using WriteLite.Services.Settings;

namespace WriteLite.Services.Personal;

/// <summary>
/// «Мой стиль» findings as an analyzer, for the fast lane.
/// </summary>
/// <remarks>
/// The orchestrated lane asks <see cref="PersonalStyleService"/> directly, beside its other
/// stylistic observations. The fast lane is a plain composite of analyzers, and both lanes
/// have to carry the same findings: the editor publishes each lane as a replacement of the
/// previous one, so a finding that only one lane produced would appear and vanish.
/// </remarks>
public sealed class PersonalStyleAnalyzer(PersonalStyleService style, Func<WriteLiteAppSettings> settings) : ITextAnalyzer
{
    public IReadOnlyList<TextIssue> Analyze(string text)
    {
        var current = settings();
        if (!current.CheckingEnabled || !current.EnableStyle) return [];

        try
        {
            return style.FindStyleIssues(text);
        }
        catch (Exception exception)
        {
            // The least important finding on the card must never cost the user the rest of it.
            CompatibilityLogger.Technical("personal-style-analysis-failed", exception);
            return [];
        }
    }

    public Task<IReadOnlyList<TextIssue>> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(Analyze(text));
}
