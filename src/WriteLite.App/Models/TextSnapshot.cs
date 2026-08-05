using WriteLite.Services;

namespace WriteLite.Models;

public sealed record TextSnapshot(
    AutomationTextTarget Target,
    string Text,
    IReadOnlyList<TextIssue> Issues,
    DateTimeOffset AnalyzedAt,
    int GenerationId = 0,
    int RequestId = 0,
    bool IsFullDictionaryLoaded = false,
    AnalysisIndicatorState IndicatorState = AnalysisIndicatorState.Hidden,
    int ApplicableIssueCount = 0,
    string? StatusMessage = null,
    /// <summary>Main overlay/indicator may be shown only after confirmed user editing.</summary>
    bool ShowMainUi = false,
    UiInteractionState InteractionState = UiInteractionState.NoTarget,
    long TextVersion = 0);
