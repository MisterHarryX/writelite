using WriteLite.Models;
using WriteLite.Views;
using System.Windows.Media;

namespace WriteLite.Services;

public sealed class InlineErrorOverlayController : IDisposable
{
    private readonly InlineErrorOverlayWindow _window;
    private readonly ITextRangeGeometryProvider _geometryProvider;
    private readonly IssueRenderingPipeline _renderingPipeline = new();
    private readonly InlineGeometryCache _geometryCache = new();
    private CancellationTokenSource? _updateCts;
    private TextSnapshot? _currentSnapshot;

    public InlineErrorOverlayController(InlineErrorOverlayWindow window, ITextRangeGeometryProvider geometryProvider)
    {
        _window = window;
        _geometryProvider = geometryProvider;
        _window.IssueClicked += OnWindowIssueClicked;
    }

    public event EventHandler<InlineIssueClickedEventArgs>? IssueClicked;

    public async Task UpdateAsync(TextSnapshot snapshot)
    {
        var previousSnapshot = _currentSnapshot;
        var canRetainExisting = previousSnapshot is not null
            && previousSnapshot.Target.Identity == snapshot.Target.Identity
            && string.Equals(previousSnapshot.Text, snapshot.Text, StringComparison.Ordinal);
        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _updateCts = new CancellationTokenSource();
        var token = _updateCts.Token;
        _currentSnapshot = snapshot;
        // Fast and deep analysis publish consecutive snapshots for the same text.
        // Hiding at the beginning of every update made an otherwise valid overlay
        // visible for only ~100 ms. Keep the existing geometry while the replacement
        // is resolved when target and text are unchanged. Click validation below still
        // rejects old issue instances against the new snapshot.
        if (!canRetainExisting)
        {
            _window.Hide();
        }

        var inlineIssues = _renderingPipeline.Filter(snapshot.Text, snapshot.Issues)
            .Where(i => i.Category is IssueCategory.Orthography or IssueCategory.Punctuation or IssueCategory.Grammar)
            .Where(IssueRenderingPipeline.IsInlineCorrectionCandidate)
            .Take(32)
            .ToArray();
        if (inlineIssues.Length == 0)
        {
            _window.Hide();
            return;
        }

        // Both probes below are cross-process UIA COM calls into the monitored
        // application, and this method runs on the dispatcher (it manipulates
        // _window). Executed inline, a busy target — Chromium in particular —
        // parked the entire UI: a dump captured mid-hang shows
        // EditableTextTargetPolicy.IsEditableTextTarget blocking under
        // OnSnapshotChanged for 5–9 seconds, which is past the 5-second ghost
        // threshold, and Windows offered to kill the app. The probes run on a
        // worker; only WPF drawing stays on the dispatcher.
        bool isEditable;
        bool geometryAvailable;
        try
        {
            var target = snapshot.Target;
            (isEditable, geometryAvailable) = await Task.Run(
                () => (target.IsEditable, _geometryProvider.CanProvideGeometry(target.Element)),
                token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            // The element vanished mid-probe (window closed, page navigated).
            // Same policy as elsewhere: keep the last good overlay, log metadata.
            CompatibilityLogger.Technical("inline-probe-failed", exception);
            if (!canRetainExisting) _window.Hide();
            return;
        }

        if (token.IsCancellationRequested) return;
        if (!isEditable)
        {
            _window.Hide();
            return;
        }
        if (!geometryAvailable)
        {
            if (!canRetainExisting) _window.Hide();
            CompatibilityLogger.Technical("inline-range-unavailable", "reason=text-pattern-unavailable");
            return;
        }

        var started = Environment.TickCount64;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(_window);
            var rectangles = new IReadOnlyList<System.Windows.Rect>[inlineIssues.Length];
            var cacheHits = 0;
            var cacheMisses = 0;
            for (var index = 0; index < inlineIssues.Length; index++)
            {
                var issue = inlineIssues[index];
                var key = CreateCacheKey(snapshot, issue, dpi.DpiScaleX, dpi.DpiScaleY);
                if (_geometryCache.TryGet(key, out var cached))
                {
                    rectangles[index] = cached;
                    cacheHits++;
                    continue;
                }

                cacheMisses++;
                var resolved = await _geometryProvider.GetRectanglesAsync(
                    snapshot.Target.Element, issue.Start, issue.Length, token).ConfigureAwait(true);
                rectangles[index] = resolved;
                if (resolved.Count > 0) _geometryCache.Set(key, resolved);

                // Keep overlay work within a frame-friendly bounded window. The
                // remaining issues stay available in the panel and can render on
                // the next snapshot/cache pass.
                if (Environment.TickCount64 - started > 850)
                {
                    CompatibilityLogger.Technical("inline-geometry-budget-hit", $"resolved={index + 1} total={inlineIssues.Length}");
                    for (var remaining = index + 1; remaining < rectangles.Length; remaining++)
                    {
                        rectangles[remaining] = [];
                    }
                    break;
                }
            }

            CompatibilityLogger.Technical("inline-geometry-cache",
                $"hits={cacheHits} misses={cacheMisses} entries={_geometryCache.Count}");
            for (var index = 0; index < inlineIssues.Length; index++)
            {
                rectangles[index] ??= [];
                CompatibilityLogger.Technical(
                    "inline-range-result",
                    $"issue={inlineIssues[index].RuleId} start={inlineIssues[index].Start} length={inlineIssues[index].Length} rectangleCount={rectangles[index].Count}");
            }
            if (token.IsCancellationRequested || !IsCurrent(snapshot))
            {
                CompatibilityLogger.Technical("inline-stale-result-rejected", $"generation={snapshot.GenerationId}");
                return;
            }

            var geometries = InlineIssueGeometryBuilder.Build(inlineIssues, rectangles, snapshot.Target.Bounds);
            if (geometries.Count == 0)
            {
                if (!canRetainExisting) _window.Hide();
                CompatibilityLogger.Technical("inline-range-unavailable", "reason=empty-rectangles");
                return;
            }

            _window.ShowIssues(snapshot.Target.Bounds, geometries);
            CompatibilityLogger.Technical("inline-geometry-created",
                $"target={snapshot.Target.Identity.RuntimeId} generation={snapshot.GenerationId} request={snapshot.RequestId} geometryCount={geometries.Count}");
            var firstRect = geometries.SelectMany(geometry => geometry.Rectangles).FirstOrDefault();
            if (!firstRect.IsEmpty)
            {
                CompatibilityLogger.Technical(
                    "inline-geometry-bounds",
                    $"x={firstRect.X:F0} y={firstRect.Y:F0} width={firstRect.Width:F0} height={firstRect.Height:F0}");
            }
            CompatibilityLogger.Technical("inline-geometry-updated", $"issueCount={geometries.Count}");
            CompatibilityLogger.Technical("inline-update-duration-ms", $"duration={Environment.TickCount64 - started}");
        }
        catch (OperationCanceledException)
        {
            CompatibilityLogger.Technical("inline-geometry-skipped", "reason=cancelled");
        }
        catch (Exception exception)
        {
            CompatibilityLogger.AccessError("inline-geometry", snapshot.Target.ProcessId, exception);
            if (!canRetainExisting) _window.Hide();
        }
    }

