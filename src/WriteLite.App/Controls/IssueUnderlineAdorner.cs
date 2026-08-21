using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using WriteLite.Models;
using WriteLite.Services;
using Pen = System.Windows.Media.Pen;
using TextBox = System.Windows.Controls.TextBox;

namespace WriteLite.Controls;

/// <summary>
/// Draws WriteLite's wavy marks under the issues found in a <see cref="TextBox"/>.
/// </summary>
/// <remarks>
/// WPF has no underline decoration for a text range inside a <c>TextBox</c>, so the marks
/// are rendered in the adorner layer above the control using
/// <see cref="TextBox.GetRectFromCharacterIndex(int)"/> for geometry. That is the same
/// wave the inline overlay draws over other applications, so a correction looks the same
/// wherever the user meets it.
///
/// Rendering is per visible line rather than per character: a long document with many
/// issues costs a handful of rect lookups per line, not one per glyph.
/// </remarks>
public sealed class IssueUnderlineAdorner : Adorner
{
    private readonly TextBox _textBox;
    private IReadOnlyList<TextIssue> _issues = [];
    private TextIssue? _focused;

    public IssueUnderlineAdorner(TextBox adornedElement)
        : base(adornedElement)
    {
        _textBox = adornedElement;
        IsHitTestVisible = false;
        _textBox.TextChanged += (_, _) => InvalidateVisual();
        _textBox.SizeChanged += (_, _) => InvalidateVisual();
        _textBox.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
            new System.Windows.Controls.ScrollChangedEventHandler((_, _) => InvalidateVisual()));
    }

    public void SetIssues(IReadOnlyList<TextIssue> issues)
    {
        _issues = issues;
        InvalidateVisual();
    }

    /// <summary>Thickens the mark for the issue whose card is currently selected.</summary>
    public void SetFocused(TextIssue? issue)
    {
        _focused = issue;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (_issues.Count == 0 || _textBox.ActualWidth <= 0)
        {
            return;
        }

        var textLength = _textBox.Text.Length;
        var viewport = new Rect(0, 0, _textBox.ActualWidth, _textBox.ActualHeight);

        foreach (var issue in _issues)
        {
            if (issue.Start < 0 || issue.Start > textLength)
            {
                continue;
            }

            var isFocused = _focused is not null
                            && _focused.Start == issue.Start
                            && _focused.Length == issue.Length
                            && string.Equals(_focused.RuleId, issue.RuleId, StringComparison.Ordinal);

            var style = IssueUnderlineTheme.ForIssue(issue);
            var brush = new SolidColorBrush(style.Color) { Opacity = isFocused ? 1.0 : style.Opacity };
            brush.Freeze();
            var pen = new Pen(brush, isFocused ? style.Thickness + 0.5 : style.Thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();

            foreach (var rect in GetLineRects(issue, textLength))
            {
                if (!rect.IntersectsWith(viewport))
                {
                    continue;
                }

                // A selected issue also gets a faint wash so it is findable in a long text.
                if (isFocused && rect.Width >= 1)
                {
                    var wash = new SolidColorBrush(style.Color) { Opacity = 0.10 };
                    wash.Freeze();
                    drawingContext.DrawRectangle(wash, null, rect);
                }

                if (issue.Length == 0 || rect.Width < 1)
                {
                    drawingContext.DrawGeometry(null, pen, IssueWaveGeometry.BuildInsertionTick(rect));
                    continue;
                }

                drawingContext.DrawGeometry(null, pen, IssueWaveGeometry.BuildWave(rect, style.WaveHeight));
            }
        }
    }

    /// <summary>
    /// One rectangle per visual line the issue spans. Wrapped text means a single issue can
    /// legitimately need several marks.
    /// </summary>
    private IEnumerable<Rect> GetLineRects(TextIssue issue, int textLength)
    {
        var start = Math.Clamp(issue.Start, 0, textLength);
        var end = Math.Clamp(issue.Start + Math.Max(issue.Length, 0), start, textLength);

        if (end == start)
        {
            var caret = _textBox.GetRectFromCharacterIndex(start);
            if (!caret.IsEmpty)
            {
                yield return caret;
            }

            yield break;
        }

        var index = start;
        while (index < end)
        {
            var lineStart = _textBox.GetRectFromCharacterIndex(index);
            if (lineStart.IsEmpty)
            {
                yield break;
            }

            // Walk to the last character that still sits on this visual line.
            var lineEnd = index;
            var top = lineStart.Top;
            while (lineEnd + 1 <= end)
            {
                var next = _textBox.GetRectFromCharacterIndex(lineEnd + 1, trailingEdge: false);
                if (next.IsEmpty || Math.Abs(next.Top - top) > 0.5)
                {
                    break;
                }

                lineEnd++;
            }

            var closing = _textBox.GetRectFromCharacterIndex(lineEnd, trailingEdge: true);
            var right = closing.IsEmpty ? lineStart.Right : closing.Right;
            var width = Math.Max(0, right - lineStart.Left);
            if (width > 0)
            {
                yield return new Rect(lineStart.Left, lineStart.Top, width, lineStart.Height);
            }

            if (lineEnd <= index)
            {
                yield break;
            }

            index = lineEnd;
            if (index >= end)
            {
                yield break;
            }

            index++;
        }
    }
}
