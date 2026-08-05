using System.Windows;
using WriteLite.Models;

namespace WriteLite.Services;

public readonly record struct InlineGeometryCacheKey(
    string TargetId,
    long TextVersion,
    string RuleId,
    IssueCategory Category,
    int Start,
    int Length,
    string Original,
    Rect HostBounds,
    double DpiX,
    double DpiY);

public sealed class InlineGeometryCache(int capacity = 512)
{
    private readonly Dictionary<InlineGeometryCacheKey, IReadOnlyList<Rect>> _entries = [];
    private readonly Queue<InlineGeometryCacheKey> _order = [];
    private readonly int _capacity = Math.Max(32, capacity);

    public int Count => _entries.Count;

    public bool TryGet(InlineGeometryCacheKey key, out IReadOnlyList<Rect> rectangles)
        => _entries.TryGetValue(key, out rectangles!);

    public void Set(InlineGeometryCacheKey key, IReadOnlyList<Rect> rectangles)
    {
        if (_entries.ContainsKey(key)) return;
        _entries[key] = rectangles.ToArray();
        _order.Enqueue(key);
        while (_entries.Count > _capacity && _order.TryDequeue(out var oldest)) _entries.Remove(oldest);
    }

    public void Clear() { _entries.Clear(); _order.Clear(); }
}

public static class CorrectionInteractionResolver
{
    public static TextIssue? FindIssueAtRange(IReadOnlyList<TextIssue> issues, int start, int length)
    {
        var end = start + Math.Max(1, length);
        return issues
            .Where(issue => issue.Length == 0
                ? issue.Start >= start && issue.Start <= end
                : issue.Start < end && issue.Start + issue.Length > start)
            .OrderBy(issue => Priority(issue.Category))
            .ThenBy(issue => issue.Length == 0 ? 1 : 0)
            .ThenBy(issue => issue.Length)
            .FirstOrDefault();
    }

    private static int Priority(IssueCategory category) => category switch
    {
        IssueCategory.Orthography => 0,
        IssueCategory.Grammar => 1,
        IssueCategory.Punctuation => 2,
        _ => 3
    };
}
