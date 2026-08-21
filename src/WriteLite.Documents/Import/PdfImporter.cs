using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using WriteLite.Documents.Model;
using WriteLite.Documents.Pdf;

namespace WriteLite.Documents.Import;

/// <summary>
/// PDF → an editable WriteLite document, reconstructed from glyph positions.
/// </summary>
/// <remarks>
/// A PDF has no paragraphs. It has glyphs at coordinates, and everything above that
/// — words, lines, paragraphs, headings, lists — has to be inferred from geometry.
/// This importer is explicit about that: it never claims to have recovered the
/// author's structure, only to have produced text a person can read and edit.
///
/// The pipeline is: words → lines (by baseline) → columns (by vertical gutter) →
/// paragraphs (by spacing, indentation and short final lines) → headings and list
/// items (by relative type size and leading markers). Page boundaries survive as
/// explicit page breaks.
///
/// A scanned PDF contains no text at all, which this reports rather than opening an
/// empty document; <see cref="PdfTextReconstructor"/> is where an OCR stage would
/// attach when one exists.
/// </remarks>
public sealed class PdfImporter : IDocumentImporter
{
    public DocumentFormat Format => DocumentFormat.Pdf;

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
        progress?.Report(new DocumentProgress("Открытие PDF", null));

        PdfDocument package;
        try
        {
            package = PdfDocument.Open(stream, new ParsingOptions
            {
                // Real-world PDFs violate the specification constantly. Strict parsing
                // rejects files every other reader opens without complaint.
                UseLenientParsing = true,
                SkipMissingFonts = true,
                ClipPaths = false
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new DocumentFormatException(
                $"pdf-open-failed: {exception.GetType().Name}",
                "Этот файл PDF повреждён или защищён и не может быть прочитан.",
                exception);
        }

        using (package)
        {
            var document = WlDocument.Empty();
            document.Metadata.SourceFormat = DocumentFormat.Pdf;
            document.Metadata.SourcePath = options.SourcePath;
            ReadMetadata(package, document.Metadata, options.SourcePath);

            var section = document.CurrentSection();
            var pageCount = package.NumberOfPages;
            var characters = 0;
            var pagesWithoutText = 0;
            var truncated = false;

            for (var number = 1; number <= pageCount; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(DocumentProgress.Of($"Страница {number} из {pageCount}", number - 1, pageCount));

                if (characters >= options.MaxCharacters)
                {
                    truncated = true;
                    break;
                }

                Page page;
                IReadOnlyList<Word> words;
                try
                {
                    page = package.GetPage(number);
                    words = page.GetWords().Where(word => !string.IsNullOrWhiteSpace(word.Text)).ToArray();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    warnings.Add(new DocumentWarning(
                        "pdf-page-unreadable",
                        $"Страницу {number} не удалось прочитать — она пропущена."));
                    continue;
                }

                if (number > 1)
                {
                    section.Blocks.Add(new PageBreak());
                    section.Page = section.Page with { WidthPt = section.Page.WidthPt };
                }

                if (words.Count == 0)
                {
                    pagesWithoutText++;
                    continue;
                }

                if (number == 1)
                {
                    section.Page = new PageSetup(page.Width, page.Height, 56.7, 56.7, 56.7, 56.7);
                }

                foreach (var block in PdfTextReconstructor.Reconstruct(words, page.Width, page.Height))
                {
                    section.Blocks.Add(block);
                    if (block is DocumentParagraph paragraph)
                    {
                        characters += paragraph.ToPlainText().Length;
                    }
                }
            }

            if (truncated)
            {
                warnings.Add(new DocumentWarning("pdf-truncated", "Документ очень большой — открыта первая часть."));
            }

            if (pagesWithoutText == pageCount && pageCount > 0)
            {
                throw new DocumentFormatException(
                    "pdf-no-text-layer",
                    "В этом PDF нет текстового слоя — вероятно, это скан. " +
                    "WriteLite пока не распознаёт текст на изображениях.");
            }

            if (pagesWithoutText > 0)
            {
                warnings.Add(new DocumentWarning(
                    "pdf-pages-without-text",
                    $"На {pagesWithoutText} стр. нет текстового слоя — вероятно, это изображения."));
            }

            warnings.Add(new DocumentWarning(
                "pdf-structure-reconstructed",
                "Структура PDF восстановлена по расположению текста и может отличаться от оригинала."));

            if (section.Blocks.Count == 0)
            {
                section.Blocks.Add(DocumentParagraph.FromText(string.Empty));
            }

            progress?.Report(new DocumentProgress("Готово", 1));
            return new DocumentImportResult(document, warnings);
        }
    }

    private static void ReadMetadata(PdfDocument package, DocumentMetadata metadata, string? sourcePath)
    {
        metadata.Title = sourcePath is null ? null : Path.GetFileNameWithoutExtension(sourcePath);

        try
        {
            var information = package.Information;
            if (!string.IsNullOrWhiteSpace(information.Title))
            {
                metadata.Title = information.Title;
            }

            metadata.Author = information.Author;
            metadata.Subject = information.Subject;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The information dictionary is optional and frequently malformed.
        }
    }
}
