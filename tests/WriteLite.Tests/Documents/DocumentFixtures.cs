using WriteLite.Documents;
using WriteLite.Documents.Model;

namespace WriteLite.Tests.Documents;

/// <summary>
/// Shared sample documents and a scratch directory for the document-engine tests.
/// </summary>
/// <remarks>
/// The sample deliberately exercises every feature the model claims to support —
/// headings, mixed inline formatting, alignment, spacing, indentation, both list
/// kinds, a table, a hyperlink and a page break — and it is written in Russian with
/// English mixed in, because the encoding path is the one most likely to break
/// silently.
/// </remarks>
internal static class DocumentFixtures
{
    public const string CyrillicSentence = "Ёжик нёс ёлку через «дорогу» — было холодно.";

    public static WlDocument BuildRichSample()
    {
        var document = WlDocument.Empty();
        document.Metadata.Title = "Тестовый документ";
        document.Metadata.Author = "WriteLite";

        var section = document.CurrentSection();

        var heading = DocumentParagraph.FromText("Заголовок первого уровня");
        heading.OutlineLevel = 1;
        section.Blocks.Add(heading);

        var body = new DocumentParagraph
        {
            Format = new ParagraphFormatting(
                TextAlign.Justify,
                LineSpacing: 1.5,
                SpaceBeforePt: 6,
                SpaceAfterPt: 12,
                IndentLeftPt: 18,
                IndentRightPt: 0,
                FirstLineIndentPt: 24)
        };
        body.Inlines.Add(new TextRun("Обычный текст, "));
        body.Inlines.Add(new TextRun("жирный", new RunFormatting(Bold: true)));
        body.Inlines.Add(new TextRun(", "));
        body.Inlines.Add(new TextRun("курсив", new RunFormatting(Italic: true)));
        body.Inlines.Add(new TextRun(", "));
        body.Inlines.Add(new TextRun("подчёркнутый", new RunFormatting(Underline: true)));
        body.Inlines.Add(new TextRun(", "));
        body.Inlines.Add(new TextRun("зачёркнутый", new RunFormatting(Strikethrough: true)));
        body.Inlines.Add(new TextRun(", "));
        body.Inlines.Add(new TextRun(
            "цветной",
            new RunFormatting(FontFamily: "Georgia", FontSizePt: 14, ColorHex: "#EC6C08")));
        body.Inlines.Add(new TextRun(". " + CyrillicSentence + " And some English too."));
        section.Blocks.Add(body);

        var link = new DocumentParagraph();
        link.Inlines.Add(new TextRun("Ссылка: "));
        var hyperlink = new HyperlinkRun("https://example.org/article");
        hyperlink.Runs.Add(new TextRun("пример"));
        link.Inlines.Add(hyperlink);
        section.Blocks.Add(link);

        for (var index = 1; index <= 3; index++)
        {
            var item = DocumentParagraph.FromText($"Нумерованный пункт {index}");
            item.List = ListInfo.Numbered();
            section.Blocks.Add(item);
        }

        foreach (var text in new[] { "Маркер один", "Маркер два" })
        {
            var item = DocumentParagraph.FromText(text);
            item.List = ListInfo.Bullet();
            section.Blocks.Add(item);
        }

        section.Blocks.Add(BuildTable());
        section.Blocks.Add(new PageBreak());

        var second = DocumentParagraph.FromText("Вторая страница после разрыва.");
        second.OutlineLevel = 2;
        section.Blocks.Add(second);

        return document;
    }

    private static DocumentTable BuildTable()
    {
        var table = new DocumentTable();

        var header = new TableRow { IsHeader = true };
        foreach (var caption in new[] { "Формат", "Импорт", "Экспорт" })
        {
            var cell = new TableCell();
            cell.Blocks.Add(DocumentParagraph.FromText(caption));
            header.Cells.Add(cell);
        }

        table.Rows.Add(header);

        foreach (var (format, import, export) in new[]
                 {
                     ("DOCX", "да", "да"),
                     ("ODT", "да", "да"),
                     ("PDF", "да", "да")
                 })
        {
            var row = new TableRow();
            foreach (var value in new[] { format, import, export })
            {
                var cell = new TableCell();
                cell.Blocks.Add(DocumentParagraph.FromText(value));
                row.Cells.Add(cell);
            }

            table.Rows.Add(row);
        }

        return table;
    }

    /// <summary>A scratch directory that cleans up after itself.</summary>
    public sealed class Workspace : IDisposable
    {
        public Workspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "wl-doc-tests-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string PathFor(DocumentFormat format, string name = "sample") =>
            Path.Combine(Root, name + DocumentFormats.Extension(format));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory must never fail a test run.
            }
        }
    }
}
