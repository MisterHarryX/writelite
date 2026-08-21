using System.IO.Compression;
using System.Xml.Linq;
using WriteLite.Documents.Model;
using WriteLite.Documents.OpenDocument;

namespace WriteLite.Documents.Import;

/// <summary>
/// OpenDocument Text → WriteLite document.
/// </summary>
/// <remarks>
/// ODF splits formatting between <c>styles.xml</c> (named styles a user picks) and
/// the <c>office:automatic-styles</c> block of <c>content.xml</c> (one generated
/// style per piece of direct formatting). A document written by LibreOffice puts
/// nearly all of its look in the second, so reading only the first returns an
/// unformatted document.
/// </remarks>
public sealed class OdtImporter : IDocumentImporter
{
    public DocumentFormat Format => DocumentFormat.Odt;

    public Task<DocumentImportResult> ImportAsync(
        Stream stream,
        DocumentImportOptions options,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Import(stream, options, progress, cancellationToken), cancellationToken);

    private static DocumentImportResult Import(
        Stream stream,
        DocumentImportOptions options,
        IProgress<DocumentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var warnings = new List<DocumentWarning>();
        progress?.Report(new DocumentProgress("Открытие документа", null));

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException exception)
        {
            throw new DocumentFormatException(
                "odt-not-a-zip",
                "Этот файл ODT повреждён и не может быть прочитан.",
                exception);
        }

        using (archive)
        {
            var content = ReadXml(archive, "content.xml")
                          ?? throw new DocumentFormatException(
                              "odt-no-content",
                              "В этом файле ODT нет части content.xml.");

            var stylesDocument = ReadXml(archive, "styles.xml");
            if (stylesDocument is null)
            {
                warnings.Add(new DocumentWarning("odt-no-styles", "Таблица стилей отсутствует — использовано оформление по умолчанию."));
            }

            var styles = new OdfStyleResolver(stylesDocument, content);

            var body = content.Root?
                .Element(Odf.Office + "body")?
                .Element(Odf.Office + "text");

            if (body is null)
            {
                throw new DocumentFormatException(
                    "odt-no-body",
                    "В этом файле ODT нет текстовой части.");
            }

            var document = WlDocument.Empty();
            document.Metadata.SourceFormat = DocumentFormat.Odt;
            document.Metadata.SourcePath = options.SourcePath;
            ReadMetadata(archive, document.Metadata, options.SourcePath);

            var section = document.CurrentSection();
            if (stylesDocument is not null && ReadPageSetup(stylesDocument) is { } page)
            {
                section.Page = page;
            }

            var characters = 0;
            var elements = body.Elements().ToArray();

            for (var index = 0; index < elements.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index % 64 == 0)
                {
                    progress?.Report(DocumentProgress.Of("Чтение абзацев", index, elements.Length));
                }

                if (characters >= options.MaxCharacters)
                {
                    warnings.Add(new DocumentWarning("odt-truncated", "Документ очень большой — открыта первая часть."));
                    break;
                }

                foreach (var block in ConvertBlock(elements[index], styles, warnings, listLevel: 0, listKind: null))
                {
                    section.Blocks.Add(block);
                    if (block is DocumentParagraph paragraph)
                    {
                        characters += paragraph.ToPlainText().Length;
                    }
                }
            }

            if (section.Blocks.Count == 0)
            {
                section.Blocks.Add(DocumentParagraph.FromText(string.Empty));
            }

