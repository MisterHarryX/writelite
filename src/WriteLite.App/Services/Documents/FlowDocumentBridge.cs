using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using WriteLite.Documents.Model;
using WpfBlock = System.Windows.Documents.Block;
using WpfParagraph = System.Windows.Documents.Paragraph;
using WpfList = System.Windows.Documents.List;
using WpfListItem = System.Windows.Documents.ListItem;
using WpfTable = System.Windows.Documents.Table;
using WpfTableRow = System.Windows.Documents.TableRow;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfHyperlink = System.Windows.Documents.Hyperlink;
using WpfRun = System.Windows.Documents.Run;
using WpfLineBreak = System.Windows.Documents.LineBreak;
using WlParagraph = WriteLite.Documents.Model.DocumentParagraph;
using WlTable = WriteLite.Documents.Model.DocumentTable;
using WlTableRow = WriteLite.Documents.Model.TableRow;
using WlTableCell = WriteLite.Documents.Model.TableCell;
using WlTextAlign = WriteLite.Documents.Model.TextAlign;
// WinForms is enabled in this project, so System.Drawing brings same-named types
// into scope; the media ones are always what a FlowDocument means.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace WriteLite.Services.Documents;

/// <summary>
/// Translates between WriteLite's document model and the editor's <see cref="FlowDocument"/>.
/// </summary>
/// <remarks>
/// The model stays the single source of truth for what a document <em>is</em>; the
/// FlowDocument is only how it is shown and edited. Keeping the two apart is what
/// lets the same document be saved as DOCX, ODT, TXT or PDF without the editor's
/// presentation choices leaking into the file.
///
/// Anything WPF cannot represent inline is carried on <see cref="FrameworkContentElement.Tag"/>
/// so it survives the trip back: the heading level of a paragraph, its source style
/// name, and the fact that a given empty paragraph is really a page break.
/// </remarks>
public static class FlowDocumentBridge
{
    /// <summary>Marks the paragraph that stands in for a page break.</summary>
    public const string PageBreakTag = "wl:page-break";

    /// <summary>Points per WPF device-independent pixel.</summary>
    private const double PixelsPerPoint = 96.0 / 72.0;

    public static double ToPixels(double points) => points * PixelsPerPoint;

    public static double ToPoints(double pixels) => pixels / PixelsPerPoint;

    // ── Model → FlowDocument ─────────────────────────────────────────────────

    public static FlowDocument ToFlowDocument(WlDocument document, EditorTypography typography)
    {
        var flow = new FlowDocument
        {
            FontFamily = typography.DefaultFamily,
            FontSize = ToPixels(typography.DefaultSizePt),
            Foreground = typography.Foreground,
            PagePadding = new Thickness(0),
            // The editor is a continuous column, not a paged view: WPF's column
            // splitting would silently reflow a long document into newspaper columns.
            ColumnWidth = double.PositiveInfinity,
            IsOptimalParagraphEnabled = true,
            IsHyphenationEnabled = false
        };

        foreach (var block in BuildBlocks(document.Blocks, typography))
        {
            flow.Blocks.Add(block);
        }

        if (flow.Blocks.Count == 0)
        {
            flow.Blocks.Add(new WpfParagraph(new WpfRun(string.Empty)));
        }

        return flow;
    }

    /// <summary>
    /// Converts model blocks, gathering consecutive list items into one WPF list.
    /// </summary>
    private static IEnumerable<WpfBlock> BuildBlocks(IEnumerable<BlockElement> blocks, EditorTypography typography)
    {
        var pending = new List<WlParagraph>();
        ListKind? pendingKind = null;

        IEnumerable<WpfBlock> FlushList()
        {
            if (pending.Count == 0)
            {
                yield break;
            }

            yield return BuildList(pending, pendingKind ?? ListKind.Bullet, typography);
            pending = [];
            pendingKind = null;
        }

        foreach (var block in blocks)
        {
            if (block is WlParagraph { List: { } list } item)
            {
                if (pendingKind is not null && pendingKind != list.Kind)
                {
                    foreach (var flushed in FlushList())
                    {
                        yield return flushed;
                    }
                }

                pendingKind = list.Kind;
                pending.Add(item);
                continue;
            }

            foreach (var flushed in FlushList())
            {
                yield return flushed;
            }

            switch (block)
            {
                case WlParagraph paragraph:
                    yield return BuildParagraph(paragraph, typography);
                    break;

                case WlTable table:
                    yield return BuildTable(table, typography);
                    break;

                case PageBreak:
                    yield return BuildPageBreak(typography);
                    break;
            }
        }

        foreach (var flushed in FlushList())
        {
            yield return flushed;
        }
    }

