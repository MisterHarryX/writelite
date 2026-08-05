using WriteLite.Models;
using MediaColor = System.Windows.Media.Color;

namespace WriteLite.Services;

/// <summary>Presentation constants for the transparent overlay only; never applied to target text.</summary>
public static class InlineIssuePresentation
{
    public const double UnderlineThickness = IssueUnderlineTheme.DefaultThickness;

    public static MediaColor ColorFor(IssueCategory category)
        => IssueUnderlineTheme.ForCategory(category).Color;

    public static IssueUnderlineStyle StyleFor(TextIssue issue)
        => IssueUnderlineTheme.ForIssue(issue);

    public static IssueUnderlineStyle StyleFor(IssueCategory category)
        => IssueUnderlineTheme.ForCategory(category);
}
