namespace WriteLite.Models;

public enum AnalysisIndicatorState
{
    Hidden,
    ActiveIdle,
    FastAnalyzing,
    DeepAnalyzing,
    NoErrors,
    HasErrors,
    BackendUnavailable,
    // Backward-compatible names used by existing views and diagnostics.
    Typing,
    Analyzing,
    IssuesFound,
    NoIssuesFullCheck,
    NoIssuesLimitedCheck,
    ReadOnlyAnalysis,
    Error
}
