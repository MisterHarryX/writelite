using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WriteLite.Services.Notes;
using Brush = System.Windows.Media.Brush;
using CheckBox = System.Windows.Controls.CheckBox;
using FontStyle = System.Windows.FontStyle;
using Image = System.Windows.Controls.Image;
using List = System.Windows.Documents.List;

namespace WriteLite.Views.Notes;

/// <summary>
/// What a block of the editor's document is, in note terms. Carried in <c>Block.Tag</c>.
/// </summary>
/// <remarks>
/// A tag rather than an inference from formatting: a 24-point paragraph pasted from a web
/// page is not a heading, and a heading the user made must stay one whatever its runs say.
/// </remarks>
public sealed class NoteBlockTag
{
    public NoteBlockKind Kind { get; set; } = NoteBlockKind.Paragraph;

    public string? BookmarkId { get; set; }

    public string? BookmarkName { get; set; }

    public string? AssetId { get; set; }

    public double? Width { get; set; }
}

/// <summary>Builds the embedded parts of a note — pictures, drawings, tick boxes — for the mapper.</summary>
public interface INoteEmbedFactory
{
    UIElement CreateImage(NoteBlock block);

    NoteSketchSurface CreateSketch(NoteBlock block);

    CheckBox CreateCheck(bool isChecked);
}

/// <summary>
/// Converts a note body between its stored blocks and the editor's <see cref="FlowDocument"/>.
/// </summary>
/// <remarks>
/// <para>Both directions are lossless for everything the editor can produce: the round trip
/// is what the tests hold it to. Content the editor did not produce — a table or a figure
/// pasted from elsewhere — is flattened into paragraphs on the way out rather than dropped,
/// so pasting never costs text.</para>
///
/// <para>Formatting is read from effective values, not from the element tree. WPF splits and
/// nests spans freely as the user edits; what matters is whether a letter is bold, not which
/// <c>Bold</c> element it happens to sit in.</para>
/// </remarks>
public static class NoteDocumentMapper
{
    public const double BodyFontSize = 15.5;

    /// <summary>
    /// The marker colour. The same amber wash as <c>WlMarkAmber</c>, held here as a frozen
    /// brush because a highlight is content: it must survive a document that is not (yet)
    /// inside a window that can resolve theme resources.
    /// </summary>
    public static readonly SolidColorBrush HighlightBrush = CreateHighlight();

