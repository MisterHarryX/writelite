using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using WriteLite.Documents.Model;
using WriteLite.Documents.Pdf;

namespace WriteLite.Documents.Export;

/// <summary>
/// WriteLite document → PDF, laid out by WriteLite rather than by a second document model.
/// </summary>
/// <remarks>
/// PDFsharp draws; it does not flow text. Everything between "here is a paragraph"
/// and "here are glyphs at coordinates" is this file: word wrapping across runs of
/// mixed formatting, alignment including justification, first-line and side indents,
/// space before and after, line spacing, list markers, tables and page breaks.
///
/// Doing it here rather than through a layout library keeps one document model in
/// the product and makes the output follow the same formatting rules the editor
/// shows on screen.
/// </remarks>
public sealed class PdfExporter : IDocumentExporter
{
    private const double DefaultFontSizePt = 11;
    private const string DefaultFontFamily = "Segoe UI";

    public DocumentFormat Format => DocumentFormat.Pdf;

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
        progress?.Report(new DocumentProgress("Подготовка PDF", null));

        SystemFontResolver.Install();

        // Unicode encoding is what lets an embedded font carry Cyrillic; the
        // WinAnsi default silently maps anything outside Latin-1 to nothing.
        GlobalFontSettings.DefaultFontEncoding = PdfFontEncoding.Unicode;

        using var package = new PdfDocument();
        WriteMetadata(package, document.Metadata);

        var page = document.Sections.Count > 0 ? document.Sections[0].Page : PageSetup.A4;
        using var layout = new PdfLayoutSession(package, page);

        var blocks = document.Blocks.ToArray();
        var counters = new Dictionary<int, int>();

        for (var index = 0; index < blocks.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index % 32 == 0)
            {
                progress?.Report(DocumentProgress.Of("Отрисовка страниц", index, blocks.Length));
            }

