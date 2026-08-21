using System.Globalization;
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
using WlDocumentModel = WriteLite.Documents.Model.WlDocument;
using WlParagraph = WriteLite.Documents.Model.DocumentParagraph;
using WlTable = WriteLite.Documents.Model.DocumentTable;

namespace WriteLite.Documents.Export;

/// <summary>
/// WriteLite document → WordprocessingML.
/// </summary>
/// <remarks>
/// The written package carries its own style table and numbering definitions
/// rather than assuming Word's built-ins: a document opened from PDF or TXT has
/// no styles to inherit, and a heading that resolves to nothing is a heading that
/// looks like body text everywhere but Word.
/// </remarks>
public sealed class DocxExporter : IDocumentExporter
{
    private const int BulletNumberingId = 1;
    private const int OrderedNumberingId = 2;

    public DocumentFormat Format => DocumentFormat.Docx;

    public Task<IReadOnlyList<DocumentWarning>> ExportAsync(
        WlDocumentModel document,
        Stream stream,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<DocumentWarning>>(
            () => Export(document, stream, progress, cancellationToken),
            cancellationToken);

    private static List<DocumentWarning> Export(
        WlDocumentModel document,
        Stream stream,
        IProgress<DocumentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var warnings = new List<DocumentWarning>();
        progress?.Report(new DocumentProgress("Подготовка документа", null));

        using var package = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);
        var mainPart = package.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());
        var body = mainPart.Document.Body!;

        WriteStyles(mainPart);
        WriteNumbering(mainPart);
        WriteMetadata(package, document.Metadata);

        var blocks = document.Blocks.ToArray();
        for (var index = 0; index < blocks.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index % 128 == 0)
            {
                progress?.Report(DocumentProgress.Of("Запись абзацев", index, blocks.Length));
            }

