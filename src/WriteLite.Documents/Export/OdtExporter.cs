using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using WriteLite.Documents.Model;
using WriteLite.Documents.OpenDocument;

namespace WriteLite.Documents.Export;

/// <summary>
/// WriteLite document → OpenDocument Text.
/// </summary>
/// <remarks>
/// The package is assembled by hand because no maintained .NET ODF library exists;
/// the format is a ZIP of four XML parts, which is far less risk than depending on
/// an abandoned one.
///
/// Two details are load-bearing for readers other than LibreOffice: <c>mimetype</c>
/// must be the first entry and stored uncompressed, and every distinct piece of
/// direct formatting must be declared as an automatic style before it is referenced.
/// </remarks>
public sealed class OdtExporter : IDocumentExporter
{
    public DocumentFormat Format => DocumentFormat.Odt;

    public Task<IReadOnlyList<DocumentWarning>> ExportAsync(
        WlDocument document,
        Stream stream,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<DocumentWarning>>(
            () => Export(document, stream, progress, cancellationToken),
            cancellationToken);

    private static List<DocumentWarning> Export(
        WlDocument document,
        Stream stream,
        IProgress<DocumentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var warnings = new List<DocumentWarning>();
        progress?.Report(new DocumentProgress("Подготовка документа", null));

        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        WriteMimeType(archive);
        WriteManifest(archive);
        WriteMeta(archive, document.Metadata);
        WriteStyles(archive, document);
        WriteContent(archive, document, warnings, progress, cancellationToken);

        progress?.Report(new DocumentProgress("Готово", 1));
        return warnings;
    }

    private static void WriteMimeType(ZipArchive archive)
    {
        // Stored, not deflated, and written first: readers sniff the format by
        // reading these bytes at a fixed offset without inflating anything.
        var entry = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using var payload = entry.Open();
        var bytes = Encoding.ASCII.GetBytes(Odf.TextMimeType);
        payload.Write(bytes, 0, bytes.Length);
    }

    private static void WriteManifest(ZipArchive archive)
    {
        XNamespace manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
        var root = new XElement(manifest + "manifest",
            new XAttribute(XNamespace.Xmlns + "manifest", manifest.NamespaceName),
            new XAttribute(manifest + "version", "1.3"),
            new XElement(manifest + "file-entry",
                new XAttribute(manifest + "full-path", "/"),
                new XAttribute(manifest + "media-type", Odf.TextMimeType)));

        foreach (var part in new[] { "content.xml", "styles.xml", "meta.xml" })
        {
            root.Add(new XElement(manifest + "file-entry",
                new XAttribute(manifest + "full-path", part),
                new XAttribute(manifest + "media-type", "text/xml")));
        }

        WriteXml(archive, "META-INF/manifest.xml", new XDocument(root));
    }

    private static void WriteMeta(ZipArchive archive, DocumentMetadata metadata)
    {
        var meta = new XElement(Odf.Office + "meta");

        if (!string.IsNullOrWhiteSpace(metadata.Title))
        {
            meta.Add(new XElement(Odf.Dc + "title", metadata.Title));
        }

        if (!string.IsNullOrWhiteSpace(metadata.Author))
        {
            meta.Add(new XElement(Odf.Meta + "initial-creator", metadata.Author));
        }

        meta.Add(new XElement(Odf.Meta + "generator", "WriteLite"));
        meta.Add(new XElement(Odf.Dc + "date", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)));

        var root = new XElement(Odf.Office + "document-meta",
            NamespaceDeclarations(),
            new XAttribute(Odf.Office + "version", "1.3"),
            meta);

        WriteXml(archive, "meta.xml", new XDocument(root));
    }