            switch (blocks[index])
            {
                case DocumentParagraph paragraph:
                    layout.DrawParagraph(paragraph, counters);
                    break;

                case DocumentTable table:
                    counters.Clear();
                    layout.DrawTable(table);
                    break;

                case PageBreak:
                    counters.Clear();
                    layout.NewPage();
                    break;
            }
        }

        layout.Finish();

        if (document.Resources.Images.Count > 0)
        {
            warnings.Add(new DocumentWarning("pdf-images-dropped", "Изображения не переносятся в этот файл."));
        }

        if (package.PageCount == 0)
        {
            package.AddPage();
        }

        package.Save(stream, closeStream: false);
        progress?.Report(new DocumentProgress("Готово", 1));
        return warnings;
    }

    private static void WriteMetadata(PdfDocument package, DocumentMetadata metadata)
    {
        package.Info.Creator = "WriteLite";

        if (!string.IsNullOrWhiteSpace(metadata.Title))
        {
            package.Info.Title = metadata.Title;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Author))
        {
            package.Info.Author = metadata.Author;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Subject))
        {
            package.Info.Subject = metadata.Subject;
        }
    }

    /// <summary>
    /// Draws one document, opening pages as the cursor runs off the bottom.
    /// </summary>
    private sealed class PdfLayoutSession(PdfDocument package, PageSetup page) : IDisposable
    {
        private readonly Dictionary<(string Family, double Size, bool Bold, bool Italic, bool Underline, bool Strike), XFont> _fonts = [];
        private XGraphics? _graphics;
        private PdfPage? _current;
        private double _y;

        public void NewPage()
        {
            _graphics?.Dispose();

            _current = package.AddPage();
            _current.Width = XUnit.FromPoint(page.WidthPt);
            _current.Height = XUnit.FromPoint(page.HeightPt);
            _graphics = XGraphics.FromPdfPage(_current);
            _y = page.MarginTopPt;
        }

        public void Finish() => _graphics?.Dispose();

        public void Dispose() => _graphics?.Dispose();

        private XGraphics Graphics
        {
            get
            {
                if (_graphics is null)
                {
                    NewPage();
                }

                return _graphics!;
            }
        }

        private double ContentBottom => page.HeightPt - page.MarginBottomPt;

        private void EnsureRoom(double height)
        {
            if (_graphics is null)
            {
                NewPage();
                return;
            }

            if (_y + height > ContentBottom)
            {
                NewPage();
            }
        }

        // ── Paragraphs ───────────────────────────────────────────────────────

        public void DrawParagraph(DocumentParagraph paragraph, Dictionary<int, int> counters)
        {
            var format = paragraph.Format;
            var baseRun = ResolveBaseRun(paragraph);

            var marker = BuildListMarker(paragraph, counters);
            var listIndent = paragraph.List is { } list ? (list.Level + 1) * 18.0 : 0;

            var left = page.MarginLeftPt + format.IndentLeftPt + listIndent;
            var right = page.WidthPt - page.MarginRightPt - format.IndentRightPt;
            var width = Math.Max(24, right - left);

            var pieces = BuildPieces(paragraph, baseRun);
            var lines = WrapLines(pieces, width, format.FirstLineIndentPt);

            _y += format.SpaceBeforePt;

            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                var lineHeight = line.Height * format.LineSpacing;

                EnsureRoom(lineHeight);

                var indent = index == 0 ? Math.Max(0, format.FirstLineIndentPt) : 0;

                if (index == 0 && marker is not null)
                {
                    var markerFont = GetFont(baseRun);
                    Graphics.DrawString(
                        marker,
                        markerFont,
                        BrushFor(baseRun.ColorHex),
                        new XPoint(left - 18, _y + line.Ascent));
                }

                DrawLine(line, left + indent, width - indent, format.Alignment, isLast: index == lines.Count - 1);
                _y += lineHeight;
            }

            _y += format.SpaceAfterPt;
        }

        /// <summary>
        /// Draws one wrapped line, distributing slack for justified text.
        /// </summary>
        private void DrawLine(LayoutLine line, double left, double width, TextAlign alignment, bool isLast)
        {
            var slack = Math.Max(0, width - line.Width);

            var x = alignment switch
            {
                TextAlign.Center => left + slack / 2,
                TextAlign.Right => left + slack,
                _ => left
            };

            // The last line of a justified paragraph is set flush left; stretching it
            // is the classic mistake that leaves a two-word line spanning the measure.
            var extraPerGap = 0.0;
            if (alignment == TextAlign.Justify && !isLast && line.GapCount > 0 && slack > 0)
            {
                extraPerGap = slack / line.GapCount;
            }

            foreach (var piece in line.Pieces)
            {
                var font = GetFont(piece.Format);
                var baseline = _y + line.Ascent;

                if (piece.Format.HighlightHex is { } highlight)
                {
                    Graphics.DrawRectangle(
                        BrushFor(highlight),
                        new XRect(x, _y, piece.Width, line.Height));
                }

                Graphics.DrawString(piece.Text, font, BrushFor(piece.Format.ColorHex), new XPoint(x, baseline));
                x += piece.Width + (piece.IsGap ? extraPerGap : 0);
            }
        }

        private static RunFormatting ResolveBaseRun(DocumentParagraph paragraph)
        {
            var first = paragraph.Inlines.OfType<TextRun>().FirstOrDefault()?.Format
                        ?? paragraph.Inlines.OfType<HyperlinkRun>().FirstOrDefault()?.Runs.FirstOrDefault()?.Format
                        ?? RunFormatting.Default;

            if (!paragraph.IsHeading)
            {
                return first;
            }

            // Headings get their weight and size from the outline level unless the
            // source already stated one, so a heading imported from PDF or TXT still
            // looks like a heading.
            var size = first.FontSizePt ?? paragraph.OutlineLevel switch
            {
                1 => 20.0,
                2 => 16.0,
                3 => 14.0,
                4 => 12.5,
                5 => 11.5,
                _ => 11.0
            };

            return first with { Bold = true, FontSizePt = size };
        }

        private static string? BuildListMarker(DocumentParagraph paragraph, Dictionary<int, int> counters)
        {
            if (paragraph.List is not { } list)
            {
                counters.Clear();
                return null;
            }

            if (list.Kind == ListKind.Bullet)
            {
                return new[] { "•", "◦", "▪" }[list.Level % 3];
            }

            counters.TryGetValue(list.Level, out var current);
            current++;
            counters[list.Level] = current;

            foreach (var deeper in counters.Keys.Where(level => level > list.Level).ToArray())
            {
                counters.Remove(deeper);
            }

            return current.ToString(CultureInfo.InvariantCulture) + ".";
        }

        // ── Line breaking ────────────────────────────────────────────────────

        /// <summary>
        /// Flattens a paragraph into measurable pieces: words and the gaps between them.
        /// </summary>
        /// <remarks>
        /// Words and gaps are separate so justification has something to stretch, and
        /// so a wrap never leaves a trailing space contributing to a line's width.
        /// </remarks>
        private List<LayoutPiece> BuildPieces(DocumentParagraph paragraph, RunFormatting baseRun)
        {
            var pieces = new List<LayoutPiece>();

            void AddText(string text, RunFormatting format)
            {
                var resolved = paragraph.IsHeading ? format.InheritFrom(baseRun) : format;
                var index = 0;

                while (index < text.Length)
                {
                    if (char.IsWhiteSpace(text[index]))
                    {
                        var start = index;
                        while (index < text.Length && char.IsWhiteSpace(text[index]) && text[index] != '\n')
                        {
                            index++;
                        }

                        if (index > start)
                        {
                            pieces.Add(MakePiece(text[start..index], resolved, isGap: true));
                        }

                        if (index < text.Length && text[index] == '\n')
                        {
                            pieces.Add(LayoutPiece.Break(resolved));
                            index++;
                        }

                        continue;
                    }

                    var wordStart = index;
                    while (index < text.Length && !char.IsWhiteSpace(text[index]))
                    {
                        index++;
                    }

                    pieces.Add(MakePiece(text[wordStart..index], resolved, isGap: false));
                }
            }

            foreach (var inline in paragraph.Inlines)
            {
                switch (inline)
                {
                    case TextRun run:
                        AddText(run.Text, run.Format);
                        break;

                    case LineBreakRun:
                        pieces.Add(LayoutPiece.Break(baseRun));
                        break;

                    case HyperlinkRun link:
                        foreach (var run in link.Runs)
                        {
                            AddText(run.Text, run.Format with { Underline = true, ColorHex = run.Format.ColorHex ?? "#0563C1" });
                        }

                        break;

                    case ImageRun image when !string.IsNullOrWhiteSpace(image.Description):
                        AddText($"[{image.Description}]", baseRun with { Italic = true });
                        break;
                }
            }

            return pieces;
        }

        private LayoutPiece MakePiece(string text, RunFormatting format, bool isGap)
        {
            var font = GetFont(format);
            var size = Graphics.MeasureString(text, font);
            return new LayoutPiece(text, format, size.Width, font.GetHeight(), font.Metrics.Ascent / (double)font.Metrics.UnitsPerEm * font.Size, isGap, IsBreak: false);
        }

        private List<LayoutLine> WrapLines(List<LayoutPiece> pieces, double width, double firstLineIndent)
        {
            var lines = new List<LayoutLine>();
            var current = new List<LayoutPiece>();
            var currentWidth = 0.0;
            var available = width - Math.Max(0, firstLineIndent);

            void Flush()
            {
                // Trailing gaps never belong to a line: they would offset centred and
                // right-aligned text by the width of a space.
                while (current.Count > 0 && current[^1].IsGap)
                {
                    current.RemoveAt(current.Count - 1);
                }

                lines.Add(LayoutLine.From(current));
                current = [];
                currentWidth = 0;
                available = width;
            }

            foreach (var piece in pieces)
            {
                if (piece.IsBreak)
                {
                    Flush();
                    continue;
                }

                // A leading gap on a continuation line is the remains of the wrap.
                if (piece.IsGap && current.Count == 0)
                {
                    continue;
                }

                if (currentWidth + piece.Width > available && current.Count > 0 && !piece.IsGap)
                {
                    Flush();
                }

                // A single word wider than the measure cannot be wrapped; it is drawn
                // long rather than dropped, which is what every editor does.
                current.Add(piece);
                currentWidth += piece.Width;
            }

            Flush();

            if (lines.Count == 0)
            {
                lines.Add(LayoutLine.From([]));
            }

            // An empty paragraph still occupies a line, or blank lines vanish and the
            // document loses the writer's spacing.
            for (var index = 0; index < lines.Count; index++)
            {
                if (lines[index].Height <= 0)
                {
                    var font = GetFont(RunFormatting.Default);
                    lines[index] = LayoutLine.Empty(font.GetHeight(), font.Metrics.Ascent / (double)font.Metrics.UnitsPerEm * font.Size);
                }
            }

            return lines;
        }

        // ── Tables ───────────────────────────────────────────────────────────

        public void DrawTable(DocumentTable table)
        {
            if (table.Rows.Count == 0)
            {
                return;
            }

            var columns = Math.Max(1, table.ColumnCount);
            var left = page.MarginLeftPt;
            var totalWidth = page.ContentWidthPt;

            var widths = table.ColumnWidths.Count == columns
                ? table.ColumnWidths.Select(fraction => fraction * totalWidth).ToArray()
                : Enumerable.Repeat(totalWidth / columns, columns).ToArray();

            var pen = new XPen(XColor.FromArgb(191, 191, 191), 0.6);
            const double padding = 4;

            foreach (var row in table.Rows)
            {
                var cellTexts = row.Cells.Select(cell => cell.ToPlainText()).ToArray();
                var cellFormat = row.IsHeader ? new RunFormatting(Bold: true) : RunFormatting.Default;

                // Measure every cell first: the row is as tall as its tallest cell,
                // and it has to fit on the page as a unit or the borders break apart.
                var cellLines = new List<LayoutLine>[cellTexts.Length];
                var rowHeight = 0.0;

                for (var index = 0; index < cellTexts.Length; index++)
                {
                    var columnWidth = ColumnWidth(widths, row, index);
                    var paragraph = DocumentParagraph.FromText(cellTexts[index], run: cellFormat);
                    cellLines[index] = WrapLines(BuildPieces(paragraph, cellFormat), columnWidth - padding * 2, 0);
                    rowHeight = Math.Max(rowHeight, cellLines[index].Sum(line => line.Height) + padding * 2);
                }

                EnsureRoom(rowHeight);

                var x = left;
                for (var index = 0; index < cellTexts.Length; index++)
                {
                    var columnWidth = ColumnWidth(widths, row, index);

                    Graphics.DrawRectangle(pen, new XRect(x, _y, columnWidth, rowHeight));

                    var savedY = _y;
                    _y += padding;
                    foreach (var line in cellLines[index])
                    {
                        DrawLine(line, x + padding, columnWidth - padding * 2, TextAlign.Left, isLast: true);
                        _y += line.Height;
                    }

                    _y = savedY;
                    x += columnWidth;
                }

                _y += rowHeight;
            }

            _y += 6;
        }

        private static double ColumnWidth(double[] widths, TableRow row, int cellIndex)
        {
            var span = Math.Max(1, row.Cells[cellIndex].ColumnSpan);
            var width = 0.0;
            for (var offset = 0; offset < span && cellIndex + offset < widths.Length; offset++)
            {
                width += widths[cellIndex + offset];
            }

            return width > 0 ? width : widths.Length > 0 ? widths[0] : 100;
        }

        // ── Fonts and brushes ────────────────────────────────────────────────

        private XFont GetFont(RunFormatting format)
        {
            var family = SystemFontResolver.Instance.ResolveFamilyName(format.FontFamily ?? DefaultFontFamily);
            var size = Math.Clamp(format.FontSizePt ?? DefaultFontSizePt, 4, 300);
            var key = (family, size, format.Bold, format.Italic, format.Underline, format.Strikethrough);

            if (_fonts.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var style = XFontStyleEx.Regular;
            if (format.Bold)
            {
                style |= XFontStyleEx.Bold;
            }

            if (format.Italic)
            {
                style |= XFontStyleEx.Italic;
            }

            if (format.Underline)
            {
                style |= XFontStyleEx.Underline;
            }

            if (format.Strikethrough)
            {
                style |= XFontStyleEx.Strikeout;
            }

            var font = new XFont(family, size, style, new XPdfFontOptions(PdfFontEncoding.Unicode));
            _fonts[key] = font;
            return font;
        }

        private static XBrush BrushFor(string? hex)
        {
            if (hex is not { Length: 7 } || hex[0] != '#')
            {
                return XBrushes.Black;
            }

            try
            {
                return new XSolidBrush(XColor.FromArgb(
                    Convert.ToInt32(hex.Substring(1, 2), 16),
                    Convert.ToInt32(hex.Substring(3, 2), 16),
                    Convert.ToInt32(hex.Substring(5, 2), 16)));
            }
            catch (FormatException)
            {
                return XBrushes.Black;
            }
        }
    }

    private readonly record struct LayoutPiece(
        string Text,
        RunFormatting Format,
        double Width,
        double Height,
        double Ascent,
        bool IsGap,
        bool IsBreak)
    {
        public static LayoutPiece Break(RunFormatting format) =>
            new(string.Empty, format, 0, 0, 0, false, true);
    }

    private readonly record struct LayoutLine(
        IReadOnlyList<LayoutPiece> Pieces,
        double Width,
        double Height,
        double Ascent,
        int GapCount)
    {
        public static LayoutLine From(List<LayoutPiece> pieces) => new(
            pieces.ToArray(),
            pieces.Sum(piece => piece.Width),
            pieces.Count == 0 ? 0 : pieces.Max(piece => piece.Height),
            pieces.Count == 0 ? 0 : pieces.Max(piece => piece.Ascent),
            pieces.Count(piece => piece.IsGap));

        public static LayoutLine Empty(double height, double ascent) => new([], 0, height, ascent, 0);
    }
}
