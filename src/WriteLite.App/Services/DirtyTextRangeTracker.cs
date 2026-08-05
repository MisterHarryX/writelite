namespace WriteLite.Services;

using WriteLite.Models;

public readonly record struct DirtyTextRange(int Start, int RemovedLength, int InsertedLength)
{
    public int Delta => InsertedLength - RemovedLength;
    public int OldEnd => Start + RemovedLength;
    public int NewEnd => Start + InsertedLength;
}

public readonly record struct TextAnalysisRange(int Start, int Length)
{
    public int End => Start + Length;
}

public static class DirtyTextRangeTracker
{
    public static DirtyTextRange Calculate(string previous, string current)
    {
        previous ??= string.Empty;
        current ??= string.Empty;
        var prefix = 0;
        var common = Math.Min(previous.Length, current.Length);
        while (prefix < common && previous[prefix] == current[prefix]) prefix++;
        var previousSuffix = previous.Length;
        var currentSuffix = current.Length;
        while (previousSuffix > prefix && currentSuffix > prefix
               && previous[previousSuffix - 1] == current[currentSuffix - 1])
        {
            previousSuffix--;
            currentSuffix--;
        }
        return new DirtyTextRange(prefix, previousSuffix - prefix, currentSuffix - prefix);
    }

    public static TextAnalysisRange ExpandToSentence(string text, DirtyTextRange dirty, int maxLength = 2400)
    {
        if (string.IsNullOrEmpty(text)) return new TextAnalysisRange(0, 0);
        var pivot = Math.Clamp(dirty.Start, 0, text.Length);
        var start = pivot;
        while (start > 0 && !IsBoundary(text[start - 1])) start--;
        var end = Math.Max(pivot, Math.Clamp(dirty.NewEnd, 0, text.Length));
        while (end < text.Length && !IsBoundary(text[end])) end++;
        if (end < text.Length) end++;
        if (end - start > maxLength)
        {
            start = Math.Max(0, pivot - maxLength / 2);
            end = Math.Min(text.Length, start + maxLength);
            start = Math.Max(0, end - maxLength);
        }
        return new TextAnalysisRange(start, end - start);
    }

    private static bool IsBoundary(char value) => value is '.' or '!' or '?' or '\n' or '\r';
}

public readonly record struct DirtyAnalysisPlan(
    DirtyTextRange Dirty,
    TextAnalysisRange PreviousRange,
    TextAnalysisRange CurrentRange,
    string Segment);

public static class DirtyIssueAnalysis
{
    public static DirtyAnalysisPlan Plan(string previousText, string currentText, bool fullDocument = false)
    {
        var dirty = DirtyTextRangeTracker.Calculate(previousText, currentText);
        var currentRange = fullDocument
            ? new TextAnalysisRange(0, currentText.Length)
            : DirtyTextRangeTracker.ExpandToSentence(currentText, dirty);
        var previousRange = fullDocument
            ? new TextAnalysisRange(0, previousText.Length)
            : DirtyTextRangeTracker.ExpandToSentence(previousText,
                new DirtyTextRange(dirty.Start, dirty.RemovedLength, dirty.RemovedLength));
        return new DirtyAnalysisPlan(
            dirty,
            previousRange,
            currentRange,
            currentText.Substring(currentRange.Start, currentRange.Length));
    }

    public static IReadOnlyList<TextIssue> Merge(
        string currentText,
        IReadOnlyList<TextIssue> previousIssues,
        IReadOnlyList<TextIssue> segmentIssues,
        DirtyAnalysisPlan plan)
    {
        var retained = previousIssues
            .Where(issue => issue.Start + issue.Length <= plan.PreviousRange.Start
                            || issue.Start >= plan.PreviousRange.End)
            .Select(issue => issue.Start >= plan.PreviousRange.End
                ? issue with { Start = issue.Start + plan.Dirty.Delta }
                : issue)
            .Where(issue => TextCorrectionService.IsRangeValid(currentText, issue.Start, issue.Length))
            .Where(issue => issue.Length == 0 || string.Equals(
                currentText.Substring(issue.Start, issue.Length), issue.Original, StringComparison.Ordinal));
        var mapped = segmentIssues.Select(issue => issue with { Start = issue.Start + plan.CurrentRange.Start });
        return retained.Concat(mapped)
            .OrderBy(issue => issue.Start)
            .ThenBy(issue => issue.Length)
            .ToArray();
    }
}
