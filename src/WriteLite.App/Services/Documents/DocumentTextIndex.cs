using System.Text;
using System.Windows.Documents;
using WpfParagraph = System.Windows.Documents.Paragraph;
using WpfTable = System.Windows.Documents.Table;
using WpfList = System.Windows.Documents.List;
using WpfBlock = System.Windows.Documents.Block;

namespace WriteLite.Services.Documents;

/// <summary>
/// A plain-text view of a <see cref="FlowDocument"/> that can map offsets back to positions.
/// </summary>
/// <remarks>
/// This is what keeps the existing analysis stack working after the editor moved
/// from a <c>TextBox</c> to rich text. Every analyzer, the issue model, the ignore
/// service and the correction applier all speak in <c>(start, length)</c> over a
/// flat string, and rewriting them for <see cref="TextPointer"/> would have meant
/// touching the whole language layer. Instead the rich document is projected to the
/// flat string they already expect, and this index carries the way back.
///
/// The projection is built once per analysis pass and treated as immutable: the
/// positions it stores are only valid for the document as it was when it was built,
/// so <see cref="IsStale"/> is checked before any of them is used to change text.
/// </remarks>
public sealed class DocumentTextIndex
{
    private readonly List<Segment> _segments;
    private readonly FlowDocument _document;
    private readonly int _generation;

    private DocumentTextIndex(FlowDocument document, string text, List<Segment> segments, int generation)
    {
        _document = document;
        _segments = segments;
        _generation = generation;
        Text = text;
    }

    /// <summary>The flat projection the analyzers see.</summary>
    public string Text { get; }

    public int Length => Text.Length;

    /// <summary>
    /// Builds an index over the whole document.
    /// </summary>
    /// <remarks>
    /// Must run on the dispatcher: <see cref="TextPointer"/> is thread-affine. The
    /// walk is linear in the number of inline runs, and the resulting string is what
    /// gets handed to the worker thread, so this is the only part of an analysis
    /// pass that touches the UI thread.
    /// </remarks>
    public static DocumentTextIndex Build(FlowDocument document, int generation = 0)
    {
        var builder = new StringBuilder();
        var segments = new List<Segment>();
        var first = true;

        foreach (var paragraph in EnumerateParagraphs(document.Blocks))
        {
            if (!first)
            {
                // The paragraph separator is one character, so a single offset
                // unambiguously identifies the break between two paragraphs.
                segments.Add(new Segment(builder.Length, 1, paragraph.ContentStart, IsBreak: true));
                builder.Append('\n');
            }

            first = false;
            AppendParagraph(builder, segments, paragraph);
        }

        return new DocumentTextIndex(document, builder.ToString(), segments, generation);
    }

    private static void AppendParagraph(StringBuilder builder, List<Segment> segments, WpfParagraph paragraph)
    {
        var pointer = paragraph.ContentStart;
        var end = paragraph.ContentEnd;

        while (pointer is not null && pointer.CompareTo(end) < 0)
        {
            switch (pointer.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.Text:
                    var run = pointer.GetTextInRun(LogicalDirection.Forward);
                    if (run.Length > 0)
                    {
                        segments.Add(new Segment(builder.Length, run.Length, pointer, IsBreak: false));
                        builder.Append(run);
                        pointer = pointer.GetPositionAtOffset(run.Length, LogicalDirection.Forward);
                        continue;
                    }

                    break;

                case TextPointerContext.ElementStart
                    when pointer.GetAdjacentElement(LogicalDirection.Forward) is LineBreak:
                    segments.Add(new Segment(builder.Length, 1, pointer, IsBreak: true));
                    builder.Append('\n');
                    break;
            }

            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }
    }