    private static void WriteStyles(ZipArchive archive, WlDocument document)
    {
        var page = document.Sections.Count > 0 ? document.Sections[0].Page : PageSetup.A4;

        var styles = new XElement(Odf.Office + "styles",
            new XElement(Odf.Style + "style",
                new XAttribute(Odf.Style + "name", "Standard"),
                new XAttribute(Odf.Style + "family", "paragraph"),
                new XElement(Odf.Style + "text-properties",
                    new XAttribute(Odf.Style + "font-name", "Calibri"),
                    new XAttribute(Odf.Fo + "font-size", "11pt"))));

        for (var level = 1; level <= 6; level++)
        {
            var size = new[] { 20, 16, 14, 12, 11, 10 }[level - 1];
            styles.Add(new XElement(Odf.Style + "style",
                new XAttribute(Odf.Style + "name", $"Heading_20_{level}"),
                new XAttribute(Odf.Style + "display-name", $"Heading {level}"),
                new XAttribute(Odf.Style + "family", "paragraph"),
                new XAttribute(Odf.Style + "parent-style-name", "Standard"),
                new XAttribute(Odf.Style + "default-outline-level", level),
                new XElement(Odf.Style + "paragraph-properties",
                    new XAttribute(Odf.Fo + "margin-top", Odf.PointsToPoints((7 - level) * 3)),
                    new XAttribute(Odf.Fo + "margin-bottom", "6pt"),
                    new XAttribute(Odf.Fo + "keep-with-next", "always")),
                new XElement(Odf.Style + "text-properties",
                    new XAttribute(Odf.Fo + "font-weight", "bold"),
                    new XAttribute(Odf.Fo + "font-size", $"{size}pt"))));
        }

        var automatic = new XElement(Odf.Office + "automatic-styles",
            new XElement(Odf.Style + "page-layout",
                new XAttribute(Odf.Style + "name", "PL1"),
                new XElement(Odf.Style + "page-layout-properties",
                    new XAttribute(Odf.Fo + "page-width", Odf.PointsToCentimetres(page.WidthPt)),
                    new XAttribute(Odf.Fo + "page-height", Odf.PointsToCentimetres(page.HeightPt)),
                    new XAttribute(Odf.Fo + "margin-left", Odf.PointsToCentimetres(page.MarginLeftPt)),
                    new XAttribute(Odf.Fo + "margin-right", Odf.PointsToCentimetres(page.MarginRightPt)),
                    new XAttribute(Odf.Fo + "margin-top", Odf.PointsToCentimetres(page.MarginTopPt)),
                    new XAttribute(Odf.Fo + "margin-bottom", Odf.PointsToCentimetres(page.MarginBottomPt)))));

        var master = new XElement(Odf.Office + "master-styles",
            new XElement(Odf.Style + "master-page",
                new XAttribute(Odf.Style + "name", "Standard"),
                new XAttribute(Odf.Style + "page-layout-name", "PL1")));

        var root = new XElement(Odf.Office + "document-styles",
            NamespaceDeclarations(),
            new XAttribute(Odf.Office + "version", "1.3"),
            styles,
            automatic,
            master);

        WriteXml(archive, "styles.xml", new XDocument(root));
    }

    private static void WriteContent(
        ZipArchive archive,
        WlDocument document,
        List<DocumentWarning> warnings,
        IProgress<DocumentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var automaticStyles = new OdfAutomaticStyleTable();
        var body = new XElement(Odf.Office + "text");

        var blocks = document.Blocks.ToArray();
        var index = 0;

        while (index < blocks.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index % 128 == 0)
            {
                progress?.Report(DocumentProgress.Of("Запись абзацев", index, blocks.Length));
            }

            switch (blocks[index])
            {
                case DocumentParagraph paragraph when paragraph.List is not null:
                    // Consecutive items at the same level belong in one text:list, or
                    // a numbered list restarts at 1 on every item.
                    var run = CollectListRun(blocks, index);
                    body.Add(BuildList(blocks, index, run, automaticStyles));
                    index += run;
                    continue;

                case DocumentParagraph paragraph:
                    body.Add(BuildParagraph(paragraph, automaticStyles));
                    break;

                case DocumentTable table:
                    body.Add(BuildTable(table, automaticStyles));
                    break;

                case PageBreak:
                    // ODF has no standalone page break: it is a paragraph whose style
                    // carries fo:break-before.
                    body.Add(new XElement(Odf.Text + "p",
                        new XAttribute(Odf.Text + "style-name", automaticStyles.PageBreakStyle())));
                    break;
            }

            index++;
        }

        if (document.Resources.Images.Count > 0)
        {
            warnings.Add(new DocumentWarning("odt-images-dropped", "Изображения не переносятся в этот файл."));
        }

        var root = new XElement(Odf.Office + "document-content",
            NamespaceDeclarations(),
            new XAttribute(Odf.Office + "version", "1.3"),
            automaticStyles.ToElement(),
            new XElement(Odf.Office + "body", body));

