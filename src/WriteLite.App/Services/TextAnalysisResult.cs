using WriteLite.Models;

namespace WriteLite.Services;

public sealed record TextAnalysisResult(
    int RequestId,
    IReadOnlyList<TextIssue> Issues,
    TimeSpan Duration,
    bool IsStale);
