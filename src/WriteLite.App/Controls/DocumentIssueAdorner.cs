using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using WriteLite.Models;
using WriteLite.Services;
using Pen = System.Windows.Media.Pen;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace WriteLite.Controls;

/// <summary>
/// Draws WriteLite's wavy marks under issues in the rich-text editor.
/// </summary>
/// <remarks>
/// The plain-text editor could ask a <c>TextBox</c> for the rectangle of a character
/// index. Rich text has no such call, so geometry comes from <see cref="TextPointer"/>
/// instead: the host resolves an issue's offsets to a range and this adorner walks
/// that range line by line, asking each end for its character rectangle.
///
/// The wave itself is shared with the plain-text adorner and with the overlay drawn
/// over other applications, so an issue looks the same wherever a writer meets it.
/// </remarks>
public sealed class DocumentIssueAdorner : Adorner
{
    /// <summary>
    /// Resolves an issue to a document range, or null when the offsets no longer apply.
    /// </summary>
    /// <remarks>
    /// Supplied by the host rather than computed here: the mapping lives in the
    /// text index, and the index is rebuilt on a different schedule from the render.
    /// </remarks>
    public delegate TextRange? RangeResolver(TextIssue issue);

    /// <summary>
    /// Beyond this many issues the rest are not drawn.
    /// </summary>
    /// <remarks>
    /// Each mark costs several <see cref="TextPointer"/> hit-tests, which are the
    /// expensive part of laying out rich text. A document with thousands of pending
    /// issues would spend longer drawing squiggles than the user spends reading, and
    /// the panel still lists every one of them.
    /// </remarks>
    private const int MaxRenderedIssues = 400;

    private readonly RichTextBox _editor;
    private readonly RangeResolver _resolve;
    private IReadOnlyList<TextIssue> _issues = [];
    private TextIssue? _focused;

    public DocumentIssueAdorner(RichTextBox editor, RangeResolver resolve)
        : base(editor)
    {
        _editor = editor;
        _resolve = resolve;
        IsHitTestVisible = false;

        _editor.TextChanged += (_, _) => InvalidateVisual();
        _editor.SizeChanged += (_, _) => InvalidateVisual();
        _editor.AddHandler(
            System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
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

        if (_issues.Count == 0 || _editor.ActualWidth <= 0)
        {
            return;
        }

        var viewport = new Rect(0, 0, _editor.ActualWidth, _editor.ActualHeight);
        var drawn = 0;

        foreach (var issue in _issues)
        {
            if (drawn >= MaxRenderedIssues)
            {
                break;
            }

            TextRange? range;
            try
            {
                range = _resolve(issue);
            }
            catch (ArgumentException)
            {
                // The document moved underneath a stale offset; the next analysis
                // pass replaces it. Never let a mark take the render down.
                continue;
            }

            if (range is null)
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

            var rendered = false;

            foreach (var rect in GetLineRects(range))
            {
                if (!rect.IntersectsWith(viewport))
                {
                    continue;
                }

                rendered = true;

                if (isFocused && rect.Width >= 1)
                {
                    var wash = new SolidColorBrush(style.Color) { Opacity = 0.10 };
                    wash.Freeze();
                    drawingContext.DrawRectangle(wash, null, rect);
                }

                if (rect.Width < 1)
                {
                    drawingContext.DrawGeometry(null, pen, IssueWaveGeometry.BuildInsertionTick(rect));
                    continue;
                }

                drawingContext.DrawGeometry(null, pen, IssueWaveGeometry.BuildWave(rect, style.WaveHeight));
            }

            if (rendered)
            {
                drawn++;
            }
        }
    }

    /// <summary>
    /// One rectangle per visual line the range spans.
    /// </summary>
    /// <remarks>
    /// A wrapped issue legitimately needs several marks. The walk steps line by line
    /// using <see cref="TextPointer.GetLineStartPosition"/> rather than character by
    /// character, so a long span costs a handful of hit-tests instead of one per glyph.
    /// </remarks>
    private static IEnumerable<Rect> GetLineRects(TextRange range)
    {
        var start = range.Start;
        var end = range.End;

        if (start.CompareTo(end) == 0)
        {
            var caret = start.GetCharacterRect(LogicalDirection.Forward);
            if (!caret.IsEmpty)
            {
                yield return caret;
            }

            yield break;
        }

        var cursor = start;
        var guard = 0;

        while (cursor is not null && cursor.CompareTo(end) < 0 && guard++ < 4096)
        {
            var nextLine = cursor.GetLineStartPosition(1);
            var lineEnd = nextLine is null || nextLine.CompareTo(end) > 0 ? end : nextLine;

            var from = cursor.GetCharacterRect(LogicalDirection.Forward);
            var to = lineEnd.GetCharacterRect(LogicalDirection.Backward);

            if (from.IsEmpty)
            {
                yield break;
            }

            // A range ending exactly at a line break reports the next line's rect for
            // its end; falling back to the start rect keeps the mark on the right row.
            var right = to.IsEmpty || Math.Abs(to.Top - from.Top) > 0.5 ? from.Right : to.Right;
            var width = Math.Max(0, right - from.Left);

            if (width > 0)
            {
                yield return new Rect(from.Left, from.Top, width, from.Height);
            }

            if (nextLine is null || nextLine.CompareTo(cursor) <= 0)
            {
                yield break;
            }

            cursor = nextLine;
        }
    }
}