        WriteXml(archive, "content.xml", new XDocument(root));
    }

    private static int CollectListRun(BlockElement[] blocks, int start)
    {
        var kind = ((DocumentParagraph)blocks[start]).List!.Kind;
        var count = 0;

        while (start + count < blocks.Length
               && blocks[start + count] is DocumentParagraph paragraph
               && paragraph.List is { } list
               && list.Kind == kind)
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Builds one <c>text:list</c>, nesting deeper levels inside their parent item.
    /// </summary>
    private static XElement BuildList(BlockElement[] blocks, int start, int count, OdfAutomaticStyleTable styles)
    {
        var baseLevel = ((DocumentParagraph)blocks[start]).List!.Level;
        var kind = ((DocumentParagraph)blocks[start]).List!.Kind;

        var list = new XElement(Odf.Text + "list",
            new XAttribute(Odf.Text + "style-name", styles.ListStyle(kind)));

        var index = start;
        var end = start + count;

        while (index < end)
        {
            var paragraph = (DocumentParagraph)blocks[index];
            var level = paragraph.List!.Level;

            if (level > baseLevel)
            {
                // Deeper items belong inside the item that came before them.
                var nestedCount = 0;
                while (index + nestedCount < end
                       && ((DocumentParagraph)blocks[index + nestedCount]).List!.Level > baseLevel)
                {
                    nestedCount++;
                }

                var nested = BuildList(blocks, index, nestedCount, styles);
                var host = list.Elements(Odf.Text + "list-item").LastOrDefault();
                if (host is null)
                {
                    host = new XElement(Odf.Text + "list-item");
                    list.Add(host);
                }

                host.Add(nested);
                index += nestedCount;
                continue;
            }

            list.Add(new XElement(Odf.Text + "list-item", BuildParagraph(paragraph, styles)));
            index++;
        }

        return list;
    }

    private static XElement BuildParagraph(DocumentParagraph paragraph, OdfAutomaticStyleTable styles)
    {
        var element = paragraph.IsHeading
            ? new XElement(Odf.Text + "h", new XAttribute(Odf.Text + "outline-level", paragraph.OutlineLevel!.Value))
            : new XElement(Odf.Text + "p");

        var parent = paragraph.IsHeading ? $"Heading_20_{paragraph.OutlineLevel}" : "Standard";
        element.Add(new XAttribute(Odf.Text + "style-name", styles.ParagraphStyle(paragraph.Format, parent)));

        foreach (var inline in paragraph.Inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    AppendText(element, run.Text, run.Format, styles);
                    break;

                case LineBreakRun:
                    element.Add(new XElement(Odf.Text + "line-break"));
                    break;

                case HyperlinkRun link:
                    var anchor = new XElement(Odf.Text + "a",
                        new XAttribute(Odf.Xlink + "href", link.Target ?? string.Empty),
                        new XAttribute(Odf.Xlink + "type", "simple"));
                    foreach (var run in link.Runs)
                    {
                        AppendText(anchor, run.Text, run.Format, styles);
                    }

                    element.Add(anchor);
                    break;

                case ImageRun image:
                    if (!string.IsNullOrWhiteSpace(image.Description))
                    {
                        element.Add(new XText($"[{image.Description}]"));
                    }

                    break;
            }
        }

        return element;
    }

    /// <summary>
    /// Writes text, encoding tabs and space runs the way ODF requires.
    /// </summary>
    /// <remarks>
    /// XML collapses whitespace, so consecutive spaces have to become
    /// <c>text:s</c> elements and tabs <c>text:tab</c>. Writing them literally
    /// loses every indent a writer typed.
    /// </remarks>
    private static void AppendText(XElement host, string text, RunFormatting format, OdfAutomaticStyleTable styles)
    {
        if (text.Length == 0)
        {
            return;
        }

        var target = host;
        if (!format.IsDefault)
        {
            target = new XElement(Odf.Text + "span",
                new XAttribute(Odf.Text + "style-name", styles.RunStyle(format)));
            host.Add(target);
        }

        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length > 0)
            {
                target.Add(new XText(buffer.ToString()));
                buffer.Clear();
            }
        }

        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];

            if (c == '\t')
            {
                Flush();
                target.Add(new XElement(Odf.Text + "tab"));
                continue;
            }

            if (c == ' ' && index + 1 < text.Length && text[index + 1] == ' ')
            {
                var run = 0;
                while (index + run < text.Length && text[index + run] == ' ')
                {
                    run++;
                }

                buffer.Append(' ');
                Flush();
                target.Add(new XElement(Odf.Text + "s", new XAttribute(Odf.Text + "c", run - 1)));
                index += run - 1;
                continue;
            }

            buffer.Append(c);
        }

        Flush();
    }

    private static XElement BuildTable(DocumentTable table, OdfAutomaticStyleTable styles)
    {
        var element = new XElement(Odf.Table + "table",
            new XAttribute(Odf.Table + "name", "Table" + Guid.NewGuid().ToString("N")[..6]));

        var columns = Math.Max(1, table.ColumnCount);
        element.Add(new XElement(Odf.Table + "table-column",
            new XAttribute(Odf.Table + "number-columns-repeated", columns)));

        foreach (var row in table.Rows)
        {
            var rowElement = new XElement(Odf.Table + "table-row");

            foreach (var cell in row.Cells)
            {
                var cellElement = new XElement(Odf.Table + "table-cell",
                    new XAttribute(Odf.Office + "value-type", "string"));

                if (cell.ColumnSpan > 1)
                {
                    cellElement.Add(new XAttribute(Odf.Table + "number-columns-spanned", cell.ColumnSpan));
                }

                var wrote = false;
                foreach (var block in cell.Blocks.OfType<DocumentParagraph>())
                {
                    cellElement.Add(BuildParagraph(block, styles));
                    wrote = true;
                }

                if (!wrote)
                {
                    cellElement.Add(new XElement(Odf.Text + "p"));
                }

                rowElement.Add(cellElement);
            }

            element.Add(rowElement);
        }

        return element;
    }

    private static object[] NamespaceDeclarations() =>
    [
        new XAttribute(XNamespace.Xmlns + "office", Odf.Office.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "text", Odf.Text.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "style", Odf.Style.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "fo", Odf.Fo.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "table", Odf.Table.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "draw", Odf.Draw.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "svg", Odf.Svg.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "meta", Odf.Meta.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "xlink", Odf.Xlink.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "dc", Odf.Dc.NamespaceName)
    ];

    private static void WriteXml(ZipArchive archive, string entryName, XDocument document)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var payload = entry.Open();
        using var writer = new StreamWriter(payload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        document.Save(writer, SaveOptions.DisableFormatting);
    }
}

