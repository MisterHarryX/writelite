using WriteLite.Models;

namespace WriteLite.Services;

public sealed class CompositeTextAnalyzer(params ITextAnalyzer[] analyzers) : ITextAnalyzer
{
    public IReadOnlyList<TextIssue> Analyze(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var issues = new List<TextIssue>();
        foreach (var analyzer in analyzers)
        {
            issues.AddRange(analyzer.Analyze(text));
        }

        return SortIssues(issues);
    }

    public async Task<IReadOnlyList<TextIssue>> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var issues = new List<TextIssue>();
        foreach (var analyzer in analyzers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            issues.AddRange(await analyzer.AnalyzeAsync(text, cancellationToken));
        }

        return SortIssues(issues);
    }

    private static IReadOnlyList<TextIssue> SortIssues(IEnumerable<TextIssue> issues)
    {
        return issues
            .OrderBy(issue => issue.Start)
            .ThenByDescending(issue => issue.Severity)
            .ToList();
    }
}
