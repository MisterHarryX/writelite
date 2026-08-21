namespace WriteLite.Documents.Model;

/// <summary>
/// WriteLite's format-independent document model.
/// </summary>
/// <remarks>
/// Every importer produces one of these and every exporter consumes one, so no
/// format ever talks to another format directly. The shape is deliberately the
/// intersection of what DOCX, ODT and a reconstructed PDF can all express — rich
/// enough to survive a round trip through any of them, small enough that a new
/// format only has to answer a bounded set of questions.
///
/// Measurements are points throughout (1 pt = 1/72 in). DOCX stores twips, ODT
/// stores centimetres and PDF stores points; converting at the boundary keeps
/// exactly one unit inside the model.
/// </remarks>
public sealed class WlDocument
{
    public DocumentMetadata Metadata { get; set; } = new();

    public List<DocumentSection> Sections { get; } = [];

    public DocumentResources Resources { get; } = new();

    /// <summary>Every block in the document, in reading order, across sections.</summary>
    public IEnumerable<BlockElement> Blocks => Sections.SelectMany(section => section.Blocks);

    public static WlDocument Empty()
    {
        var document = new WlDocument();
        document.Sections.Add(new DocumentSection());
        return document;
    }

    /// <summary>The section blocks are appended to, creating one when the document has none.</summary>
    public DocumentSection CurrentSection()
    {
        if (Sections.Count == 0)
        {
            Sections.Add(new DocumentSection());
        }

        return Sections[^1];
    }

    /// <summary>Plain-text projection, used for word counts, TXT export and analysis.</summary>
    public string ToPlainText()
    {
        var builder = new System.Text.StringBuilder();
        var first = true;

        foreach (var block in Blocks)
        {
            AppendBlockText(builder, block, ref first);
        }

        return builder.ToString();
    }

    private static void AppendBlockText(System.Text.StringBuilder builder, BlockElement block, ref bool first)
    {
        switch (block)
        {
            case DocumentParagraph paragraph:
                if (!first)
                {
                    builder.Append('\n');
                }

                builder.Append(paragraph.ToPlainText());
                first = false;
                break;

            case DocumentTable table:
                foreach (var row in table.Rows)
                {
                    if (!first)
                    {
                        builder.Append('\n');
                    }

                    builder.Append(string.Join('\t', row.Cells.Select(cell => cell.ToPlainText())));
                    first = false;
                }

                break;

            case PageBreak:
                if (!first)
                {
                    builder.Append('\n');
                }

                first = false;
                break;
        }
    }
}

public sealed class DocumentMetadata
{
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Subject { get; set; }
    public string? Language { get; set; }
    public DateTimeOffset? Created { get; set; }
    public DateTimeOffset? Modified { get; set; }

    /// <summary>Where this document came from, when it came from a file.</summary>
    public string? SourcePath { get; set; }

    public DocumentFormat SourceFormat { get; set; } = DocumentFormat.Unknown;
}

/// <summary>A run of blocks sharing one page geometry.</summary>
public sealed class DocumentSection
{
    public List<BlockElement> Blocks { get; } = [];

    public PageSetup Page { get; set; } = PageSetup.A4;
}

/// <summary>Page geometry in points.</summary>
public sealed record PageSetup(
    double WidthPt,
    double HeightPt,
    double MarginLeftPt,
    double MarginRightPt,
    double MarginTopPt,
    double MarginBottomPt)
{
    public static readonly PageSetup A4 = new(595.28, 841.89, 56.7, 56.7, 56.7, 56.7);

    public double ContentWidthPt => Math.Max(1, WidthPt - MarginLeftPt - MarginRightPt);

    public double ContentHeightPt => Math.Max(1, HeightPt - MarginTopPt - MarginBottomPt);
}

/// <summary>Images and other binary payloads, addressed by id from the blocks that use them.</summary>
public sealed class DocumentResources
{
    private readonly Dictionary<string, DocumentImage> _images = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, DocumentImage> Images => _images;

    public string Add(DocumentImage image)
    {
        var id = $"img{_images.Count + 1:D4}";
        _images[id] = image;
        return id;
    }

    public DocumentImage? Get(string id) => _images.TryGetValue(id, out var image) ? image : null;
}

public sealed record DocumentImage(byte[] Data, string ContentType, double WidthPt, double HeightPt);

// ── Blocks ───────────────────────────────────────────────────────────────────

public abstract class BlockElement;

/// <summary>
/// A paragraph, which is also how headings and list items are represented.
/// </summary>
/// <remarks>
/// Headings are paragraphs with an <see cref="OutlineLevel"/> rather than a separate
/// node type, and list items are paragraphs carrying <see cref="List"/>. Both choices
/// mirror how DOCX and ODT actually store them, so importing does not have to invent
/// structure the source never had, and exporting does not have to flatten it back.
/// </remarks>
public sealed class DocumentParagraph : BlockElement
{
    public List<InlineElement> Inlines { get; } = [];

    public ParagraphFormatting Format { get; set; } = ParagraphFormatting.Default;

    /// <summary>1–6 for Heading 1–6; null for body text.</summary>
    public int? OutlineLevel { get; set; }

    public ListInfo? List { get; set; }