/// <summary>
/// Collects distinct direct formatting and hands out a style name for each.
/// </summary>
/// <remarks>
/// ODF has no inline style attributes: every variation must be declared as an
/// automatic style and referenced by name. Deduplicating by value keeps a
/// hundred-page document to a handful of declarations rather than one per run.
/// </remarks>
internal sealed class OdfAutomaticStyleTable
{
    private readonly Dictionary<(ParagraphFormatting Format, string Parent), string> _paragraphs = [];
    private readonly Dictionary<RunFormatting, string> _runs = [];
    private readonly Dictionary<ListKind, string> _lists = [];
    private string? _pageBreak;

    public string ParagraphStyle(ParagraphFormatting format, string parent)
    {
        if (_paragraphs.TryGetValue((format, parent), out var existing))
        {
            return existing;
        }

        var name = $"WLP{_paragraphs.Count + 1}";
        _paragraphs[(format, parent)] = name;
        return name;
    }

    public string RunStyle(RunFormatting format)
    {
        if (_runs.TryGetValue(format, out var existing))
        {
            return existing;
        }

        var name = $"WLT{_runs.Count + 1}";
        _runs[format] = name;
        return name;
    }

    public string ListStyle(ListKind kind)
    {
        if (_lists.TryGetValue(kind, out var existing))
        {
            return existing;
        }

        var name = kind == ListKind.Numbered ? "WLNumbering" : "WLBullets";
        _lists[kind] = name;
        return name;
    }

    public string PageBreakStyle() => _pageBreak ??= "WLPageBreak";

