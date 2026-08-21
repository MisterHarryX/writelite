using WriteLite.Documents;
using WriteLite.Documents.Model;

namespace WriteLite.Tests.Documents;

/// <summary>
/// The conversion matrix, exercised as real file-to-file conversions.
/// </summary>
/// <remarks>
/// Every advertised conversion is tested here. The rule the product states is that
/// a conversion is only listed as supported if it actually works, and this is what
/// keeps that claim honest — the matrix in the documentation is generated from the
/// same <see cref="IDocumentConverter.CanConvert"/> these tests drive.
/// </remarks>
[TestClass]
public sealed class DocumentConversionTests
{
    private static readonly DocumentFormat[] Formats =
    [
        DocumentFormat.Docx,
        DocumentFormat.Odt,
        DocumentFormat.Txt,
        DocumentFormat.Pdf
    ];

    [TestMethod]
    public void Every_pair_of_supported_formats_is_convertible()
    {
        var converter = new DocumentConversionService();

        foreach (var from in Formats)
        {
            foreach (var to in Formats.Where(format => format != from))
            {
                Assert.IsTrue(
                    converter.CanConvert(from, to),
                    $"{from} → {to} is advertised but not supported.");
            }
        }
    }

    [TestMethod]
    public void Unknown_formats_are_not_convertible()
    {
        var converter = new DocumentConversionService();

        Assert.IsFalse(converter.CanConvert(DocumentFormat.Unknown, DocumentFormat.Docx));
        Assert.IsFalse(converter.CanConvert(DocumentFormat.Docx, DocumentFormat.Unknown));
    }

    /// <summary>
    /// Runs all twelve conversions and checks the text survived each one.
    /// </summary>
    /// <remarks>
    /// One test rather than twelve because the sources have to be produced first,
    /// and producing them per case would triple the run time for no extra coverage.
    /// The failure message names the pair, so a break is still pinpointed.
    /// </remarks>
    [TestMethod]
    public async Task Every_conversion_produces_a_readable_document()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var serializer = new DocumentSerializer();
        var converter = new DocumentConversionService(serializer);
        var sample = DocumentFixtures.BuildRichSample();

        foreach (var format in Formats)
        {
            await serializer.SaveAsync(sample, workspace.PathFor(format), format);
        }

        foreach (var from in Formats)
        {
            foreach (var to in Formats.Where(format => format != from))
            {
                var input = workspace.PathFor(from);
                var output = Path.Combine(workspace.Root, $"{from}-to-{to}{DocumentFormats.Extension(to)}");

                var result = await converter.ConvertAsync(input, output, to);

                Assert.IsTrue(File.Exists(output), $"{from} → {to} produced no file.");
                Assert.IsGreaterThan(0, new FileInfo(output).Length, $"{from} → {to} produced an empty file.");
                Assert.AreEqual(from, result.From);
                Assert.AreEqual(to, result.To);

                var reloaded = (await serializer.LoadAsync(output)).Document.ToPlainText();
                Assert.Contains(
                    "Ёжик",
                    reloaded,
                    $"{from} → {to} lost the document text.");
            }
        }
    }

    [TestMethod]
    public async Task Conversion_preserves_headings_and_lists_between_rich_formats()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var serializer = new DocumentSerializer();
        var converter = new DocumentConversionService(serializer);

        var source = workspace.PathFor(DocumentFormat.Docx);
        await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), source, DocumentFormat.Docx);

        var output = Path.Combine(workspace.Root, "converted.odt");
        await converter.ConvertAsync(source, output, DocumentFormat.Odt);

        var paragraphs = (await serializer.LoadAsync(output)).Document
            .Blocks.OfType<DocumentParagraph>()
            .ToArray();

        Assert.IsTrue(paragraphs.Any(paragraph => paragraph.OutlineLevel == 1));
        Assert.AreEqual(3, paragraphs.Count(paragraph => paragraph.List?.Kind == ListKind.Numbered));
        Assert.AreEqual(2, paragraphs.Count(paragraph => paragraph.List?.Kind == ListKind.Bullet));
        Assert.IsTrue(paragraphs.Any(paragraph =>
            paragraph.Inlines.OfType<TextRun>().Any(run => run.Format.Bold)));
    }

    [TestMethod]
    public async Task Converting_a_file_onto_itself_is_refused()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Docx);

        await new DocumentSerializer().SaveAsync(
            DocumentFixtures.BuildRichSample(),
            path,
            DocumentFormat.Docx);

        var exception = await Assert.ThrowsExactlyAsync<DocumentFormatException>(
            () => new DocumentConversionService().ConvertAsync(path, path, DocumentFormat.Docx));

        Assert.Contains("нельзя перезаписать", exception.UserMessage);
    }

    [TestMethod]
    public async Task Conversion_reports_what_it_could_not_preserve()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var serializer = new DocumentSerializer();
        var source = workspace.PathFor(DocumentFormat.Docx);

        await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), source, DocumentFormat.Docx);

        var result = await new DocumentConversionService(serializer).ConvertAsync(
            source,
            Path.Combine(workspace.Root, "flat.txt"),
            DocumentFormat.Txt);

        Assert.IsTrue(
            result.Warnings.Any(warning => warning.Code == "txt-formatting-dropped"),
            "Converting to TXT drops formatting and must say so.");
    }

    [TestMethod]
    public async Task Conversion_can_be_cancelled_without_leaving_a_file()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var serializer = new DocumentSerializer();
        var source = workspace.PathFor(DocumentFormat.Docx);
        var output = Path.Combine(workspace.Root, "cancelled.pdf");

        await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), source, DocumentFormat.Docx);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => new DocumentConversionService(serializer).ConvertAsync(
                source,
                output,
                DocumentFormat.Pdf,
                progress: null,
                cancellationToken: cancellation.Token));

        Assert.IsFalse(File.Exists(output));
    }

    [TestMethod]
    public async Task Conversion_progress_runs_from_import_through_export()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var serializer = new DocumentSerializer();
        var source = workspace.PathFor(DocumentFormat.Docx);

        await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), source, DocumentFormat.Docx);

        var fractions = new List<double>();
        var progress = new Progress<DocumentProgress>(report =>
        {
            if (report.Fraction is { } fraction)
            {
                fractions.Add(fraction);
            }
        });

        await new DocumentConversionService(serializer).ConvertAsync(
            source,
            Path.Combine(workspace.Root, "progress.odt"),
            DocumentFormat.Odt,
            progress);

        await Task.Delay(120);

        Assert.IsNotEmpty(fractions);
        Assert.IsTrue(fractions.All(fraction => fraction is >= 0 and <= 1));

        // Import occupies the first half of the bar and export the second, so a
        // report above one half proves the whole pipeline was covered.
        Assert.IsTrue(fractions.Any(fraction => fraction > 0.5));
    }
}
