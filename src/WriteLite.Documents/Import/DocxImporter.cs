using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WriteLite.Documents.Model;
using OpenXmlParagraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using OpenXmlTable = DocumentFormat.OpenXml.Wordprocessing.Table;
using OpenXmlTableRow = DocumentFormat.OpenXml.Wordprocessing.TableRow;
using OpenXmlTableCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;
using OpenXmlRun = DocumentFormat.OpenXml.Wordprocessing.Run;
using OpenXmlBreak = DocumentFormat.OpenXml.Wordprocessing.Break;
using OpenXmlHyperlink = DocumentFormat.OpenXml.Wordprocessing.Hyperlink;
using WlParagraph = WriteLite.Documents.Model.DocumentParagraph;
using WlTable = WriteLite.Documents.Model.DocumentTable;
using WlTableRow = WriteLite.Documents.Model.TableRow;
using WlTableCell = WriteLite.Documents.Model.TableCell;
// Aliased because WriteLite's own DocumentFormat enum shadows the OpenXml SDK's
// root namespace of the same name for any type named inside this namespace.
using DrawingBlip = DocumentFormat.OpenXml.Drawing.Blip;
using DrawingExtent = DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent;
using DrawingDocProperties = DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties;

namespace WriteLite.Documents.Import;

/// <summary>
/// WordprocessingML → WriteLite document.
/// </summary>
/// <remarks>
/// Direct formatting alone is not enough: most real documents carry their look in
/// named styles, so every run and paragraph is resolved against the style chain
/// (docDefaults → linked/based-on styles → direct formatting) before it reaches the
/// model. Skipping that step is why naive importers open a styled document as
/// uniform 11 pt Calibri.
/// </remarks>
public sealed class DocxImporter : IDocumentImporter
{
    public DocumentFormat Format => DocumentFormat.Docx;

    public Task<DocumentImportResult> ImportAsync(
        Stream stream,
        DocumentImportOptions options,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default)
        // OpenXml's API is synchronous throughout; the work is pushed off the caller's
        // thread here so the one await at the call site is genuinely non-blocking.
        => Task.Run(() => Import(stream, options, progress, cancellationToken), cancellationToken);

    private static DocumentImportResult Import(
        Stream stream,
        DocumentImportOptions options,
        IProgress<DocumentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var warnings = new List<DocumentWarning>();
        progress?.Report(new DocumentProgress("Открытие документа", null));

        WordprocessingDocument package;
        try
        {
            package = WordprocessingDocument.Open(stream, isEditable: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new DocumentFormatException(
                $"docx-open-failed: {exception.GetType().Name}",
                "Этот файл DOCX повреждён и не может быть прочитан.",
                exception);
        }

        using (package)
        {
            var body = package.MainDocumentPart?.Document?.Body;
            if (body is null)
            {
                throw new DocumentFormatException(
                    "docx-no-body",
                    "В этом файле DOCX нет текстовой части.");
            }

            var styles = new DocxStyleResolver(package.MainDocumentPart);
            var numbering = new DocxNumberingResolver(package.MainDocumentPart);

            var document = WlDocument.Empty();
            document.Metadata.SourceFormat = DocumentFormat.Docx;
            document.Metadata.SourcePath = options.SourcePath;
            ReadMetadata(package, document.Metadata, options.SourcePath);

            var section = document.CurrentSection();
            section.Page = ReadPageSetup(body);

            var elements = body.ChildElements.ToArray();
            var characters = 0;

            for (var index = 0; index < elements.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index % 64 == 0)
                {
                    progress?.Report(DocumentProgress.Of("Чтение абзацев", index, elements.Length));
                }

                if (characters >= options.MaxCharacters)
                {
                    warnings.Add(new DocumentWarning("docx-truncated", "Документ очень большой — открыта первая часть."));
                    break;
                }

                switch (elements[index])
                {
                    case OpenXmlParagraph source:
                        foreach (var block in ConvertParagraph(source, styles, numbering, package.MainDocumentPart, document, warnings))
                        {
                            section.Blocks.Add(block);
                            if (block is WlParagraph converted)
                            {
                                characters += converted.ToPlainText().Length;
                            }
                        }

                        break;

                    case OpenXmlTable table:
                        try
                        {
                            section.Blocks.Add(ConvertTable(table, styles, numbering, package.MainDocumentPart, document, warnings));
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            warnings.Add(new DocumentWarning(
                                "docx-table-unreadable",
                                "Одну из таблиц не удалось прочитать — её текст добавлен абзацами."));
                            foreach (var paragraph in table.Descendants<OpenXmlParagraph>())
                            {
                                section.Blocks.Add(WlParagraph.FromText(paragraph.InnerText));
                            }
                        }

                        break;
                }
            }

            if (section.Blocks.Count == 0)
            {
                section.Blocks.Add(WlParagraph.FromText(string.Empty));
            }

            progress?.Report(new DocumentProgress("Готово", 1));
            return new DocumentImportResult(document, warnings);
        }
    }

