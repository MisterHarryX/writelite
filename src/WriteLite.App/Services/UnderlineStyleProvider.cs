using System.Windows.Media;
using WriteLite.Models;
using Color = System.Windows.Media.Color;

namespace WriteLite.Services;

public static class UnderlineStyleProvider
{
    public static Color GetUnderlineColor(TextIssue issue)
        => IssueUnderlineTheme.ForIssue(issue).Color;

    public static double GetOpacity(TextIssue issue)
        => IssueUnderlineTheme.ForIssue(issue).Opacity;

    public static double GetThickness(TextIssue issue)
        => IssueUnderlineTheme.ForIssue(issue).Thickness;

    public static IssueUnderlineStyle GetStyle(TextIssue issue)
        => IssueUnderlineTheme.ForIssue(issue);

    public static bool UseDashedLine(TextIssue issue)
        => issue.Severity == IssueSeverity.Suggestion || !issue.CanApplyAutomatically;
}
