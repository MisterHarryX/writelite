using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using WriteLite.Documents;
using WriteLite.Documents.Model;
using WlParagraph = WriteLite.Documents.Model.DocumentParagraph;
using WlTable = WriteLite.Documents.Model.DocumentTable;
using WpfParagraph = System.Windows.Documents.Paragraph;
using WpfTableRow = System.Windows.Documents.TableRow;
using WpfTableCell = System.Windows.Documents.TableCell;

namespace WriteLite.Services.Reading;

/// <summary>
/// A book prepared for reading: the rendered document, its plain text, and the map
/// between the two.
/// </summary>
/// <remarks>
/// Everything the reader saves — a highlight, an annotation, a bookmark, the place it
/// stopped — is a character offset into <see cref="Text"/>. That is the only
/// coordinate that means the same thing across sessions: a <see cref="TextPointer"/>
/// dies with its <see cref="FlowDocument"/>, and a page number does not exist in a
/// reflowing canvas. This type owns the translation in both directions.
///
/// The text is built while the document is built, run by run, so the mapping is
/// exact rather than reconstructed afterwards. Paragraph breaks, cell breaks and row
/// breaks occupy an offset each without belonging to any run; <see cref="PointerAt"/>
/// resolves such a gap to the start of the next run, which is where a reader would
/// expect to land anyway.
///
/// Structure and typography are deliberately separate. <see cref="Build"/> creates the
/// runs, the text and the offset map — everything that depends on the *book*. Every
/// number that depends on the reader's *type size* is applied afterwards by
/// <see cref="ApplyTypography"/>, which walks a recorded list of blocks and rewrites
/// their metrics in place. That is what makes changing the type size cost a relayout
/// instead of a re-import: no run is recreated, so no <see cref="TextPointer"/>, no
/// offset and no painted mark is invalidated by it.
/// </remarks>
public sealed class ReadingDocument
{
    private readonly List<Span> _spans = [];
    private readonly Dictionary<Run, Span> _byRun = [];
    private readonly List<int> _blockStarts = [];
    private readonly List<TypographyTarget> _typography = [];

    private ReadingDocument(FlowDocument flow)
    {
        Flow = flow;
    }

    public FlowDocument Flow { get; }

    /// <summary>The type settings currently applied to <see cref="Flow"/>.</summary>
    public ReaderTypography Typography { get; private set; } = ReaderTypography.Default;

    /// <summary>The whole book as one string. Every stored coordinate indexes into this.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Offset at which each top-level block begins, for the coarse anchor fallback.</summary>
    public IReadOnlyList<int> BlockStarts => _blockStarts;

    public int Length => Text.Length;

    /// <summary>Number of paragraph-like blocks, shown as the reader's "location" count.</summary>
    public int BlockCount => BlockStarts.Count;

