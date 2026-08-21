using WriteLite.Models;

namespace WriteLite.Services;

/// <summary>One mutually-exclusive count projection for badge tabs and cards.</summary>
public readonly record struct IssueCountSummary(
    int All,
    int Orthography,
    int Punctuation,
    int Grammar,
    int Style)
{
    public int CategoryTotal => Orthography + Punctuation + Grammar + Style;

    public static IssueCountSummary From(IEnumerable<TextIssue> issues)
    {
        var all = 0;
        var orthography = 0;
        var punctuation = 0;
        var grammar = 0;
        var style = 0;

        foreach (var issue in issues)
        {
            all++;
            switch (issue.Category)
            {
                case IssueCategory.Orthography: orthography++; break;
                case IssueCategory.Punctuation: punctuation++; break;
                case IssueCategory.Grammar: grammar++; break;
                case IssueCategory.Style:
                case IssueCategory.Readability: style++; break;
            }
        }

        return new IssueCountSummary(all, orthography, punctuation, grammar, style);
    }
}