    private static void ReadMetadata(WordprocessingDocument package, DocumentMetadata metadata, string? sourcePath)
    {
        try
        {
            var properties = package.PackageProperties;
            metadata.Title = string.IsNullOrWhiteSpace(properties.Title)
                ? (sourcePath is null ? null : Path.GetFileNameWithoutExtension(sourcePath))
                : properties.Title;
            metadata.Author = properties.Creator;
            metadata.Subject = properties.Subject;
            metadata.Created = properties.Created;
            metadata.Modified = properties.Modified;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Core properties are optional and a damaged part must not stop the import.
            metadata.Title = sourcePath is null ? null : Path.GetFileNameWithoutExtension(sourcePath);
        }
    }

    private static PageSetup ReadPageSetup(Body body)
    {
        var properties = body.Elements<SectionProperties>().FirstOrDefault();
        var size = properties?.Elements<PageSize>().FirstOrDefault();
        var margin = properties?.Elements<PageMargin>().FirstOrDefault();

        if (size is null)
        {
            return PageSetup.A4;
        }

        return new PageSetup(
            TwipsToPoints(size.Width?.Value ?? 11906),
            TwipsToPoints(size.Height?.Value ?? 16838),
            TwipsToPoints((uint)Math.Max(0, margin?.Left?.Value ?? 1134)),
            TwipsToPoints((uint)Math.Max(0, margin?.Right?.Value ?? 1134)),
            TwipsToPoints((uint)Math.Max(0, margin?.Top?.Value ?? 1134)),
            TwipsToPoints((uint)Math.Max(0, margin?.Bottom?.Value ?? 1134)));
    }

    private static double TwipsToPoints(uint twips) => twips / 20.0;

    private static double TwipsToPoints(int twips) => twips / 20.0;

    // ── Paragraphs ───────────────────────────────────────────────────────────

