using System.Text;
using WriteLite.Documents.Model;

namespace WriteLite.Documents.Export;

/// <summary>
/// Plain text: content and paragraph structure, nothing else.
/// </summary>
/// <remarks>
/// Always UTF-8 with a BOM. Without one, Notepad and a good deal of Windows
/// tooling still fall back to the ANSI code page and render Cyrillic as mojibake,
/// which is exactly the failure this product exists to avoid.
///
/// Structure that TXT cannot express is rendered rather than dropped: list markers
/// are written out, table rows become tab-separated lines, and a page break becomes
/// a blank line. That is lossy and reported as such, but it stays readable.
/// </remarks>
public sealed class TxtExporter : IDocumentExporter
{
    public DocumentFormat Format => DocumentFormat.Txt;

    public async Task<IReadOnlyList<DocumentWarning>> ExportAsync(
        WlDocument document,
        Stream stream,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<DocumentWarning>();
        var builder = new StringBuilder();
        var blocks = document.Blocks.ToArray();
        var counters = new Dictionary<int, int>();
        var droppedFormatting = false;
        var droppedTables = false;

        for (var index = 0; index < blocks.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index % 256 == 0)
            {
                progress?.Report(DocumentProgress.Of("Запись текста", index, blocks.Length));
            }

            switch (blocks[index])
            {
                case DocumentParagraph paragraph:
                    AppendParagraph(builder, paragraph, counters);
                    if (!droppedFormatting && HasVisualFormatting(paragraph))
                    {
                        droppedFormatting = true;
                    }

                    break;

                case DocumentTable table:
                    droppedTables = true;
                    AppendTable(builder, table);
                    break;

                case PageBreak:
                    builder.Append('\n');
                    counters.Clear();
                    break;
            }
        }

        if (droppedFormatting)
        {
            warnings.Add(new DocumentWarning("txt-formatting-dropped", "Оформление текста в TXT не сохраняется."));
        }

        if (droppedTables)
        {
            warnings.Add(new DocumentWarning("txt-tables-flattened", "Таблицы записаны строками с табуляцией."));
        }

        // The preamble has to be written explicitly: Encoding.GetBytes never emits
        // one, whatever the encoder was constructed with — only StreamWriter does.
        // Getting this wrong produces a BOM-less file that Notepad opens as ANSI,
        // which is exactly the mojibake this export exists to prevent.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await stream.WriteAsync(encoding.GetPreamble(), cancellationToken).ConfigureAwait(false);
        await stream
            .WriteAsync(encoding.GetBytes(builder.ToString().ReplaceLineEndings("\r\n")), cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new DocumentProgress("Готово", 1));
        return warnings;
    }

    private static void AppendParagraph(StringBuilder builder, DocumentParagraph paragraph, Dictionary<int, int> counters)
    {
        var text = paragraph.ToPlainText();

        if (paragraph.List is { } list)
        {
            var indent = new string(' ', Math.Clamp(list.Level, 0, 8) * 2);
            if (list.Kind == ListKind.Numbered)
            {
                counters.TryGetValue(list.Level, out var current);
                current++;
                counters[list.Level] = current;

                // Deeper counters restart under a new parent item.
                foreach (var deeper in counters.Keys.Where(level => level > list.Level).ToArray())
                {
                    counters.Remove(deeper);
                }

                builder.Append(indent).Append(current).Append(". ").Append(text).Append('\n');
                return;
            }

            builder.Append(indent).Append("• ").Append(text).Append('\n');
            return;
        }

        counters.Clear();
        builder.Append(text).Append('\n');
    }

    private static void AppendTable(StringBuilder builder, DocumentTable table)
    {
        foreach (var row in table.Rows)
        {
            builder.Append(string.Join('\t', row.Cells.Select(cell => cell.ToPlainText()))).Append('\n');
        }
    }

    private static bool HasVisualFormatting(DocumentParagraph paragraph) =>
        paragraph.IsHeading
        || paragraph.Format != ParagraphFormatting.Default
        || paragraph.Inlines.OfType<TextRun>().Any(run => !run.Format.IsDefault);
}