    public void Hide() => _window.Hide();

    private void OnWindowIssueClicked(object? sender, InlineIssueClickedEventArgs args)
    {
        var snapshot = _currentSnapshot;
        var currentIssue = snapshot is null
            ? null
            : snapshot.Issues.FirstOrDefault(issue => IsSameIssue(issue, args.Issue));
        if (snapshot is null
            || !snapshot.Target.IsEditable
            || currentIssue is null
            || !IsExactCurrentRange(currentIssue, snapshot.Text))
        {
            CompatibilityLogger.Technical("inline-stale-result-rejected", "reason=clicked-issue-not-current");
            _window.Hide();
            return;
        }

        CompatibilityLogger.Technical(
            "inline-issue-clicked-current",
            $"target={snapshot.Target.Identity.RuntimeId} generation={snapshot.GenerationId} request={snapshot.RequestId} issue={args.Issue.RuleId}");
        IssueClicked?.Invoke(this, new InlineIssueClickedEventArgs(currentIssue, args.Anchor));
    }

    private bool IsCurrent(TextSnapshot snapshot) => _currentSnapshot is not null
        && _currentSnapshot.GenerationId == snapshot.GenerationId
        && _currentSnapshot.RequestId == snapshot.RequestId
        && _currentSnapshot.Target.Identity == snapshot.Target.Identity
        && string.Equals(_currentSnapshot.Text, snapshot.Text, StringComparison.Ordinal);

    private static InlineGeometryCacheKey CreateCacheKey(TextSnapshot snapshot, TextIssue issue, double dpiX, double dpiY)
        => new(
            snapshot.Target.Identity.RuntimeId,
            snapshot.TextVersion,
            issue.RuleId,
            issue.Category,
            issue.Start,
            issue.Length,
            issue.Original,
            snapshot.Target.Bounds,
            dpiX,
            dpiY);

    private static bool IsExactCurrentRange(TextIssue issue, string text)
    {
        if (!TextCorrectionService.IsRangeValid(text, issue.Start, issue.Length)) return false;
        return issue.Length == 0
            ? issue.Original.Length == 0
            : string.Equals(text.Substring(issue.Start, issue.Length), issue.Original, StringComparison.Ordinal);
    }

    private static bool IsSameIssue(TextIssue left, TextIssue right) =>
        left.Start == right.Start
        && left.Length == right.Length
        && left.Category == right.Category
        && string.Equals(left.Original, right.Original, StringComparison.Ordinal)
        && string.Equals(left.Replacement, right.Replacement, StringComparison.Ordinal);

    public void Dispose()
    {
        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _geometryCache.Clear();
        _window.Hide();
        _window.IssueClicked -= OnWindowIssueClicked;
    }
}
