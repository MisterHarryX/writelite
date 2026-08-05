using WriteLite.Models;

namespace WriteLite.Services;

public interface ITextAnalyzer
{
    IReadOnlyList<TextIssue> Analyze(string text);

    Task<IReadOnlyList<TextIssue>> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Analyze(text));
    }
}