    private static WpfList BuildList(List<WlParagraph> items, ListKind kind, EditorTypography typography)
    {
        var list = new WpfList
        {
            MarkerStyle = kind == ListKind.Numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            MarkerOffset = ToPixels(9),
            Padding = new Thickness(ToPixels(18), 0, 0, 0),
            Margin = new Thickness(0)
        };

        foreach (var item in items)
        {
            list.ListItems.Add(new WpfListItem(BuildParagraph(item, typography)));
        }

        return list;
    }

    private static WpfParagraph BuildPageBreak(EditorTypography typography)
    {
        return new WpfParagraph
        {
            Tag = PageBreakTag,
            Margin = new Thickness(0, ToPixels(14), 0, ToPixels(14)),
            BorderBrush = typography.RuleBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            FontSize = 1,
            // A caret has to be able to sit on this paragraph so it can be deleted;
            // a zero-height empty paragraph is unclickable and becomes permanent.
            LineHeight = ToPixels(2)
        };
    }

    public static WpfParagraph BuildParagraph(WlParagraph source, EditorTypography typography)
    {
        var paragraph = new WpfParagraph
        {
            Tag = new ParagraphMetadata(source.OutlineLevel, source.StyleName, source.List),
            TextAlignment = ToWpfAlignment(source.Format.Alignment),
            TextIndent = ToPixels(source.Format.FirstLineIndentPt),
            Margin = new Thickness(
                ToPixels(source.Format.IndentLeftPt),
                ToPixels(source.Format.SpaceBeforePt),
                ToPixels(source.Format.IndentRightPt),
                ToPixels(source.Format.SpaceAfterPt))
        };

        var heading = source.IsHeading ? typography.Heading(source.OutlineLevel!.Value) : null;
        var baseSizePt = heading?.SizePt ?? typography.DefaultSizePt;

        paragraph.FontSize = ToPixels(baseSizePt);
        if (heading is not null)
        {
            paragraph.FontWeight = FontWeights.SemiBold;
        }

        // WPF line spacing is an absolute height, so the multiplier has to be applied
        // against this paragraph's own size rather than the document's.
        if (Math.Abs(source.Format.LineSpacing - 1.0) > 0.01)
        {
            paragraph.LineHeight = ToPixels(baseSizePt) * source.Format.LineSpacing * 1.2;
            paragraph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }

        foreach (var inline in source.Inlines)
        {
            switch (inline)
            {
                case TextRun run when run.Text.Length > 0:
                    paragraph.Inlines.Add(BuildRun(run.Text, run.Format, typography));
                    break;

                case LineBreakRun:
                    paragraph.Inlines.Add(new WpfLineBreak());
                    break;

                case HyperlinkRun link:
                    paragraph.Inlines.Add(BuildHyperlink(link, typography));
                    break;

                case ImageRun image when !string.IsNullOrWhiteSpace(image.Description):
                    paragraph.Inlines.Add(BuildRun(
                        $"[{image.Description}]",
                        new RunFormatting(Italic: true),
                        typography));
                    break;
            }
        }

        return paragraph;
    }

    private static WpfRun BuildRun(string text, RunFormatting format, EditorTypography typography)
    {
        var run = new WpfRun(text);
        ApplyFormatting(run, format, typography);
        return run;
    }

    private static WpfHyperlink BuildHyperlink(HyperlinkRun link, EditorTypography typography)
    {
        var hyperlink = new WpfHyperlink
        {
            Foreground = typography.LinkBrush,
            Tag = link.Target
        };

        foreach (var run in link.Runs)
        {
            hyperlink.Inlines.Add(BuildRun(run.Text, run.Format, typography));
        }

        // An absolute URI is what the navigation handler needs; an anchor or a
        // malformed target stays readable text with no navigation attached.
        if (Uri.TryCreate(link.Target, UriKind.Absolute, out var uri))
        {
            hyperlink.NavigateUri = uri;
        }

        return hyperlink;
    }

