using WriteLite.Models;

namespace WriteLite.Services;

public sealed record CorrectionComposeResult(
    bool CanApply,
    string NewText,
    int AppliedCount,
    int SkippedCount,
    int RemainingRecommendations,
    IReadOnlyList<TextIssue> AppliedIssues);

public sealed record CorrectionApplyResult(
    bool Succeeded,
    bool WasBlocked,
    int AppliedCount,
    int SkippedCount,
    int RemainingRecommendations,
    int WriteOperations,
    string? UserMessage)
{
    public static CorrectionApplyResult Blocked { get; } = new(
        Succeeded: false,
        WasBlocked: true,
        AppliedCount: 0,
        SkippedCount: 0,
        RemainingRecommendations: 0,
        WriteOperations: 0,
        UserMessage: null);

    public static string FormatApplyAllButton(int applicableCount)
    {
        return applicableCount <= 0
            ? "Исправить все безопасные — 0"
            : $"Исправить все безопасные — {applicableCount}";
    }

    public static string FormatResultMessage(int appliedCount, int remainingRecommendations)
    {
        if (appliedCount <= 0) return string.Empty;

        return remainingRecommendations <= 0
            ? $"Исправлено {appliedCount}."
            : $"Исправлено {appliedCount}, осталось {remainingRecommendations} рекомендаций.";
    }
}