    /// <summary>Paragraphs in reading order, descending into lists and tables.</summary>
    private static IEnumerable<WpfParagraph> EnumerateParagraphs(IEnumerable<WpfBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case WpfParagraph paragraph:
                    yield return paragraph;
                    break;

                case WpfList list:
                    foreach (var item in list.ListItems)
                    {
                        foreach (var nested in EnumerateParagraphs(item.Blocks))
                        {
                            yield return nested;
                        }
                    }

                    break;

                case WpfTable table:
                    foreach (var group in table.RowGroups)
                    {
                        foreach (var row in group.Rows)
                        {
                            foreach (var cell in row.Cells)
                            {
                                foreach (var nested in EnumerateParagraphs(cell.Blocks))
                                {
                                    yield return nested;
                                }
                            }
                        }
                    }

                    break;

                case Section section:
                    foreach (var nested in EnumerateParagraphs(section.Blocks))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// True when the document has changed since this index was built.
    /// </summary>
    /// <remarks>
    /// Positions from a stale index point into the document the user <em>had</em>,
    /// and applying a correction through one would replace the wrong span. Cheaper
    /// and more reliable than comparing text: the caller bumps a generation counter
    /// on every edit.
    /// </remarks>
    public bool IsStale(int currentGeneration) => currentGeneration != _generation;

    /// <summary>The position at a plain-text offset, or null when the offset is out of range.</summary>
    public TextPointer? PointerAt(int offset)
    {
        if (offset < 0 || offset > Text.Length || _segments.Count == 0)
        {
            return offset == 0 ? _document.ContentStart : null;
        }

        var index = FindSegment(offset);
        if (index < 0)
        {
            return _document.ContentEnd;
        }

        var segment = _segments[index];

        if (segment.IsBreak)
        {
            return segment.Start;
        }

        var delta = offset - segment.Offset;
        return delta == 0 ? segment.Start : segment.Start.GetPositionAtOffset(delta, LogicalDirection.Forward);
    }

    /// <summary>A selectable range for a plain-text span, or null when it cannot be resolved.</summary>
    public TextRange? RangeFor(int start, int length)
    {
        if (start < 0 || length < 0 || start + length > Text.Length)
        {
            return null;
        }

        var from = PointerAt(start);
        var to = PointerAt(start + length);

        if (from is null || to is null || from.CompareTo(to) > 0)
        {
            return null;
        }

        return new TextRange(from, to);
    }

    /// <summary>The plain-text offset of a position, or -1 when it lies outside the indexed content.</summary>
    public int OffsetOf(TextPointer pointer)
    {
        foreach (var segment in _segments)
        {
            var segmentEnd = segment.Start.GetPositionAtOffset(segment.Length, LogicalDirection.Forward);
            if (segmentEnd is null)
            {
                continue;
            }

            if (pointer.CompareTo(segment.Start) >= 0 && pointer.CompareTo(segmentEnd) <= 0)
            {
                return segment.Offset + Math.Max(0, segment.Start.GetOffsetToPosition(pointer));
            }
        }

        // Past the last run: the caret sitting at the very end of the document is a
        // legitimate position and must not report "not found".
        return pointer.CompareTo(_document.ContentEnd) >= 0 ? Text.Length : -1;
    }

    /// <summary>
    /// Binary search for the segment an offset belongs to.
    /// </summary>
    /// <remarks>
    /// An offset sitting exactly on a segment's end belongs to <em>that</em>
    /// segment, not to whatever comes next. Getting this wrong is not cosmetic: the
    /// end offset of the last word in a paragraph would fall through to the document
    /// end, and a correction applied to that range would replace everything from the
    /// word to the end of the document.
    /// </remarks>
    private int FindSegment(int offset)
    {
        var low = 0;
        var high = _segments.Count - 1;
        var candidate = -1;

        while (low <= high)
        {
            var middle = (low + high) / 2;
            var segment = _segments[middle];

            if (offset < segment.Offset)
            {
                high = middle - 1;
            }
            else
            {
                // Remember the last segment starting at or before the offset; if
                // nothing strictly contains it, this is the one it terminates.
                candidate = middle;

                if (offset < segment.Offset + segment.Length)
                {
                    return middle;
                }

                low = middle + 1;
            }
        }

        if (candidate < 0)
        {
            return -1;
        }

        var last = _segments[candidate];
        return offset == last.Offset + last.Length ? candidate : -1;
    }

    private readonly record struct Segment(int Offset, int Length, TextPointer Start, bool IsBreak);
}
