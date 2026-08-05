using WriteLite.Models;

namespace WriteLite.Services;

public static class AnalysisIndicatorResolver
{
    public static AnalysisIndicatorState Resolve(
        string text,
        IReadOnlyList<TextIssue> issues,
        bool supportsDirectWrite,
        bool isFullDictionaryLoaded,
        bool hasError = false)
    {
        if (hasError) return AnalysisIndicatorState.BackendUnavailable;

        if (string.IsNullOrWhiteSpace(text)) return AnalysisIndicatorState.Hidden;

        if (!supportsDirectWrite)
        {
            return issues.Count > 0
                ? AnalysisIndicatorState.ReadOnlyAnalysis
                : AnalysisIndicatorState.ReadOnlyAnalysis;
        }

        if (issues.Count > 0) return AnalysisIndicatorState.HasErrors;

        return isFullDictionaryLoaded
            ? AnalysisIndicatorState.NoErrors
            : AnalysisIndicatorState.NoIssuesLimitedCheck;
    }

    public static string? ResolveTooltip(AnalysisIndicatorState state)
    {
        return state switch
        {
            AnalysisIndicatorState.NoIssuesLimitedCheck =>
                "Явных ошибок не найдено. Полный словарь ещё не подключён.",
            AnalysisIndicatorState.ReadOnlyAnalysis =>
                "Поле доступно только для анализа.",
            AnalysisIndicatorState.NoIssuesFullCheck =>
                "Ошибок не найдено.",
            AnalysisIndicatorState.IssuesFound =>
                null,
            AnalysisIndicatorState.BackendUnavailable =>
                "Локальный ИИ недоступен. Используется быстрая проверка.",
            AnalysisIndicatorState.Analyzing =>
                "Проверка текста…",
            AnalysisIndicatorState.Typing =>
                "Ожидание ввода…",
            AnalysisIndicatorState.Error =>
                "Ошибка проверки.",
            _ => null
        };
    }
}