    public static void ApplyFormatting(Inline inline, RunFormatting format, EditorTypography typography)
    {
        if (format.Bold)
        {
            inline.FontWeight = FontWeights.Bold;
        }

        if (format.Italic)
        {
            inline.FontStyle = FontStyles.Italic;
        }

        if (format.Underline || format.Strikethrough)
        {
            var decorations = new TextDecorationCollection();
            if (format.Underline)
            {
                decorations.Add(TextDecorations.Underline);
            }

            if (format.Strikethrough)
            {
                decorations.Add(TextDecorations.Strikethrough);
            }

            inline.TextDecorations = decorations;
        }

        if (format.FontFamily is { Length: > 0 } family)
        {
            inline.FontFamily = new FontFamily(family);
        }

        if (format.FontSizePt is { } size)
        {
            inline.FontSize = ToPixels(size);
        }

        if (ParseBrush(format.ColorHex) is { } foreground)
        {
            inline.Foreground = foreground;
        }
        else if (typography.Foreground is { } inherited)
        {
            inline.Foreground = inherited;
        }

        if (ParseBrush(format.HighlightHex) is { } background)
        {
            inline.Background = background;
        }
    }

    private static WpfTable BuildTable(WlTable source, EditorTypography typography)
    {
        var table = new WpfTable
        {
            CellSpacing = 0,
            Margin = new Thickness(0, ToPixels(6), 0, ToPixels(6))
        };

        var columns = Math.Max(1, source.ColumnCount);
        for (var index = 0; index < columns; index++)
        {
            table.Columns.Add(new TableColumn
            {
                Width = source.ColumnWidths.Count == columns
                    ? new GridLength(source.ColumnWidths[index], GridUnitType.Star)
                    : new GridLength(1, GridUnitType.Star)
            });
        }

        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        foreach (var sourceRow in source.Rows)
        {
            var row = new WpfTableRow();

            foreach (var sourceCell in sourceRow.Cells)
            {
                var cell = new WpfTableCell
                {
                    ColumnSpan = Math.Max(1, sourceCell.ColumnSpan),
                    BorderBrush = typography.RuleBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(ToPixels(6), ToPixels(4), ToPixels(6), ToPixels(4))
                };

                if (sourceRow.IsHeader)
                {
                    cell.FontWeight = FontWeights.SemiBold;
                }

                foreach (var block in sourceCell.Blocks.OfType<WlParagraph>())
                {
                    cell.Blocks.Add(BuildParagraph(block, typography));
                }

                if (cell.Blocks.Count == 0)
                {
                    cell.Blocks.Add(new WpfParagraph());
                }

                row.Cells.Add(cell);
            }

            group.Rows.Add(row);
        }

        return table;
    }

    // ── FlowDocument → Model ─────────────────────────────────────────────────

    public static WlDocument ToDocumentModel(FlowDocument flow, DocumentMetadata? metadata = null)
    {
        var document = WlDocument.Empty();
        document.Metadata = metadata ?? new DocumentMetadata();

        var section = document.CurrentSection();
        foreach (var block in ReadBlocks(flow.Blocks, listLevel: 0, listKind: null))
        {
            section.Blocks.Add(block);
        }

        if (section.Blocks.Count == 0)
        {
            section.Blocks.Add(WlParagraph.FromText(string.Empty));
        }

        return document;
    }

    private static IEnumerable<BlockElement> ReadBlocks(IEnumerable<WpfBlock> blocks, int listLevel, ListKind? listKind)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case WpfParagraph paragraph when ReferenceEquals(paragraph.Tag, PageBreakTag)
                                                 || (paragraph.Tag as string) == PageBreakTag:
                    yield return new PageBreak();
                    break;

                case WpfParagraph paragraph:
                    yield return ReadParagraph(paragraph, listLevel, listKind);
                    break;