    /// <summary>
    /// One WordprocessingML paragraph, which can yield a page break as well as text.
    /// </summary>
    private static IEnumerable<BlockElement> ConvertParagraph(
        OpenXmlParagraph source,
        DocxStyleResolver styles,
        DocxNumberingResolver numbering,
        MainDocumentPart? part,
        WlDocument document,
        List<DocumentWarning> warnings)
    {
        var properties = source.ParagraphProperties;
        var styleId = properties?.ParagraphStyleId?.Val?.Value;
        var resolved = styles.ResolveParagraph(styleId, properties);

        var paragraph = new WlParagraph
        {
            Format = resolved.Format,
            OutlineLevel = resolved.OutlineLevel,
            StyleName = resolved.StyleName
        };

        var numberingProperties = properties?.NumberingProperties;
        if (numberingProperties is not null)
        {
            var numberId = numberingProperties.NumberingId?.Val?.Value;
            var level = numberingProperties.NumberingLevelReference?.Val?.Value ?? 0;
            paragraph.List = numbering.Resolve(numberId, level);
        }
        else if (resolved.List is { } inherited)
        {
            paragraph.List = inherited;
        }

        var pageBreakBefore = properties?.PageBreakBefore is { } before
                              && (before.Val is null || before.Val.Value);
        if (pageBreakBefore)
        {
            yield return new PageBreak();
        }

        var pendingBreaks = new List<BlockElement>();

        foreach (var child in source.ChildElements)
        {
            switch (child)
            {
                case OpenXmlRun run:
                    AppendRun(paragraph, run, styles, resolved.RunFormat, part, document, pendingBreaks);
                    break;

                case OpenXmlHyperlink link:
                    AppendHyperlink(paragraph, link, styles, resolved.RunFormat, part);
                    break;

                case SimpleField field:
                    // A field's cached result is its visible text; the instruction is not.
                    foreach (var run in field.Elements<OpenXmlRun>())
                    {
                        AppendRun(paragraph, run, styles, resolved.RunFormat, part, document, pendingBreaks);
                    }

                    break;

                case BookmarkStart or BookmarkEnd or ProofError or ParagraphProperties:
                    break;

                default:
                    // Content controls and revision containers wrap ordinary runs.
                    foreach (var run in child.Descendants<OpenXmlRun>())
                    {
                        AppendRun(paragraph, run, styles, resolved.RunFormat, part, document, pendingBreaks);
                    }

                    break;
            }
        }

        yield return paragraph;

        foreach (var pending in pendingBreaks)
        {
            yield return pending;
        }
    }

    private static void AppendRun(
        WlParagraph paragraph,
        OpenXmlRun run,
        DocxStyleResolver styles,
        RunFormatting inherited,
        MainDocumentPart? part,
        WlDocument document,
        List<BlockElement> pendingBreaks)
    {
        var format = styles.ResolveRun(run.RunProperties, inherited);

        foreach (var child in run.ChildElements)
        {
            switch (child)
            {
                case Text text:
                    Append(paragraph, text.Text, format);
                    break;

                case OpenXmlBreak brk when brk.Type is not null && brk.Type.Value == BreakValues.Page:
                    pendingBreaks.Add(new PageBreak());
                    break;

                case OpenXmlBreak:
                    paragraph.Inlines.Add(new LineBreakRun());
                    break;

                case TabChar:
                    Append(paragraph, "\t", format);
                    break;

                case Drawing drawing:
                    AppendImage(paragraph, drawing, part, document);
                    break;

                case NoBreakHyphen:
                    Append(paragraph, "‑", format);
                    break;

                case SoftHyphen:
                    Append(paragraph, "­", format);
                    break;
            }
        }
    }

    private static void Append(WlParagraph paragraph, string text, RunFormatting format)
    {
        if (text.Length == 0)
        {
            return;
        }

        // Merging into the previous run keeps the model close to what a writer sees:
        // Word routinely splits one visually uniform sentence across a dozen runs
        // because of spell-check state, and importing those verbatim makes every
        // downstream offset calculation noisier for no gain.
        if (paragraph.Inlines.Count > 0
            && paragraph.Inlines[^1] is TextRun previous
            && previous.Format == format)
        {
            previous.Text += text;
            return;
        }

        paragraph.Inlines.Add(new TextRun(text, format));
    }

    private static void AppendHyperlink(
        WlParagraph paragraph,
        OpenXmlHyperlink link,
        DocxStyleResolver styles,
        RunFormatting inherited,
        MainDocumentPart? part)
    {
        var target = ResolveHyperlinkTarget(link, part);
        var hyperlink = new HyperlinkRun(target);

        foreach (var run in link.Descendants<OpenXmlRun>())
        {
            var format = styles.ResolveRun(run.RunProperties, inherited);
            var text = string.Concat(run.Descendants<Text>().Select(node => node.Text));
            if (text.Length > 0)
            {
                hyperlink.Runs.Add(new TextRun(text, format));
            }
        }

        if (hyperlink.Runs.Count > 0)
        {
            paragraph.Inlines.Add(hyperlink);
        }
    }