            switch (blocks[index])
            {
                case WlParagraph paragraph:
                    body.AppendChild(BuildParagraph(paragraph, mainPart, document, warnings));
                    break;

                case WlTable table:
                    body.AppendChild(BuildTable(table, mainPart, document, warnings));
                    // Word requires a paragraph after a table; without one the next
                    // table merges into this one and the file opens with a repair prompt.
                    body.AppendChild(new OpenXmlParagraph());
                    break;

                case PageBreak:
                    body.AppendChild(new OpenXmlParagraph(
                        new OpenXmlRun(new OpenXmlBreak { Type = BreakValues.Page })));
                    break;
            }
        }

        var page = document.Sections.Count > 0 ? document.Sections[0].Page : PageSetup.A4;
        body.AppendChild(new SectionProperties(
            new PageSize
            {
                Width = (uint)PointsToTwips(page.WidthPt),
                Height = (uint)PointsToTwips(page.HeightPt)
            },
            new PageMargin
            {
                Left = (uint)PointsToTwips(page.MarginLeftPt),
                Right = (uint)PointsToTwips(page.MarginRightPt),
                Top = PointsToTwips(page.MarginTopPt),
                Bottom = PointsToTwips(page.MarginBottomPt)
            }));

        mainPart.Document.Save();
        progress?.Report(new DocumentProgress("Готово", 1));
        return warnings;
    }

    private static int PointsToTwips(double points) => (int)Math.Round(points * 20);

    private static void WriteMetadata(WordprocessingDocument package, DocumentMetadata metadata)
    {
        try
        {
            var properties = package.PackageProperties;
            if (!string.IsNullOrWhiteSpace(metadata.Title))
            {
                properties.Title = metadata.Title;
            }

            if (!string.IsNullOrWhiteSpace(metadata.Author))
            {
                properties.Creator = metadata.Author;
            }

            properties.Modified = DateTime.UtcNow;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Core properties are cosmetic; never fail a save over them.
        }
    }

    // ── Paragraphs ───────────────────────────────────────────────────────────

    private static OpenXmlParagraph BuildParagraph(
        WlParagraph source,
        MainDocumentPart part,
        WlDocumentModel document,
        List<DocumentWarning> warnings)
    {
        var paragraph = new OpenXmlParagraph();
        var properties = new ParagraphProperties();

        if (source.IsHeading)
        {
            properties.ParagraphStyleId = new ParagraphStyleId { Val = $"Heading{Math.Clamp(source.OutlineLevel!.Value, 1, 6)}" };
        }

        properties.Justification = new Justification
        {
            Val = source.Format.Alignment switch
            {
                TextAlign.Center => JustificationValues.Center,
                TextAlign.Right => JustificationValues.Right,
                TextAlign.Justify => JustificationValues.Both,
                _ => JustificationValues.Left
            }
        };

        properties.SpacingBetweenLines = new SpacingBetweenLines
        {
            Before = PointsToTwips(source.Format.SpaceBeforePt).ToString(CultureInfo.InvariantCulture),
            After = PointsToTwips(source.Format.SpaceAfterPt).ToString(CultureInfo.InvariantCulture),
            Line = ((int)Math.Round(source.Format.LineSpacing * 240)).ToString(CultureInfo.InvariantCulture),
            LineRule = LineSpacingRuleValues.Auto
        };

        if (source.Format.IndentLeftPt != 0 || source.Format.IndentRightPt != 0 || source.Format.FirstLineIndentPt != 0)
        {
            var indentation = new Indentation
            {
                Left = PointsToTwips(source.Format.IndentLeftPt).ToString(CultureInfo.InvariantCulture),
                Right = PointsToTwips(source.Format.IndentRightPt).ToString(CultureInfo.InvariantCulture)
            };

            if (source.Format.FirstLineIndentPt >= 0)
            {
                indentation.FirstLine = PointsToTwips(source.Format.FirstLineIndentPt).ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                indentation.Hanging = PointsToTwips(-source.Format.FirstLineIndentPt).ToString(CultureInfo.InvariantCulture);
            }

            properties.Indentation = indentation;
        }

        if (source.List is { } list)
        {
            properties.NumberingProperties = new NumberingProperties(
                new NumberingLevelReference { Val = Math.Clamp(list.Level, 0, 8) },
                new NumberingId { Val = list.Kind == ListKind.Numbered ? OrderedNumberingId : BulletNumberingId });
        }

        paragraph.ParagraphProperties = properties;

        foreach (var inline in source.Inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    paragraph.AppendChild(BuildRun(run.Text, run.Format));
                    break;

                case LineBreakRun:
                    paragraph.AppendChild(new OpenXmlRun(new OpenXmlBreak()));
                    break;

                case HyperlinkRun link:
                    AppendHyperlink(paragraph, link, part);
                    break;

                case ImageRun image:
                    AppendImageFallback(paragraph, image, document, warnings);
                    break;
            }
        }

        return paragraph;
    }

    private static OpenXmlRun BuildRun(string text, RunFormatting format)
    {
        var run = new OpenXmlRun();
        var properties = new RunProperties();

        if (format.Bold)
        {
            properties.Bold = new Bold();
        }

        if (format.Italic)
        {
            properties.Italic = new Italic();
        }

        if (format.Underline)
        {
            properties.Underline = new Underline { Val = UnderlineValues.Single };
        }

        if (format.Strikethrough)
        {
            properties.Strike = new Strike();
        }

        if (!string.IsNullOrWhiteSpace(format.FontFamily))
        {
            properties.RunFonts = new RunFonts
            {
                Ascii = format.FontFamily,
                HighAnsi = format.FontFamily,
                ComplexScript = format.FontFamily
            };
        }

        if (format.FontSizePt is { } size)
        {
            var halfPoints = ((int)Math.Round(size * 2)).ToString(CultureInfo.InvariantCulture);
            properties.FontSize = new FontSize { Val = halfPoints };
            properties.FontSizeComplexScript = new FontSizeComplexScript { Val = halfPoints };
        }

        if (format.ColorHex is { Length: 7 } color)
        {
            properties.Color = new Color { Val = color[1..] };
        }

        if (format.HighlightHex is { Length: 7 } highlight)
        {
            properties.Shading = new Shading { Val = ShadingPatternValues.Clear, Fill = highlight[1..] };
        }

        if (properties.HasChildren)
        {
            run.RunProperties = properties;
        }

        // Space:preserve or Word silently eats leading and trailing spaces, which
        // turns "word " + "next" into "wordnext" on reopen.
        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return run;
    }

    private static void AppendHyperlink(OpenXmlParagraph paragraph, HyperlinkRun link, MainDocumentPart part)
    {
        var runs = link.Runs
            .Select(run => BuildRun(run.Text, run.Format with { Underline = true, ColorHex = run.Format.ColorHex ?? "#0563C1" }))
            .ToArray();

        if (runs.Length == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(link.Target))
        {
            foreach (var run in runs)
            {
                paragraph.AppendChild(run);
            }

            return;
        }

        if (link.Target.StartsWith('#'))
        {
            paragraph.AppendChild(new OpenXmlHyperlink(runs) { Anchor = link.Target[1..] });
            return;
        }

        if (!Uri.TryCreate(link.Target, UriKind.Absolute, out var uri))
        {
            foreach (var run in runs)
            {
                paragraph.AppendChild(run);
            }

            return;
        }

        var relationship = part.AddHyperlinkRelationship(uri, isExternal: true);
        paragraph.AppendChild(new OpenXmlHyperlink(runs) { Id = relationship.Id });
    }

    /// <summary>
    /// Images survive as their description.
    /// </summary>
    /// <remarks>
    /// Re-embedding the binary would mean rebuilding the full DrawingML wrapper, and
    /// a wrong one produces a file Word offers to repair. Losing a picture is bad;
    /// producing a document that will not open is worse, so this is reported rather
    /// than attempted.
    /// </remarks>
    private static void AppendImageFallback(
        OpenXmlParagraph paragraph,
        ImageRun image,
        WlDocumentModel document,
        List<DocumentWarning> warnings)
    {
        if (warnings.All(warning => warning.Code != "docx-images-dropped"))
        {
            warnings.Add(new DocumentWarning("docx-images-dropped", "Изображения не переносятся в этот файл."));
        }

        var caption = image.Description
                      ?? (document.Resources.Get(image.ResourceId) is null ? "изображение" : "изображение");
        paragraph.AppendChild(BuildRun($"[{caption}]", new RunFormatting(Italic: true)));
    }

    // ── Tables ───────────────────────────────────────────────────────────────

    private static OpenXmlTable BuildTable(
        WlTable source,
        MainDocumentPart part,
        WlDocumentModel document,
        List<DocumentWarning> warnings)
    {
        var table = new OpenXmlTable();

        table.AppendChild(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                new RightBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" }),
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }));

        foreach (var sourceRow in source.Rows)
        {
            var row = new OpenXmlTableRow();

            if (sourceRow.IsHeader)
            {
                row.TableRowProperties = new TableRowProperties(new TableHeader());
            }

            foreach (var sourceCell in sourceRow.Cells)
            {
                var cell = new OpenXmlTableCell();
                if (sourceCell.ColumnSpan > 1)
                {
                    cell.TableCellProperties = new TableCellProperties(new GridSpan { Val = sourceCell.ColumnSpan });
                }

                var wrote = false;
                foreach (var block in sourceCell.Blocks.OfType<WlParagraph>())
                {
                    cell.AppendChild(BuildParagraph(block, part, document, warnings));
                    wrote = true;
                }

                // A table cell with no paragraph is invalid WordprocessingML.
                if (!wrote)
                {
                    cell.AppendChild(new OpenXmlParagraph());
                }

                row.AppendChild(cell);
            }

            if (row.ChildElements.Count > 0)
            {
                table.AppendChild(row);
            }
        }

        return table;
    }

    // ── Style and numbering tables ───────────────────────────────────────────

    private static void WriteStyles(MainDocumentPart part)
    {
        var stylesPart = part.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles();

        styles.AppendChild(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri" },
                new FontSize { Val = "22" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                new SpacingBetweenLines { After = "160", Line = "259", LineRule = LineSpacingRuleValues.Auto }))));

        styles.AppendChild(new Style(
            new StyleName { Val = "Normal" },
            new PrimaryStyle())
        {
            Type = StyleValues.Paragraph,
            StyleId = "Normal",
            Default = true
        });

        // Sizes descend from 20 pt so a six-level document still reads as a hierarchy.
        var headingSizes = new[] { 40, 32, 28, 24, 22, 20 };
        for (var level = 1; level <= 6; level++)
        {
            styles.AppendChild(new Style(
                new StyleName { Val = $"heading {level}" },
                new BasedOn { Val = "Normal" },
                new NextParagraphStyle { Val = "Normal" },
                new StyleParagraphProperties(
                    new OutlineLevel { Val = level - 1 },
                    new KeepNext(),
                    new SpacingBetweenLines
                    {
                        Before = ((7 - level) * 60).ToString(CultureInfo.InvariantCulture),
                        After = "120",
                        Line = "259",
                        LineRule = LineSpacingRuleValues.Auto
                    }),
                new StyleRunProperties(
                    new Bold(),
                    new FontSize { Val = headingSizes[level - 1].ToString(CultureInfo.InvariantCulture) }))
            {
                Type = StyleValues.Paragraph,
                StyleId = $"Heading{level}"
            });
        }

        stylesPart.Styles = styles;
        stylesPart.Styles.Save();
    }

    private static void WriteNumbering(MainDocumentPart part)
    {
        var numberingPart = part.AddNewPart<NumberingDefinitionsPart>();
        var numbering = new Numbering();

        numbering.AppendChild(BuildAbstractNumbering(0, bullet: true));
        numbering.AppendChild(BuildAbstractNumbering(1, bullet: false));

        numbering.AppendChild(new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = BulletNumberingId });
        numbering.AppendChild(new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = OrderedNumberingId });

        numberingPart.Numbering = numbering;
        numberingPart.Numbering.Save();
    }

    private static AbstractNum BuildAbstractNumbering(int abstractId, bool bullet)
    {
        var definition = new AbstractNum { AbstractNumberId = abstractId };
        var bulletGlyphs = new[] { "•", "◦", "▪" };
        var numberFormats = new[]
        {
            NumberFormatValues.Decimal,
            NumberFormatValues.LowerLetter,
            NumberFormatValues.LowerRoman
        };

        for (var level = 0; level < 9; level++)
        {
            var indent = 720 * (level + 1);
            definition.AppendChild(new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat
                {
                    Val = bullet ? NumberFormatValues.Bullet : numberFormats[level % numberFormats.Length]
                },
                new LevelText
                {
                    Val = bullet ? bulletGlyphs[level % bulletGlyphs.Length] : $"%{level + 1}."
                },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new PreviousParagraphProperties(new Indentation
                {
                    Left = indent.ToString(CultureInfo.InvariantCulture),
                    Hanging = "360"
                }))
            {
                LevelIndex = level
            });
        }

        return definition;
    }
}