                case WpfList list:
                    var kind = list.MarkerStyle is TextMarkerStyle.Decimal
                        or TextMarkerStyle.LowerLatin
                        or TextMarkerStyle.UpperLatin
                        or TextMarkerStyle.LowerRoman
                        or TextMarkerStyle.UpperRoman
                        ? ListKind.Numbered
                        : ListKind.Bullet;

                    foreach (var item in list.ListItems)
                    {
                        foreach (var nested in ReadBlocks(item.Blocks, listLevel + 1, kind))
                        {
                            yield return nested;
                        }
                    }

                    break;

                case WpfTable table:
                    yield return ReadTable(table);
                    break;

                case Section section:
                    foreach (var nested in ReadBlocks(section.Blocks, listLevel, listKind))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    private static WlParagraph ReadParagraph(WpfParagraph source, int listLevel, ListKind? listKind)
    {
        var metadata = source.Tag as ParagraphMetadata;

        var paragraph = new WlParagraph
        {
            OutlineLevel = metadata?.OutlineLevel,
            StyleName = metadata?.StyleName,
            Format = new ParagraphFormatting(
                FromWpfAlignment(source.TextAlignment),
                ReadLineSpacing(source),
                Math.Max(0, ToPoints(source.Margin.Top)),
                Math.Max(0, ToPoints(source.Margin.Bottom)),
                Math.Max(0, ToPoints(source.Margin.Left)),
                Math.Max(0, ToPoints(source.Margin.Right)),
                ToPoints(source.TextIndent))
        };

        if (listLevel > 0 && listKind is { } kind)
        {
            paragraph.List = new ListInfo(kind, Math.Clamp(listLevel - 1, 0, 8));
        }
        else if (metadata?.List is { } inherited)
        {
            paragraph.List = inherited;
        }

        ReadInlines(source.Inlines, paragraph, source);
        return paragraph;
    }

    private static double ReadLineSpacing(WpfParagraph paragraph)
    {
        if (double.IsNaN(paragraph.LineHeight) || paragraph.LineHeight <= 0 || paragraph.FontSize <= 0)
        {
            return 1.0;
        }

        return Math.Clamp(paragraph.LineHeight / (paragraph.FontSize * 1.2), 0.5, 5);
    }

    private static void ReadInlines(InlineCollection inlines, WlParagraph target, WpfParagraph owner)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case WpfRun run when run.Text.Length > 0:
                    Append(target, run.Text, ReadFormatting(run, owner));
                    break;

                case WpfLineBreak:
                    target.Inlines.Add(new LineBreakRun());
                    break;

                case WpfHyperlink hyperlink:
                    var link = new HyperlinkRun(hyperlink.NavigateUri?.ToString() ?? hyperlink.Tag as string);
                    foreach (var child in hyperlink.Inlines.OfType<WpfRun>())
                    {
                        link.Runs.Add(new TextRun(child.Text, ReadFormatting(child, owner)));
                    }

                    if (link.Runs.Count > 0)
                    {
                        target.Inlines.Add(link);
                    }

                    break;