    /// <summary>The source style name, kept so a round trip can restore it.</summary>
    public string? StyleName { get; set; }

    public bool IsHeading => OutlineLevel is >= 1;

    public string ToPlainText()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var inline in Inlines)
        {
            inline.AppendPlainText(builder);
        }

        return builder.ToString();
    }

    public static DocumentParagraph FromText(string text, ParagraphFormatting? format = null, RunFormatting? run = null)
    {
        var paragraph = new DocumentParagraph { Format = format ?? ParagraphFormatting.Default };
        if (text.Length > 0)
        {
            paragraph.Inlines.Add(new TextRun(text, run ?? RunFormatting.Default));
        }

        return paragraph;
    }
}

public sealed record ListInfo(ListKind Kind, int Level, string? Marker = null)
{
    public static ListInfo Bullet(int level = 0) => new(ListKind.Bullet, level);

    public static ListInfo Numbered(int level = 0) => new(ListKind.Numbered, level);
}

public enum ListKind
{
    Bullet,
    Numbered
}

public sealed class PageBreak : BlockElement;

public sealed class DocumentTable : BlockElement
{
    public List<TableRow> Rows { get; } = [];

    /// <summary>Relative column widths, normalised to sum to 1. Empty means "distribute evenly".</summary>
    public List<double> ColumnWidths { get; } = [];

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(row => row.Cells.Count);
}

public sealed class TableRow
{
    public List<TableCell> Cells { get; } = [];

    public bool IsHeader { get; set; }
}

public sealed class TableCell
{
    public List<BlockElement> Blocks { get; } = [];

    public int ColumnSpan { get; set; } = 1;

    public string ToPlainText() =>
        string.Join(
            " ",
            Blocks.OfType<DocumentParagraph>()
                .Select(paragraph => paragraph.ToPlainText())
                .Where(text => text.Length > 0));
}

// ── Inlines ──────────────────────────────────────────────────────────────────

public abstract class InlineElement
{
    public abstract void AppendPlainText(System.Text.StringBuilder builder);
}

public sealed class TextRun(string text, RunFormatting format) : InlineElement
{
    public string Text { get; set; } = text;

    public RunFormatting Format { get; set; } = format;

    public TextRun(string text) : this(text, RunFormatting.Default)
    {
    }

    public override void AppendPlainText(System.Text.StringBuilder builder) => builder.Append(Text);
}

/// <summary>A hard line break inside a paragraph, as distinct from a paragraph break.</summary>
public sealed class LineBreakRun : InlineElement
{
    public override void AppendPlainText(System.Text.StringBuilder builder) => builder.Append('\n');
}

public sealed class HyperlinkRun(string? target) : InlineElement
{
    public string? Target { get; set; } = target;

    public List<TextRun> Runs { get; } = [];

    public string Text => string.Concat(Runs.Select(run => run.Text));

    public override void AppendPlainText(System.Text.StringBuilder builder)
    {
        foreach (var run in Runs)
        {
            builder.Append(run.Text);
        }
    }
}

public sealed class ImageRun(string resourceId) : InlineElement
{
    public string ResourceId { get; } = resourceId;

    public double WidthPt { get; set; }

    public double HeightPt { get; set; }

    /// <summary>Alt text, which is also what a text-only export can fall back to.</summary>
    public string? Description { get; set; }

    public override void AppendPlainText(System.Text.StringBuilder builder)
    {
        if (!string.IsNullOrWhiteSpace(Description))
        {
            builder.Append(Description);
        }
    }
}

// ── Formatting ───────────────────────────────────────────────────────────────

public enum TextAlign
{
    Left,
    Center,
    Right,
    Justify
}

/// <summary>Character-level formatting. A null member means "inherit".</summary>
public sealed record RunFormatting(
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Strikethrough = false,
    string? FontFamily = null,
    double? FontSizePt = null,
    string? ColorHex = null,
    string? HighlightHex = null)
{
    public static readonly RunFormatting Default = new();

    public bool IsDefault =>
        !Bold && !Italic && !Underline && !Strikethrough
        && FontFamily is null && FontSizePt is null && ColorHex is null && HighlightHex is null;

    /// <summary>Fills unset members from <paramref name="fallback"/>; explicit members win.</summary>
    public RunFormatting InheritFrom(RunFormatting fallback) => new(
        Bold || fallback.Bold,
        Italic || fallback.Italic,
        Underline || fallback.Underline,
        Strikethrough || fallback.Strikethrough,
        FontFamily ?? fallback.FontFamily,
        FontSizePt ?? fallback.FontSizePt,
        ColorHex ?? fallback.ColorHex,
        HighlightHex ?? fallback.HighlightHex);
}

/// <summary>Paragraph-level formatting. Points throughout; line spacing is a multiplier.</summary>
public sealed record ParagraphFormatting(
    TextAlign Alignment = TextAlign.Left,
    double LineSpacing = 1.0,
    double SpaceBeforePt = 0,
    double SpaceAfterPt = 0,
    double IndentLeftPt = 0,
    double IndentRightPt = 0,
    double FirstLineIndentPt = 0)
{
    public static readonly ParagraphFormatting Default = new();
}