    // ── Building ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Lays a document model out for reading.
    /// </summary>
    /// <remarks>
    /// Deliberately not the editor's <c>FlowDocumentBridge</c>. The editor renders a
    /// document to be changed — page geometry, exact point sizes, the fonts the file
    /// asked for — while a reader renders it to be read: one measure, one type size
    /// the reader chose, and headings that keep the structure legible. Reusing the
    /// editor's layout would drag page margins and embedded fonts into a surface whose
    /// entire job is to calm the text down.
    /// </remarks>
    public static ReadingDocument Build(WlDocument document, ReaderTypography typography)
    {
        ArgumentNullException.ThrowIfNull(document);

        var flow = new FlowDocument
        {
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left
        };

        var reading = new ReadingDocument(flow);
        var text = new System.Text.StringBuilder();
        var numbering = new Dictionary<int, int>();

        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case WlParagraph paragraph:
                    reading.AppendParagraph(flow.Blocks, paragraph, text, numbering);
                    break;

                case WlTable table:
                    reading.AppendTable(flow.Blocks, table, text);
                    numbering.Clear();
                    break;

                case PageBreak:
                    reading.AppendSeparator(flow.Blocks, text);
                    numbering.Clear();
                    break;
            }
        }

        reading.Text = text.ToString();
        reading.ApplyTypography(typography);
        return reading;
    }

    /// <summary>
    /// Re-sets every type-derived measurement on the already-built document.
    /// </summary>
    /// <remarks>
    /// The entire point of the reader's performance story. Changing the type size is a
    /// visual preference, not a change to the book, so it must not reach the importer,
    /// the disk or the offset map. This walks a list recorded during
    /// <see cref="Build"/> — one entry per block that has a size-dependent metric — and
    /// writes the new numbers onto the existing WPF objects. The runs, and therefore
    /// every offset, pointer and painted highlight, are untouched; WPF re-lays the
    /// document out and nothing else happens.
    ///
    /// Cost is O(blocks) with no allocation per run, against O(file) plus a full
    /// re-parse for the rebuild it replaces.
    /// </remarks>
    public void ApplyTypography(ReaderTypography? typography)
    {
        var settings = (typography ?? ReaderTypography.Default).Clamped();
        Typography = settings;

        var size = settings.FontSize;

        Flow.FontSize = size;
        Flow.LineHeight = size * settings.LineSpacing;

        foreach (var target in _typography)
        {
            if (target.Paragraph is { } paragraph)
            {
                if (target.HeadingLevel > 0)
                {
                    // Scaled from the reader's own size rather than from the point sizes
                    // in the file, so a book whose H1 is 36 pt does not shout at 14.
                    var headingSize = size * HeadingScale(target.HeadingLevel);
                    paragraph.FontSize = headingSize;
                    paragraph.LineHeight = headingSize * 1.25;
                }

                paragraph.Margin = target.Role switch
                {
                    TypographyRole.ListItem => new Thickness(
                        (size * 1.6 * (target.ListLevel + 1)) + target.IndentDip, 0, 0, size * 0.4),
                    TypographyRole.Heading => new Thickness(
                        target.IndentDip, size * 1.6, 0, size * 0.5),
                    _ => new Thickness(target.IndentDip, 0, 0, size * 0.85)
                };

                continue;
            }

            if (target.Table is { } table)
            {
                table.Margin = new Thickness(0, size * 0.6, 0, size * 1.1);
                continue;
            }

            if (target.Cell is { } cell)
            {
                cell.Padding = new Thickness(0, size * 0.3, size, size * 0.3);
            }
        }
    }

    private static double HeadingScale(int level) => level switch
    {
        1 => 1.7,
        2 => 1.42,
        3 => 1.22,
        4 => 1.1,
        _ => 1.0
    };

    private void AppendParagraph(
        BlockCollection target,
        WlParagraph source,
        System.Text.StringBuilder text,
        Dictionary<int, int> numbering)
    {
        var paragraph = new WpfParagraph
        {
            TextAlignment = source.Format.Alignment == TextAlign.Justify
                ? TextAlignment.Justify
                : TextAlignment.Left
        };

        // The role, not the numbers. ApplyTypography turns this into margins and sizes,
        // and can do so again whenever the reader changes the type size. The two are
        // separate because a list item's spacing and a heading's type size are
        // independent answers: a heading that is also a list item keeps heading type
        // and takes list spacing, which is what the original layout did.
        var headingLevel = 0;
        var role = TypographyRole.Body;
        var listLevel = 0;

        if (source.IsHeading)
        {
            headingLevel = Math.Clamp(source.OutlineLevel ?? 1, 1, 6);
            role = TypographyRole.Heading;
            paragraph.FontWeight = FontWeights.Medium;
            numbering.Clear();
        }

        if (source.List is { } list)
        {
            role = TypographyRole.ListItem;
            listLevel = list.Level;
        }

        // Indentation authored in the file is in points and does not scale with the
        // reader's preference, so it is carried as a fixed offset rather than rebuilt.
        var indentDip = source.Format.IndentLeftPt > 0 ? source.Format.IndentLeftPt * 96 / 72 : 0;

        _typography.Add(TypographyTarget.ForParagraph(paragraph, role, headingLevel, listLevel, indentDip));

        if (_blockStarts.Count > 0)
        {
            // The break between blocks is one character in the flattened text and
            // belongs to no run — the gap PointerAt knows how to step over.
            text.Append('\n');
        }

        _blockStarts.Add(text.Length);

        if (source.List is { } bullet)
        {
            var marker = bullet.Kind == ListKind.Numbered
                ? $"{Next(numbering, bullet.Level)}. "
                : "• ";
            AddRun(paragraph, marker, text, muted: true);
        }
        else
        {
            numbering.Clear();
        }

        foreach (var inline in source.Inlines)
        {
            switch (inline)
            {
                case TextRun run when run.Text.Length > 0:
                    AddRun(paragraph, run.Text, text, format: run.Format);
                    break;

                case HyperlinkRun link when link.Text.Length > 0:
                    AddRun(paragraph, link.Text, text, link: true);
                    break;

                case LineBreakRun:
                    // A hard break costs one character so the text keeps its shape,
                    // and the LineBreak element itself carries no run.
                    paragraph.Inlines.Add(new LineBreak());
                    text.Append('\n');
                    break;

                case ImageRun image when !string.IsNullOrWhiteSpace(image.Description):
                    AddRun(paragraph, image.Description!, text, muted: true);
                    break;
            }
        }

        if (paragraph.Inlines.Count == 0)
        {
            // An empty paragraph still occupies its offset; without a run there would
            // be nothing to anchor to and the spacing would collapse.
            AddRun(paragraph, string.Empty, text);
        }

        target.Add(paragraph);
    }

    private static int Next(Dictionary<int, int> numbering, int level)
    {
        numbering.TryGetValue(level, out var current);
        numbering[level] = current + 1;

        foreach (var deeper in numbering.Keys.Where(key => key > level).ToList())
        {
            numbering.Remove(deeper);
        }

        return current + 1;
    }

    private void AppendTable(
        BlockCollection target,
        WlTable source,
        System.Text.StringBuilder text)
    {
        var table = new Table { CellSpacing = 0 };
        _typography.Add(TypographyTarget.ForTable(table));

        var columns = Math.Max(1, source.ColumnCount);
        for (var index = 0; index < columns; index++)
        {
            table.Columns.Add(new TableColumn());
        }

        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        foreach (var row in source.Rows)
        {
            var wpfRow = new WpfTableRow();

            for (var index = 0; index < row.Cells.Count; index++)
            {
                if (_blockStarts.Count > 0)
                {
                    text.Append(index == 0 ? '\n' : '\t');
                }

                _blockStarts.Add(text.Length);

                var cell = new WpfTableCell
                {
                    BorderThickness = new Thickness(0, 0, 0, 1)
                };

                cell.SetResourceReference(WpfTableCell.BorderBrushProperty, "WlLineSoft");
                _typography.Add(TypographyTarget.ForCell(cell));

                var paragraph = new WpfParagraph { Margin = new Thickness(0) };
                if (row.IsHeader)
                {
                    paragraph.FontWeight = FontWeights.Medium;
                }

                AddRun(paragraph, row.Cells[index].ToPlainText(), text);
                cell.Blocks.Add(paragraph);
                wpfRow.Cells.Add(cell);
            }

            group.Rows.Add(wpfRow);
        }

        target.Add(table);
    }

    private void AppendSeparator(BlockCollection target, System.Text.StringBuilder text)
    {
        if (_blockStarts.Count > 0)
        {
            text.Append('\n');
        }

        _blockStarts.Add(text.Length);

        var rule = new WpfParagraph
        {
            Margin = new Thickness(0, 26, 0, 26),
            BorderThickness = new Thickness(0, 0, 0, 1),
            TextAlignment = TextAlignment.Center
        };

        rule.SetResourceReference(WpfParagraph.BorderBrushProperty, "WlLineSoft");
        AddRun(rule, string.Empty, text);
        target.Add(rule);
    }

    /// <summary>Adds one run to a paragraph and records where its text starts.</summary>
    private void AddRun(
        WpfParagraph paragraph,
        string value,
        System.Text.StringBuilder text,
        RunFormatting? format = null,
        bool muted = false,
        bool link = false)
    {
        var run = new Run(value);

        if (format is not null)
        {
            if (format.Bold) run.FontWeight = FontWeights.SemiBold;
            if (format.Italic) run.FontStyle = FontStyles.Italic;
            if (format.Underline) run.TextDecorations = TextDecorations.Underline;
            if (format.Strikethrough) run.TextDecorations = TextDecorations.Strikethrough;
        }

        if (muted)
        {
            run.SetResourceReference(TextElement.ForegroundProperty, "WlTextSubtle");
        }
        else if (link)
        {
            run.SetResourceReference(TextElement.ForegroundProperty, "WlBrand");
        }

        var span = new Span(run, text.Length, value.Length);
        _spans.Add(span);
        _byRun[run] = span;

        text.Append(value);
        paragraph.Inlines.Add(run);
    }

    // ── Offsets ↔ pointers ───────────────────────────────────────────────────

    /// <summary>The position in the rendered document for a character offset.</summary>
    public TextPointer PointerAt(int offset)
    {
        if (_spans.Count == 0)
        {
            return Flow.ContentStart;
        }

        offset = Math.Clamp(offset, 0, Length);

        var index = FindSpan(offset);
        var span = _spans[index];

        if (offset >= span.Start + span.Length)
        {
            // Inside a gap. The next run's first character is where a reader lands.
            if (index + 1 < _spans.Count)
            {
                return _spans[index + 1].Run.ContentStart;
            }

            return span.Run.ContentEnd;
        }

        return span.Run.ContentStart.GetPositionAtOffset(offset - span.Start, LogicalDirection.Forward)
               ?? span.Run.ContentStart;
    }

    /// <summary>The character offset of a position in the rendered document.</summary>
    public int OffsetOf(TextPointer? pointer)
    {
        if (pointer is null || _spans.Count == 0)
        {
            return 0;
        }

        if (pointer.Parent is Run run && _byRun.TryGetValue(run, out var span))
        {
            var within = run.ContentStart.GetOffsetToPosition(pointer);
            return Math.Clamp(span.Start + within, 0, Length);
        }

        // Between runs — at a paragraph edge, or on a table structure element. Walk
        // forward to the next run and report where that begins.
        var cursor = pointer.GetNextInsertionPosition(LogicalDirection.Forward);
        var guard = 0;
        while (cursor is not null && guard++ < 64)
        {
            if (cursor.Parent is Run next && _byRun.TryGetValue(next, out var nextSpan))
            {
                return nextSpan.Start;
            }

            cursor = cursor.GetNextInsertionPosition(LogicalDirection.Forward);
        }

        return Length;
    }

    /// <summary>The range covering a stored span, or null when it cannot be placed.</summary>
    public TextRange? RangeFor(int start, int length)
    {
        if (length <= 0 || _spans.Count == 0)
        {
            return null;
        }

        var from = PointerAt(start);
        var to = PointerAt(start + length);
        return from.CompareTo(to) < 0 ? new TextRange(from, to) : null;
    }

    /// <summary>A short excerpt starting at an offset, for bookmark and card previews.</summary>
    public string Preview(int offset, int maxLength = 90)
    {
        if (Length == 0)
        {
            return string.Empty;
        }

        var start = Math.Clamp(offset, 0, Math.Max(0, Length - 1));

        // Start at a word boundary so a preview never opens mid-word.
        while (start > 0 && !char.IsWhiteSpace(Text[start - 1]) && offset - start < 24)
        {
            start--;
        }

        var end = Math.Min(Length, start + maxLength);
        var excerpt = Text[start..end].Replace('\n', ' ').Replace('\t', ' ').Trim();
        excerpt = System.Text.RegularExpressions.Regex.Replace(excerpt, @"\s{2,}", " ");

        return end < Length && excerpt.Length > 0 ? excerpt + "…" : excerpt;
    }

    private int FindSpan(int offset)
    {
        var low = 0;
        var high = _spans.Count - 1;
        var result = 0;

        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (_spans[middle].Start <= offset)
            {
                result = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return result;
    }

    private sealed record Span(Run Run, int Start, int Length);

    private enum TypographyRole
    {
        Body,
        ListItem,
        Heading,
        Table,
        TableCell
    }

    /// <summary>
    /// One block whose metrics are a function of the reader's type size.
    /// </summary>
    /// <remarks>
    /// Holds the WPF object rather than an index into the document, so applying a new
    /// size never walks the block tree. A separator rule is deliberately absent from
    /// this list: its margins are fixed spacing between sections and do not scale.
    /// </remarks>
    private readonly struct TypographyTarget
    {
        private TypographyTarget(
            TypographyRole role,
            WpfParagraph? paragraph,
            Table? table,
            WpfTableCell? cell,
            int headingLevel,
            int listLevel,
            double indentDip)
        {
            Role = role;
            Paragraph = paragraph;
            Table = table;
            Cell = cell;
            HeadingLevel = headingLevel;
            ListLevel = listLevel;
            IndentDip = indentDip;
        }

        /// <summary>Which spacing rule this block's margin follows.</summary>
        public TypographyRole Role { get; }

        public WpfParagraph? Paragraph { get; }

        public Table? Table { get; }

        public WpfTableCell? Cell { get; }

        /// <summary>1–6 for a heading, 0 for anything else. Drives type size, not spacing.</summary>
        public int HeadingLevel { get; }

        /// <summary>List nesting depth, used only by the list spacing rule.</summary>
        public int ListLevel { get; }

        /// <summary>Indentation authored in the source file, which does not scale with type size.</summary>
        public double IndentDip { get; }

        public static TypographyTarget ForParagraph(
            WpfParagraph paragraph, TypographyRole role, int headingLevel, int listLevel, double indentDip) =>
            new(role, paragraph, null, null, headingLevel, listLevel, indentDip);

        public static TypographyTarget ForTable(Table table) =>
            new(TypographyRole.Table, null, table, null, 0, 0, 0);

        public static TypographyTarget ForCell(WpfTableCell cell) =>
            new(TypographyRole.TableCell, null, null, cell, 0, 0, 0);
    }
}