                case Span span:
                    ReadInlines(span.Inlines, target, owner);
                    break;
            }
        }
    }

    private static void Append(WlParagraph paragraph, string text, RunFormatting format)
    {
        if (paragraph.Inlines.Count > 0
            && paragraph.Inlines[^1] is TextRun previous
            && previous.Format == format)
        {
            previous.Text += text;
            return;
        }

        paragraph.Inlines.Add(new TextRun(text, format));
    }

    /// <summary>
    /// Reads a run's effective formatting.
    /// </summary>
    /// <remarks>
    /// Effective, not local: WPF inherits font properties down the inline tree, so a
    /// run inside a bold span reports <c>FontWeight.Normal</c> locally while
    /// rendering bold. <see cref="DependencyObject.GetValue"/> on the run gives the
    /// value actually in force, which is what the file should record.
    ///
    /// Sizes and colours are only recorded when they differ from the paragraph's own,
    /// so a document does not acquire an explicit font size on every run just by
    /// being opened and saved.
    /// </remarks>
    private static RunFormatting ReadFormatting(Inline run, WpfParagraph owner)
    {
        var decorations = run.TextDecorations;
        var underline = decorations?.Any(decoration => decoration.Location == TextDecorationLocation.Underline) == true;
        var strikethrough = decorations?.Any(decoration => decoration.Location == TextDecorationLocation.Strikethrough) == true;

        var size = run.FontSize;
        var ownerSize = owner.FontSize;

        var family = run.FontFamily?.Source;
        var ownerFamily = owner.FontFamily?.Source;

        string? color = null;
        if (run.Foreground is SolidColorBrush { } brush && !ReferenceEquals(run.Foreground, owner.Foreground))
        {
            color = ToHex(brush.Color);
        }

        string? highlight = null;
        if (run.Background is SolidColorBrush background)
        {
            highlight = ToHex(background.Color);
        }

        return new RunFormatting(
            run.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight(),
            run.FontStyle != FontStyles.Normal,
            underline,
            strikethrough,
            string.Equals(family, ownerFamily, StringComparison.Ordinal) ? null : family,
            Math.Abs(size - ownerSize) < 0.01 ? null : Math.Round(ToPoints(size), 1),
            color,
            highlight);
    }

    private static WlTable ReadTable(WpfTable source)
    {
        var table = new WlTable();

        foreach (var column in source.Columns)
        {
            table.ColumnWidths.Add(column.Width.IsStar ? column.Width.Value : 1);
        }

        var total = table.ColumnWidths.Sum();
        if (total > 0)
        {
            for (var index = 0; index < table.ColumnWidths.Count; index++)
            {
                table.ColumnWidths[index] /= total;
            }
        }

        foreach (var group in source.RowGroups)
        {
            foreach (var sourceRow in group.Rows)
            {
                var row = new WlTableRow
                {
                    IsHeader = sourceRow.Cells.Count > 0
                               && sourceRow.Cells[0].FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight()
                };

                foreach (var sourceCell in sourceRow.Cells)
                {
                    var cell = new WlTableCell { ColumnSpan = sourceCell.ColumnSpan };
                    foreach (var block in ReadBlocks(sourceCell.Blocks, 0, null))
                    {
                        cell.Blocks.Add(block);
                    }

                    if (cell.Blocks.Count == 0)
                    {
                        cell.Blocks.Add(WlParagraph.FromText(string.Empty));
                    }

                    row.Cells.Add(cell);
                }

                if (row.Cells.Count > 0)
                {
                    table.Rows.Add(row);
                }
            }
        }

        return table;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    public static TextAlignment ToWpfAlignment(WlTextAlign alignment) => alignment switch
    {
        WlTextAlign.Center => TextAlignment.Center,
        WlTextAlign.Right => TextAlignment.Right,
        WlTextAlign.Justify => TextAlignment.Justify,
        _ => TextAlignment.Left
    };

    public static WlTextAlign FromWpfAlignment(TextAlignment alignment) => alignment switch
    {
        TextAlignment.Center => WlTextAlign.Center,
        TextAlignment.Right => WlTextAlign.Right,
        TextAlignment.Justify => WlTextAlign.Justify,
        _ => WlTextAlign.Left
    };

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static SolidColorBrush? ParseBrush(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#')
        {
            return null;
        }

        try
        {
            var brush = new SolidColorBrush(Color.FromRgb(
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16)));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>What a WPF paragraph cannot express about itself.</summary>
public sealed record ParagraphMetadata(int? OutlineLevel, string? StyleName, ListInfo? List);

/// <summary>
/// The editor's own type defaults, so the bridge never reaches into a theme.
/// </summary>
public sealed record EditorTypography(
    FontFamily DefaultFamily,
    double DefaultSizePt,
    Brush? Foreground,
    Brush RuleBrush,
    Brush LinkBrush)
{
    /// <summary>Size and weight for a heading level, used when the source states none.</summary>
    public HeadingStyle Heading(int level) => level switch
    {
        1 => new HeadingStyle(DefaultSizePt * 1.85),
        2 => new HeadingStyle(DefaultSizePt * 1.5),
        3 => new HeadingStyle(DefaultSizePt * 1.28),
        4 => new HeadingStyle(DefaultSizePt * 1.15),
        5 => new HeadingStyle(DefaultSizePt * 1.06),
        _ => new HeadingStyle(DefaultSizePt)
    };
}

public sealed record HeadingStyle(double SizePt);