            progress?.Report(new DocumentProgress("Готово", 1));
            return new DocumentImportResult(document, warnings);
        }
    }

    private static XDocument? ReadXml(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null)
        {
            return null;
        }

        try
        {
            using var payload = entry.Open();
            return XDocument.Load(payload, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static void ReadMetadata(ZipArchive archive, DocumentMetadata metadata, string? sourcePath)
    {
        metadata.Title = sourcePath is null ? null : Path.GetFileNameWithoutExtension(sourcePath);

        var meta = ReadXml(archive, "meta.xml")?.Root?
            .Element(Odf.Office + "meta");
        if (meta is null)
        {
            return;
        }

        var title = meta.Element(Odf.Dc + "title")?.Value;
        if (!string.IsNullOrWhiteSpace(title))
        {
            metadata.Title = title;
        }

        metadata.Author = meta.Element(Odf.Meta + "initial-creator")?.Value
                          ?? meta.Element(Odf.Dc + "creator")?.Value;
        metadata.Subject = meta.Element(Odf.Dc + "subject")?.Value;
        metadata.Language = meta.Element(Odf.Dc + "language")?.Value;

        if (DateTimeOffset.TryParse(meta.Element(Odf.Meta + "creation-date")?.Value, out var created))
        {
            metadata.Created = created;
        }
    }

    private static PageSetup? ReadPageSetup(XDocument styles)
    {
        var layout = styles.Root?
            .Element(Odf.Office + "automatic-styles")?
            .Elements(Odf.Style + "page-layout")
            .FirstOrDefault()?
            .Element(Odf.Style + "page-layout-properties");

        if (layout is null)
        {
            return null;
        }

        var width = Odf.LengthToPoints(layout.Attribute(Odf.Fo + "page-width")?.Value);
        var height = Odf.LengthToPoints(layout.Attribute(Odf.Fo + "page-height")?.Value);
        if (width is null || height is null)
        {
            return null;
        }

        return new PageSetup(
            width.Value,
            height.Value,
            Odf.LengthToPoints(layout.Attribute(Odf.Fo + "margin-left")?.Value) ?? 56.7,
            Odf.LengthToPoints(layout.Attribute(Odf.Fo + "margin-right")?.Value) ?? 56.7,
            Odf.LengthToPoints(layout.Attribute(Odf.Fo + "margin-top")?.Value) ?? 56.7,
            Odf.LengthToPoints(layout.Attribute(Odf.Fo + "margin-bottom")?.Value) ?? 56.7);
    }

    // ── Blocks ───────────────────────────────────────────────────────────────

    private static IEnumerable<BlockElement> ConvertBlock(
        XElement element,
        OdfStyleResolver styles,
        List<DocumentWarning> warnings,
        int listLevel,
        ListKind? listKind)
    {
        if (element.Name == Odf.Text + "p" || element.Name == Odf.Text + "h")
        {
            var paragraph = ConvertParagraph(element, styles, listLevel, listKind);

            if (styles.ResolveParagraph(element.Attribute(Odf.Text + "style-name")?.Value).PageBreakBefore)
            {
                yield return new PageBreak();

                // An otherwise empty paragraph exists only to carry the break; keeping
                // it as well would add a blank line on every round trip.
                if (paragraph.Inlines.Count == 0)
                {
                    yield break;
                }
            }

            yield return paragraph;
            yield break;
        }

        if (element.Name == Odf.Text + "list")
        {
            var kind = styles.ResolveListKind(element.Attribute(Odf.Text + "style-name")?.Value, listLevel)
                       ?? listKind
                       ?? ListKind.Bullet;

            foreach (var item in element.Elements(Odf.Text + "list-item")
                         .Concat(element.Elements(Odf.Text + "list-header")))
            {
                foreach (var child in item.Elements())
                {
                    foreach (var block in ConvertBlock(child, styles, warnings, listLevel + 1, kind))
                    {
                        yield return block;
                    }
                }
            }

            yield break;
        }

        if (element.Name == Odf.Table + "table")
        {
            yield return ConvertTable(element, styles, warnings);
            yield break;
        }

        if (element.Name == Odf.Text + "section")
        {
            foreach (var child in element.Elements())
            {
                foreach (var block in ConvertBlock(child, styles, warnings, listLevel, listKind))
                {
                    yield return block;
                }
            }

            yield break;
        }

        // Indexes, tracked-change containers and other wrappers: take their text so
        // nothing readable is silently lost.
        if (element.Name == Odf.Text + "table-of-content"
            || element.Name == Odf.Text + "alphabetical-index"
            || element.Name == Odf.Text + "tracked-changes")
        {
            foreach (var paragraph in element.Descendants(Odf.Text + "p"))
            {
                yield return ConvertParagraph(paragraph, styles, listLevel, listKind);
            }
        }
    }

    private static DocumentParagraph ConvertParagraph(
        XElement element,
        OdfStyleResolver styles,
        int listLevel,
        ListKind? listKind)
    {
        var styleName = element.Attribute(Odf.Text + "style-name")?.Value;
        var resolved = styles.ResolveParagraph(styleName);

        var paragraph = new DocumentParagraph
        {
            Format = resolved.Format,
            StyleName = styleName
        };

        if (element.Name == Odf.Text + "h")
        {
            var level = (int?)element.Attribute(Odf.Text + "outline-level") ?? 1;
            paragraph.OutlineLevel = Math.Clamp(level, 1, 6);
        }
        else if (resolved.OutlineLevel is { } inherited)
        {
            paragraph.OutlineLevel = inherited;
        }

        if (listLevel > 0 && listKind is { } kind)
        {
            paragraph.List = new ListInfo(kind, Math.Clamp(listLevel - 1, 0, 8));
        }

        AppendInlines(paragraph, element, styles, resolved.RunFormat);
        return paragraph;
    }

    private static void AppendInlines(
        DocumentParagraph paragraph,
        XElement container,
        OdfStyleResolver styles,
        RunFormatting inherited)
    {
        foreach (var node in container.Nodes())
        {
            switch (node)
            {
                case XText text:
                    Append(paragraph, text.Value, inherited);
                    break;

                case XElement element when element.Name == Odf.Text + "span":
                    var spanFormat = styles.ResolveRun(element.Attribute(Odf.Text + "style-name")?.Value, inherited);
                    AppendInlines(paragraph, element, styles, spanFormat);
                    break;

                case XElement element when element.Name == Odf.Text + "s":
                    // Runs of spaces are stored as a count, not as literal characters.
                    var count = (int?)element.Attribute(Odf.Text + "c") ?? 1;
                    Append(paragraph, new string(' ', Math.Clamp(count, 1, 4096)), inherited);
                    break;

                case XElement element when element.Name == Odf.Text + "tab":
                    Append(paragraph, "\t", inherited);
                    break;

                case XElement element when element.Name == Odf.Text + "line-break":
                    paragraph.Inlines.Add(new LineBreakRun());
                    break;

                case XElement element when element.Name == Odf.Text + "a":
                    AppendHyperlink(paragraph, element, styles, inherited);
                    break;

                case XElement element when element.Name == Odf.Text + "soft-page-break":
                    break;

                case XElement element:
                    // Bookmarks, notes, fields and change marks: keep their text.
                    AppendInlines(paragraph, element, styles, inherited);
                    break;
            }
        }
    }

    private static void AppendHyperlink(
        DocumentParagraph paragraph,
        XElement element,
        OdfStyleResolver styles,
        RunFormatting inherited)
    {
        var link = new HyperlinkRun(element.Attribute(Odf.Xlink + "href")?.Value);
        var carrier = new DocumentParagraph();
        AppendInlines(carrier, element, styles, inherited);

        foreach (var run in carrier.Inlines.OfType<TextRun>())
        {
            link.Runs.Add(run);
        }

        if (link.Runs.Count > 0)
        {
            paragraph.Inlines.Add(link);
        }
    }

    private static void Append(DocumentParagraph paragraph, string text, RunFormatting format)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (paragraph.Inlines.Count > 0
            && paragraph.Inlines[^1] is TextRun previous
            && previous.Format == format)
        {
            previous.Text += text;
            return;
        }

        paragraph.Inlines.Add(new TextRun(text, format));
    }

    private static DocumentTable ConvertTable(XElement element, OdfStyleResolver styles, List<DocumentWarning> warnings)
    {
        var table = new DocumentTable();

        var rows = element.Descendants(Odf.Table + "table-row").ToArray();
        var headerRows = element.Elements(Odf.Table + "table-header-rows")
            .SelectMany(header => header.Descendants(Odf.Table + "table-row"))
            .ToHashSet();

        foreach (var sourceRow in rows)
        {
            var row = new TableRow { IsHeader = headerRows.Contains(sourceRow) };

            foreach (var sourceCell in sourceRow.Elements(Odf.Table + "table-cell"))
            {
                var cell = new TableCell
                {
                    ColumnSpan = (int?)sourceCell.Attribute(Odf.Table + "number-columns-spanned") ?? 1
                };

                foreach (var child in sourceCell.Elements())
                {
                    foreach (var block in ConvertBlock(child, styles, warnings, listLevel: 0, listKind: null))
                    {
                        cell.Blocks.Add(block);
                    }
                }

                if (cell.Blocks.Count == 0)
                {
                    cell.Blocks.Add(DocumentParagraph.FromText(string.Empty));
                }

                row.Cells.Add(cell);
            }

            if (row.Cells.Count > 0)
            {
                table.Rows.Add(row);
            }
        }

        return table;
    }
}