    public XElement ToElement()
    {
        var container = new XElement(Odf.Office + "automatic-styles");

        foreach (var ((format, parent), name) in _paragraphs)
        {
            container.Add(new XElement(Odf.Style + "style",
                new XAttribute(Odf.Style + "name", name),
                new XAttribute(Odf.Style + "family", "paragraph"),
                new XAttribute(Odf.Style + "parent-style-name", parent),
                new XElement(Odf.Style + "paragraph-properties",
                    new XAttribute(Odf.Fo + "text-align", format.Alignment switch
                    {
                        TextAlign.Center => "center",
                        TextAlign.Right => "end",
                        TextAlign.Justify => "justify",
                        _ => "start"
                    }),
                    new XAttribute(Odf.Fo + "line-height",
                        (format.LineSpacing * 100).ToString("0", CultureInfo.InvariantCulture) + "%"),
                    new XAttribute(Odf.Fo + "margin-top", Odf.PointsToPoints(format.SpaceBeforePt)),
                    new XAttribute(Odf.Fo + "margin-bottom", Odf.PointsToPoints(format.SpaceAfterPt)),
                    new XAttribute(Odf.Fo + "margin-left", Odf.PointsToPoints(format.IndentLeftPt)),
                    new XAttribute(Odf.Fo + "margin-right", Odf.PointsToPoints(format.IndentRightPt)),
                    new XAttribute(Odf.Fo + "text-indent", Odf.PointsToPoints(format.FirstLineIndentPt)))));
        }

        foreach (var (format, name) in _runs)
        {
            var properties = new XElement(Odf.Style + "text-properties");

            properties.Add(new XAttribute(Odf.Fo + "font-weight", format.Bold ? "bold" : "normal"));
            properties.Add(new XAttribute(Odf.Fo + "font-style", format.Italic ? "italic" : "normal"));
            properties.Add(new XAttribute(Odf.Style + "text-underline-style", format.Underline ? "solid" : "none"));
            properties.Add(new XAttribute(Odf.Style + "text-line-through-style", format.Strikethrough ? "solid" : "none"));

            if (format.FontFamily is { Length: > 0 } family)
            {
                properties.Add(new XAttribute(Odf.Style + "font-name", family));
                properties.Add(new XAttribute(Odf.Fo + "font-family", family));
            }

            if (format.FontSizePt is { } size)
            {
                properties.Add(new XAttribute(Odf.Fo + "font-size", Odf.PointsToPoints(size)));
            }

            if (format.ColorHex is { Length: 7 } color)
            {
                properties.Add(new XAttribute(Odf.Fo + "color", color));
            }

            if (format.HighlightHex is { Length: 7 } highlight)
            {
                properties.Add(new XAttribute(Odf.Fo + "background-color", highlight));
            }

            container.Add(new XElement(Odf.Style + "style",
                new XAttribute(Odf.Style + "name", name),
                new XAttribute(Odf.Style + "family", "text"),
                properties));
        }

        foreach (var (kind, name) in _lists)
        {
            var listStyle = new XElement(Odf.Text + "list-style",
                new XAttribute(Odf.Style + "name", name));

            for (var level = 1; level <= 9; level++)
            {
                var indent = Odf.PointsToCentimetres(level * 18);
                var properties = new XElement(Odf.Style + "list-level-properties",
                    new XAttribute(Odf.Text + "space-before", indent),
                    new XAttribute(Odf.Text + "min-label-width", "0.5cm"));

                listStyle.Add(kind == ListKind.Numbered
                    ? new XElement(Odf.Text + "list-level-style-number",
                        new XAttribute(Odf.Text + "level", level),
                        new XAttribute(Odf.Style + "num-suffix", "."),
                        new XAttribute(Odf.Style + "num-format", "1"),
                        properties)
                    : new XElement(Odf.Text + "list-level-style-bullet",
                        new XAttribute(Odf.Text + "level", level),
                        new XAttribute(Odf.Text + "bullet-char", "•"),
                        properties));
            }

            container.Add(listStyle);
        }

        if (_pageBreak is not null)
        {
            container.Add(new XElement(Odf.Style + "style",
                new XAttribute(Odf.Style + "name", _pageBreak),
                new XAttribute(Odf.Style + "family", "paragraph"),
                new XAttribute(Odf.Style + "parent-style-name", "Standard"),
                new XElement(Odf.Style + "paragraph-properties",
                    new XAttribute(Odf.Fo + "break-before", "page"))));
        }

        return container;
    }
}