    private static string? ResolveHyperlinkTarget(OpenXmlHyperlink link, MainDocumentPart? part)
    {
        if (link.Anchor?.Value is { Length: > 0 } anchor)
        {
            return "#" + anchor;
        }

        var id = link.Id?.Value;
        if (id is null || part is null)
        {
            return null;
        }

        try
        {
            return part.HyperlinkRelationships
                .FirstOrDefault(relationship => relationship.Id == id)?.Uri.ToString();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private static void AppendImage(WlParagraph paragraph, Drawing drawing, MainDocumentPart? part, WlDocument document)
    {
        if (part is null)
        {
            return;
        }

        try
        {
            var blip = drawing.Descendants<DrawingBlip>().FirstOrDefault();
            var embed = blip?.Embed?.Value;
            if (embed is null)
            {
                return;
            }

            if (part.GetPartById(embed) is not ImagePart image)
            {
                return;
            }

            using var payload = image.GetStream();
            using var buffer = new MemoryStream();
            payload.CopyTo(buffer);

            var extent = drawing.Descendants<DrawingExtent>().FirstOrDefault();
            var widthPt = extent is null ? 0 : extent.Cx!.Value / 12700.0;
            var heightPt = extent is null ? 0 : extent.Cy!.Value / 12700.0;

            var id = document.Resources.Add(new DocumentImage(
                buffer.ToArray(),
                image.ContentType,
                widthPt,
                heightPt));

            paragraph.Inlines.Add(new ImageRun(id)
            {
                WidthPt = widthPt,
                HeightPt = heightPt,
                Description = drawing.Descendants<DrawingDocProperties>().FirstOrDefault()?.Description
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An unreadable image never costs the surrounding text.
        }
    }

    // ── Tables ───────────────────────────────────────────────────────────────

    private static WlTable ConvertTable(
        OpenXmlTable source,
        DocxStyleResolver styles,
        DocxNumberingResolver numbering,
        MainDocumentPart? part,
        WlDocument document,
        List<DocumentWarning> warnings)
    {
        var table = new WlTable();

        var grid = source.Elements<TableGrid>().FirstOrDefault();
        if (grid is not null)
        {
            var widths = grid.Elements<GridColumn>()
                .Select(column => double.TryParse(column.Width?.Value, out var value) ? value : 0)
                .ToArray();
            var total = widths.Sum();
            if (total > 0)
            {
                table.ColumnWidths.AddRange(widths.Select(width => width / total));
            }
        }

        foreach (var sourceRow in source.Elements<OpenXmlTableRow>())
        {
            var row = new WlTableRow
            {
                IsHeader = sourceRow.TableRowProperties?.Elements<TableHeader>().Any() == true
            };

            foreach (var sourceCell in sourceRow.Elements<OpenXmlTableCell>())
            {
                var cell = new WlTableCell
                {
                    ColumnSpan = sourceCell.TableCellProperties?.GridSpan?.Val?.Value ?? 1
                };

                foreach (var child in sourceCell.ChildElements)
                {
                    switch (child)
                    {
                        case OpenXmlParagraph paragraph:
                            foreach (var block in ConvertParagraph(paragraph, styles, numbering, part, document, warnings))
                            {
                                if (block is not PageBreak)
                                {
                                    cell.Blocks.Add(block);
                                }
                            }

                            break;

                        case OpenXmlTable nested:
                            // Nested tables are flattened to their text: the model has no
                            // nesting and a silently dropped table is worse than a plain one.
                            foreach (var paragraph in nested.Descendants<OpenXmlParagraph>())
                            {
                                cell.Blocks.Add(WlParagraph.FromText(paragraph.InnerText));
                            }

                            break;
                    }
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

        return table;
    }
}