    private static SolidColorBrush CreateHighlight()
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x38, 0xEC, 0x6C, 0x08));
        brush.Freeze();
        return brush;
    }

    // ── Blocks → document ────────────────────────────────────────────────────

    public static void Load(FlowDocument document, IReadOnlyList<NoteBlock> blocks, INoteEmbedFactory embeds)
    {
        document.Blocks.Clear();

        var index = 0;
        while (index < blocks.Count)
        {
            var block = blocks[index];
            if (block.Kind is NoteBlockKind.Bullet or NoteBlockKind.Numbered)
            {
                // A run of list lines of the same kind is one list, nested by indent.
                var end = index;
                while (end < blocks.Count && blocks[end].Kind == block.Kind)
                {
                    end++;
                }

                document.Blocks.Add(BuildList(blocks, index, end, block.Kind, 0, out _));
                index = end;
                continue;
            }

            document.Blocks.Add(BuildBlock(block, embeds));
            index++;
        }

        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(BuildParagraph(NoteBlock.Paragraph(string.Empty), embeds));
        }
    }

    /// <summary>Builds lines [start, end) at <paramref name="level"/>, recursing into deeper ones.</summary>
    private static List BuildList(IReadOnlyList<NoteBlock> blocks, int start, int end, NoteBlockKind kind, int level, out int consumed)
    {
        var list = NewList(kind);
        var index = start;

        while (index < end)
        {
            var block = blocks[index];
            var indent = Math.Max(0, block.Indent);

            if (indent > level && list.ListItems.LastListItem is { } parent)
            {
                // Deeper lines nest inside the previous item.
                parent.Blocks.Add(BuildList(blocks, index, end, kind, level + 1, out var used));
                index += used;
                continue;
            }

            if (indent < level)
            {
                break;
            }

            var item = new ListItem();
            item.Blocks.Add(BuildTextParagraph(block));
            list.ListItems.Add(item);
            index++;
        }

        consumed = index - start;
        return list;
    }

    public static List NewList(NoteBlockKind kind) => new()
    {
        MarkerStyle = kind == NoteBlockKind.Numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
        Margin = new Thickness(0, 0, 0, 8),
        Padding = new Thickness(26, 0, 0, 0)
    };

    public static Block BuildBlock(NoteBlock block, INoteEmbedFactory embeds) => block.Kind switch
    {
        NoteBlockKind.Image => Embedded(block, embeds.CreateImage(block)),
        NoteBlockKind.Sketch => Embedded(block, embeds.CreateSketch(block)),
        NoteBlockKind.Divider => Embedded(block, BuildDivider()),
        _ => BuildParagraph(block, embeds)
    };

    private static BlockUIContainer Embedded(NoteBlock block, UIElement child)
    {
        var container = new BlockUIContainer(child)
        {
            Tag = new NoteBlockTag
            {
                Kind = block.Kind,
                AssetId = block.AssetId,
                Width = block.Width,
                BookmarkId = block.BookmarkId,
                BookmarkName = block.BookmarkName
            },
            Margin = new Thickness(0, 4, 0, 10)
        };
        ApplyBookmarkMarker(container);
        return container;
    }

    public static UIElement BuildDivider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8), SnapsToDevicePixels = true };
        line.SetResourceReference(Border.BackgroundProperty, "WlLineStrong");
        return line;
    }

    public static Paragraph BuildParagraph(NoteBlock block, INoteEmbedFactory embeds)
    {
        var paragraph = BuildTextParagraph(block);
        if (block.Kind == NoteBlockKind.Check)
        {
            paragraph.Inlines.InsertBefore(paragraph.Inlines.FirstInline, CheckInline(embeds.CreateCheck(block.IsChecked)));
            ApplyCheckStyle(paragraph, block.IsChecked);
        }

        return paragraph;
    }

    /// <summary>The tick box of a checklist line, positioned to sit on the text baseline.</summary>
    public static InlineUIContainer CheckInline(CheckBox box) => new(box)
    {
        BaselineAlignment = BaselineAlignment.Center
    };

    private static Paragraph BuildTextParagraph(NoteBlock block)
    {
        var paragraph = new Paragraph
        {
            Tag = new NoteBlockTag
            {
                Kind = block.Kind,
                BookmarkId = block.BookmarkId,
                BookmarkName = block.BookmarkName
            }
        };

        foreach (var span in block.Spans)
        {
            AppendSpan(paragraph.Inlines, span);
        }

        // An empty paragraph needs a run to give the caret somewhere to stand.
        if (paragraph.Inlines.Count == 0)
        {
            paragraph.Inlines.Add(new Run(string.Empty));
        }

        ApplyKindStyle(paragraph, block.Kind);
        ApplyBookmarkMarker(paragraph);
        return paragraph;
    }

    private static void AppendSpan(InlineCollection inlines, NoteSpan span)
    {
        var lines = span.Text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                inlines.Add(new LineBreak());
            }

            if (lines[index].Length == 0)
            {
                continue;
            }

            var run = new Run(lines[index]);
            if (span.Bold)
            {
                run.FontWeight = FontWeights.Bold;
            }

            if (span.Italic)
            {
                run.FontStyle = FontStyles.Italic;
            }

            if (span.Underline || span.Strike)
            {
                var decorations = new TextDecorationCollection();
                if (span.Underline)
                {
                    decorations.Add(TextDecorations.Underline);
                }

                if (span.Strike)
                {
                    decorations.Add(TextDecorations.Strikethrough);
                }

                run.TextDecorations = decorations;
            }

            if (span.Highlight)
            {
                run.Background = HighlightBrush;
            }

            inlines.Add(run);
        }
    }

    /// <summary>The look of each paragraph kind. One place, so the editor's style menu and loading agree.</summary>
    public static void ApplyKindStyle(Paragraph paragraph, NoteBlockKind kind)
    {
        paragraph.ClearValue(TextElement.FontSizeProperty);
        paragraph.ClearValue(TextElement.FontWeightProperty);
        paragraph.ClearValue(TextElement.FontStyleProperty);
        paragraph.ClearValue(TextElement.ForegroundProperty);
        paragraph.ClearValue(Block.MarginProperty);
        paragraph.ClearValue(Block.PaddingProperty);
        paragraph.ClearValue(Block.BorderThicknessProperty);
        paragraph.ClearValue(Block.BorderBrushProperty);

        paragraph.LineHeight = double.NaN;

        switch (kind)
        {
            case NoteBlockKind.Heading1:
                paragraph.FontSize = 25;
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new Thickness(0, 18, 0, 8);
                break;
            case NoteBlockKind.Heading2:
                paragraph.FontSize = 20;
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new Thickness(0, 14, 0, 6);
                break;
            case NoteBlockKind.Heading3:
                paragraph.FontSize = 16.5;
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new Thickness(0, 12, 0, 4);
                break;
            case NoteBlockKind.Quote:
                paragraph.FontSize = BodyFontSize;
                paragraph.FontStyle = FontStyles.Italic;
                paragraph.Margin = new Thickness(0, 4, 0, 10);
                paragraph.Padding = new Thickness(14, 2, 0, 2);
                paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
                paragraph.SetResourceReference(Block.BorderBrushProperty, "WlLineStrong");
                paragraph.SetResourceReference(TextElement.ForegroundProperty, "WlTextSecondary");
                break;
            default:
                paragraph.FontSize = BodyFontSize;
                paragraph.Margin = new Thickness(0, 0, 0, kind == NoteBlockKind.Check ? 4 : 8);
                break;
        }
    }

    public static void ApplyCheckStyle(Paragraph paragraph, bool isChecked)
    {
        if (isChecked)
        {
            paragraph.SetResourceReference(TextElement.ForegroundProperty, "WlTextMuted");
        }
        else
        {
            paragraph.ClearValue(TextElement.ForegroundProperty);
        }
    }

    /// <summary>
    /// A bookmarked block carries a brand-coloured rule on its left.
    /// </summary>
    /// <remarks>
    /// A rule rather than an icon in the text: an icon would be an inline the user can
    /// select, delete or type around, and the bookmark would then live or die with it.
    /// </remarks>
    public static void ApplyBookmarkMarker(Block block)
    {
        var tag = block.Tag as NoteBlockTag;
        var marked = !string.IsNullOrEmpty(tag?.BookmarkId);

        if (tag?.Kind == NoteBlockKind.Quote && block is Paragraph quote)
        {
            if (marked)
            {
                quote.SetResourceReference(Block.BorderBrushProperty, "WlBrand");
            }
            else
            {
                quote.SetResourceReference(Block.BorderBrushProperty, "WlLineStrong");
            }

            return;
        }

        if (marked)
        {
            block.BorderThickness = new Thickness(3, 0, 0, 0);
            block.Padding = new Thickness(12, block.Padding.Top, block.Padding.Right, block.Padding.Bottom);
            block.SetResourceReference(Block.BorderBrushProperty, "WlBrand");
        }
        else
        {
            block.ClearValue(Block.BorderThicknessProperty);
            block.ClearValue(Block.BorderBrushProperty);
            block.Padding = new Thickness(0, block.Padding.Top, block.Padding.Right, block.Padding.Bottom);
        }
    }

    // ── Document → blocks ────────────────────────────────────────────────────

    public static List<NoteBlock> Read(FlowDocument document)
    {
        var blocks = new List<NoteBlock>();
        ReadBlocks(document.Blocks, blocks, listKind: null, indent: 0);

        // Trailing empty paragraphs are typing room, not content.
        while (blocks.Count > 1 && blocks[^1].Kind == NoteBlockKind.Paragraph && blocks[^1].Spans.Count == 0
               && string.IsNullOrEmpty(blocks[^1].BookmarkId))
        {
            blocks.RemoveAt(blocks.Count - 1);
        }

        return blocks;
    }

    private static void ReadBlocks(BlockCollection source, List<NoteBlock> target, NoteBlockKind? listKind, int indent)
    {
        foreach (var block in source)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    target.Add(ReadParagraph(paragraph, listKind, indent));
                    break;

                case List list:
                    var kind = IsNumbered(list.MarkerStyle) ? NoteBlockKind.Numbered : NoteBlockKind.Bullet;
                    var level = listKind is null ? 0 : indent + 1;
                    foreach (var item in list.ListItems)
                    {
                        ReadBlocks(item.Blocks, target, kind, level);
                    }

                    break;

                case BlockUIContainer container when ReadEmbedded(container) is { } embedded:
                    target.Add(embedded);
                    break;

                case Section section:
                    ReadBlocks(section.Blocks, target, listKind, indent);
                    break;

                case Table table:
                    // Flattened, never dropped: a pasted table keeps its words.
                    foreach (var cell in table.RowGroups.SelectMany(group => group.Rows).SelectMany(row => row.Cells))
                    {
                        ReadBlocks(cell.Blocks, target, listKind, indent);
                    }

                    break;
            }
        }
    }

    private static bool IsNumbered(TextMarkerStyle style) => style is TextMarkerStyle.Decimal
        or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin
        or TextMarkerStyle.LowerRoman or TextMarkerStyle.UpperRoman;

    private static NoteBlock ReadParagraph(Paragraph paragraph, NoteBlockKind? listKind, int indent)
    {
        var tag = paragraph.Tag as NoteBlockTag;
        var check = FindCheck(paragraph);

        var kind = listKind
                   ?? (check is not null ? NoteBlockKind.Check : (NoteBlockKind?)null)
                   ?? (tag?.Kind is { } tagged && tagged is not (NoteBlockKind.Check or NoteBlockKind.Bullet or NoteBlockKind.Numbered)
                       ? tagged
                       : NoteBlockKind.Paragraph);

        var block = new NoteBlock
        {
            Kind = kind,
            Indent = listKind is null ? 0 : indent,
            IsChecked = check?.IsChecked == true,
            BookmarkId = tag?.BookmarkId,
            BookmarkName = tag?.BookmarkName
        };

        var baseline = paragraph.FontWeight.ToOpenTypeWeight();
        var spans = new List<NoteSpan>();
        ReadInlines(paragraph.Inlines, spans, baseline, paragraph.FontStyle);
        block.Spans = NoteContent.Normalize(spans);

        // A line break at the very end is an artefact of editing, not content.
        if (block.Spans.Count > 0 && block.Spans[^1].Text.EndsWith('\n'))
        {
            block.Spans[^1].Text = block.Spans[^1].Text.TrimEnd('\n');
            block.Spans = NoteContent.Normalize(block.Spans);
        }

        return block;
    }

    private static void ReadInlines(InlineCollection inlines, List<NoteSpan> target, int baselineWeight, FontStyle baselineStyle)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run when run.Text.Length > 0:
                    target.Add(new NoteSpan
                    {
                        Text = run.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'),
                        Bold = run.FontWeight.ToOpenTypeWeight() >= 600 && baselineWeight < 600,
                        Italic = run.FontStyle != FontStyles.Normal && baselineStyle == FontStyles.Normal,
                        Underline = HasDecoration(run, TextDecorationLocation.Underline),
                        Strike = HasDecoration(run, TextDecorationLocation.Strikethrough),
                        Highlight = IsHighlighted(run)
                    });
                    break;

                case LineBreak:
                    target.Add(new NoteSpan { Text = "\n" });
                    break;

                case Span span:
                    ReadInlines(span.Inlines, target, baselineWeight, baselineStyle);
                    break;
            }
        }
    }

    private static bool HasDecoration(Inline inline, TextDecorationLocation location)
    {
        for (DependencyObject? current = inline; current is Inline element; current = element.Parent)
        {
            if (element.TextDecorations?.Any(decoration => decoration.Location == location) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHighlighted(Inline inline)
    {
        for (DependencyObject? current = inline; current is Inline element; current = element.Parent)
        {
            if (element.Background is SolidColorBrush { Color.A: > 0 } or LinearGradientBrush)
            {
                return true;
            }
        }

        return false;
    }

    public static CheckBox? FindCheck(Paragraph paragraph) =>
        paragraph.Inlines.FirstInline is InlineUIContainer { Child: CheckBox box } ? box : null;

    private static NoteBlock? ReadEmbedded(BlockUIContainer container)
    {
        var tag = container.Tag as NoteBlockTag;
        switch (container.Child)
        {
            case NoteSketchSurface sketch:
                return new NoteBlock
                {
                    Kind = NoteBlockKind.Sketch,
                    Ink = sketch.SaveInk(),
                    Height = Math.Round(sketch.SurfaceHeight),
                    BookmarkId = tag?.BookmarkId,
                    BookmarkName = tag?.BookmarkName
                };

            case not null when tag?.Kind == NoteBlockKind.Image && tag.AssetId is not null:
                return new NoteBlock
                {
                    Kind = NoteBlockKind.Image,
                    AssetId = tag.AssetId,
                    Width = tag.Width,
                    BookmarkId = tag.BookmarkId,
                    BookmarkName = tag.BookmarkName
                };

            case not null when tag?.Kind == NoteBlockKind.Divider:
                return new NoteBlock { Kind = NoteBlockKind.Divider };

            default:
                return null;
        }
    }

    // ── Helpers shared with the editor ───────────────────────────────────────

    /// <summary>Every text paragraph of the document, in reading order, with list items included.</summary>
    public static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    yield return paragraph;
                    break;
                case List list:
                    foreach (var paragraph in list.ListItems.SelectMany(item => Paragraphs(item.Blocks)))
                    {
                        yield return paragraph;
                    }

                    break;
                case Section section:
                    foreach (var paragraph in Paragraphs(section.Blocks))
                    {
                        yield return paragraph;
                    }

                    break;
            }
        }
    }

    /// <summary>Every block of the document that can carry a bookmark, in reading order.</summary>
    public static IEnumerable<Block> BookmarkableBlocks(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph or BlockUIContainer:
                    yield return block;
                    break;
                case List list:
                    foreach (var inner in list.ListItems.SelectMany(item => BookmarkableBlocks(item.Blocks)))
                    {
                        yield return inner;
                    }

                    break;
                case Section section:
                    foreach (var inner in BookmarkableBlocks(section.Blocks))
                    {
                        yield return inner;
                    }

                    break;
            }
        }
    }

    /// <summary>Where the editable text of a paragraph starts: after its tick box, if it has one.</summary>
    public static TextPointer TextStart(Paragraph paragraph) =>
        FindCheck(paragraph) is not null && paragraph.Inlines.FirstInline is { } box
            ? box.ElementEnd
            : paragraph.ContentStart;

    /// <summary>The plain text of a paragraph, without its tick box.</summary>
    public static string TextOf(Paragraph paragraph) => new TextRange(TextStart(paragraph), paragraph.ContentEnd).Text;

    internal static Brush? Resolve(FrameworkElement scope, string key) => scope.TryFindResource(key) as Brush;
}
