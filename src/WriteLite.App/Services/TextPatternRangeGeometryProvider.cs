using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace WriteLite.Services;

public sealed class TextPatternRangeGeometryProvider : ITextRangeGeometryProvider
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMilliseconds(550);
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public bool CanProvideGeometry(AutomationElement element)
    {
        // Never call a foreign UIA provider synchronously from the WPF dispatcher.
        // Capability is checked inside the bounded background operation below.
        return element is not null;
    }

    public Task<IReadOnlyList<Rect>> GetRectanglesAsync(
        AutomationElement element,
        int start,
        int length,
        CancellationToken cancellationToken)
    {
        return GetRectanglesBoundedAsync(element, start, length, cancellationToken);
    }

    private async Task<IReadOnlyList<Rect>> GetRectanglesBoundedAsync(
        AutomationElement element,
        int start,
        int length,
        CancellationToken cancellationToken)
    {
        if (!_operationGate.Wait(0))
        {
            CompatibilityLogger.Technical("inline-geometry-skipped", "reason=uia-worker-busy");
            return [];
        }

        var ownsLease = 1;
        var work = Task.Run(() =>
        {
            try
            {
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var textObject) ||
                    textObject is not TextPattern textPattern)
                {
                    return (IReadOnlyList<Rect>)[];
                }

                if (start < 0 || length < 0)
                {
                    return (IReadOnlyList<Rect>)[];
                }

                var documentRange = textPattern.DocumentRange;
                var issueRange = documentRange.Clone();

            // Clone starts as the whole document. Collapse both endpoints before
            // applying the UTF-16 offset; otherwise End remains at document end and
            // a one-word issue becomes a field-spanning range that the geometry
            // validator correctly rejects.
                issueRange.MoveEndpointByRange(TextPatternRangeEndpoint.Start, documentRange, TextPatternRangeEndpoint.Start);
                issueRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, documentRange, TextPatternRangeEndpoint.Start);
                if (start > 0)
                {
                    issueRange.Move(TextUnit.Character, start);
                }

                if (length > 0)
                {
                    issueRange.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, length);
                }
                else
                {
                // UIA returns no rectangle for an empty range. Query its following
                // character and collapse it to a small insertion marker instead.
                    issueRange.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1);
                }

            // WPF UI Automation returns Rect[], not raw double quads.
                var rectangles = issueRange.GetBoundingRectangles();
                if (rectangles is null || rectangles.Length == 0)
                {
                    return (IReadOnlyList<Rect>)[];
                }

                var result = new List<Rect>(rectangles.Length);
                foreach (var rect in rectangles)
                {
                    if (rect.Width > 0 && rect.Height > 0)
                    {
                        result.Add(length == 0 ? InlineIssueGeometryBuilder.CreateInsertionMarker(rect) : rect);
                    }
                }

                return result;
            }
            finally
            {
                ReleaseLease(ref ownsLease);
            }
        }, CancellationToken.None);

        try
        {
            return await work.WaitAsync(OperationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ReleaseLease(ref ownsLease);
            CompatibilityLogger.Technical(
                "inline-geometry-skipped",
                $"reason=uia-timeout timeoutMs={OperationTimeout.TotalMilliseconds:F0}");
            return [];
        }
        catch (OperationCanceledException)
        {
            ReleaseLease(ref ownsLease);
            throw;
        }
    }

    private void ReleaseLease(ref int ownsLease)
    {
        if (Interlocked.Exchange(ref ownsLease, 0) == 1)
        {
            _operationGate.Release();
        }
    }
}
