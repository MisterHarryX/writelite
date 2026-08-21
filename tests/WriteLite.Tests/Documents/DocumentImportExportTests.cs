using System.Text;
using WriteLite.Documents;
using WriteLite.Documents.Import;
using WriteLite.Documents.Model;

namespace WriteLite.Tests.Documents;

/// <summary>
/// Import and export for every supported format.
/// </summary>
/// <remarks>
/// These run against real files written to disk and read back through the real
/// serializer — not against mocks. A document engine's failures are almost always
/// in the bytes, and an in-memory test of the model would have caught none of the
/// defects this suite actually caught.
/// </remarks>
[TestClass]
public sealed class DocumentImportExportTests
{
    // ── TXT ──────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Txt_import_reads_utf8_with_bom_and_keeps_cyrillic()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Txt);

        await File.WriteAllTextAsync(
            path,
            "Первая строка\r\nВторая строка\r\n" + DocumentFixtures.CyrillicSentence,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await new DocumentSerializer().LoadAsync(path);
        var text = result.Document.ToPlainText();

        Assert.Contains("Первая строка", text);
        Assert.Contains(DocumentFixtures.CyrillicSentence, text);
        Assert.DoesNotContain("﻿", text, "The byte-order mark must not survive into the text.");
        Assert.AreEqual(3, result.Document.Blocks.OfType<DocumentParagraph>().Count());
    }

    [TestMethod]
    public async Task Txt_import_reads_utf8_without_bom()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Txt);

        await File.WriteAllTextAsync(path, DocumentFixtures.CyrillicSentence, new UTF8Encoding(false));

        var result = await new DocumentSerializer().LoadAsync(path);

        Assert.Contains(DocumentFixtures.CyrillicSentence, result.Document.ToPlainText());
        Assert.IsFalse(result.HasWarnings, "Valid UTF-8 must not be reported as a guessed encoding.");
    }

    /// <summary>
    /// Legacy Windows-1251 is still what a great deal of older Russian text is in.
    /// </summary>
    [TestMethod]
    public async Task Txt_import_detects_windows1251()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Txt);

        const string original = "Привет, мир! Это текст в кодировке Windows-1251.";
        await File.WriteAllBytesAsync(path, Encoding.GetEncoding(1251).GetBytes(original));

        var result = await new DocumentSerializer().LoadAsync(path);

        Assert.Contains(original, result.Document.ToPlainText());
        Assert.IsTrue(
            result.Warnings.Any(warning => warning.Code == "txt-encoding-guessed"),
            "A legacy encoding must be reported rather than applied silently.");
    }

    [TestMethod]
    public void Encoding_detector_prefers_utf8_over_legacy_guessing()
    {
        var bytes = new UTF8Encoding(false).GetBytes(DocumentFixtures.CyrillicSentence);
        var detection = TextEncodingDetector.Detect(bytes);

        Assert.IsFalse(detection.IsFallback);
        Assert.AreEqual("UTF-8", detection.Name);
    }

    [TestMethod]
    public async Task Txt_export_writes_utf8_bom_so_windows_tools_read_cyrillic()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Txt, "export");

        await new DocumentSerializer().SaveAsync(
            DocumentFixtures.BuildRichSample(),
            path,
            DocumentFormat.Txt);

        var bytes = await File.ReadAllBytesAsync(path);

        Assert.IsTrue(
            bytes is [0xEF, 0xBB, 0xBF, ..],
            "TXT export must carry a BOM or Windows tools fall back to the ANSI code page.");
        Assert.Contains(DocumentFixtures.CyrillicSentence, await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task Txt_export_renders_list_markers_it_cannot_otherwise_express()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Txt, "lists");

        var result = await new DocumentSerializer().SaveAsync(
            DocumentFixtures.BuildRichSample(),
            path,
            DocumentFormat.Txt);

        var text = await File.ReadAllTextAsync(path);

        Assert.Contains("1. Нумерованный пункт 1", text);
        Assert.Contains("• Маркер один", text);
        Assert.IsTrue(
            result.Warnings.Any(warning => warning.Code == "txt-formatting-dropped"),
            "Losing formatting must be reported, not silent.");
    }

    // ── DOCX ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Docx_round_trip_preserves_text_and_inline_formatting()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Docx);
        var serializer = new DocumentSerializer();

        await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), path, DocumentFormat.Docx);
        var reloaded = (await serializer.LoadAsync(path)).Document;

        var paragraphs = reloaded.Blocks.OfType<DocumentParagraph>().ToArray();
        var runs = paragraphs.SelectMany(paragraph => paragraph.Inlines).OfType<TextRun>().ToArray();

        Assert.Contains(DocumentFixtures.CyrillicSentence, reloaded.ToPlainText());
        Assert.IsTrue(runs.Any(run => run.Format.Bold && run.Text.Contains("жирный")));
        Assert.IsTrue(runs.Any(run => run.Format.Italic && run.Text.Contains("курсив")));
        Assert.IsTrue(runs.Any(run => run.Format.Underline && run.Text.Contains("подчёркнутый")));
        Assert.IsTrue(runs.Any(run => run.Format.Strikethrough && run.Text.Contains("зачёркнутый")));
        Assert.IsTrue(runs.Any(run => run.Format.ColorHex == "#EC6C08"));
        Assert.IsTrue(runs.Any(run => run.Format.FontSizePt is 14));
    }

    [TestMethod]
    public async Task Docx_round_trip_preserves_structure()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Docx, "structure");
        var serializer = new DocumentSerializer();

        await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), path, DocumentFormat.Docx);
        var reloaded = (await serializer.LoadAsync(path)).Document;

        var paragraphs = reloaded.Blocks.OfType<DocumentParagraph>().ToArray();

        Assert.AreEqual(1, paragraphs.Count(paragraph => paragraph.OutlineLevel == 1));
        Assert.AreEqual(1, paragraphs.Count(paragraph => paragraph.OutlineLevel == 2));
        Assert.AreEqual(3, paragraphs.Count(paragraph => paragraph.List?.Kind == ListKind.Numbered));
        Assert.AreEqual(2, paragraphs.Count(paragraph => paragraph.List?.Kind == ListKind.Bullet));
        Assert.AreEqual(1, reloaded.Blocks.OfType<DocumentTable>().Count());
        Assert.AreEqual(1, reloaded.Blocks.OfType<PageBreak>().Count());
    }

    [TestMethod]
    public async Task Docx_round_trip_preserves_paragraph_layout()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Docx, "layout");
        var serializer = new DocumentSerializer();

        await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), path, DocumentFormat.Docx);
        var reloaded = (await serializer.LoadAsync(path)).Document;

        var justified = reloaded.Blocks
            .OfType<DocumentParagraph>()
            .Single(paragraph => paragraph.Format.Alignment == TextAlign.Justify);

        Assert.AreEqual(1.5, justified.Format.LineSpacing, 0.05);
        Assert.AreEqual(6, justified.Format.SpaceBeforePt, 0.6);
        Assert.AreEqual(12, justified.Format.SpaceAfterPt, 0.6);
        Assert.AreEqual(18, justified.Format.IndentLeftPt, 0.6);
        Assert.AreEqual(24, justified.Format.FirstLineIndentPt, 0.6);
    }

    [TestMethod]
    public async Task Docx_round_trip_preserves_tables_and_links()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Docx, "tables");
        var serializer = new DocumentSerializer();

        await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), path, DocumentFormat.Docx);
        var reloaded = (await serializer.LoadAsync(path)).Document;

        var table = reloaded.Blocks.OfType<DocumentTable>().Single();
        Assert.AreEqual(4, table.Rows.Count);
        Assert.AreEqual(3, table.ColumnCount);
        Assert.AreEqual("Формат", table.Rows[0].Cells[0].ToPlainText());
        Assert.IsTrue(table.Rows[0].IsHeader);

        var link = reloaded.Blocks
            .OfType<DocumentParagraph>()
            .SelectMany(paragraph => paragraph.Inlines)
            .OfType<HyperlinkRun>()
            .Single();

        Assert.AreEqual("пример", link.Text);
        Assert.Contains("example.org", link.Target!);
    }

    [TestMethod]
    public async Task Docx_import_rejects_a_damaged_file_with_a_readable_message()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Docx, "damaged");

        // A plausible-looking but structurally invalid package.
        await File.WriteAllBytesAsync(path, [0x50, 0x4B, 0x03, 0x04, 0x00, 0x01, 0x02, 0x03]);

        var exception = await Assert.ThrowsExactlyAsync<DocumentFormatException>(
            () => new DocumentSerializer().LoadAsync(path));

        Assert.Contains("DOCX", exception.UserMessage);
        Assert.DoesNotContain("Exception", exception.UserMessage);
    }

    // ── ODT ──────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Odt_round_trip_preserves_text_and_formatting()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Odt);
        var serializer = new DocumentSerializer();

        await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), path, DocumentFormat.Odt);
        var reloaded = (await serializer.LoadAsync(path)).Document;

        var runs = reloaded.Blocks
            .OfType<DocumentParagraph>()
            .SelectMany(paragraph => paragraph.Inlines)
            .OfType<TextRun>()
            .ToArray();

        Assert.Contains(DocumentFixtures.CyrillicSentence, reloaded.ToPlainText());
        Assert.IsTrue(runs.Any(run => run.Format.Bold && run.Text.Contains("жирный")));
        Assert.IsTrue(runs.Any(run => run.Format.Italic && run.Text.Contains("курсив")));
        Assert.IsTrue(runs.Any(run => run.Format.Underline));
        Assert.IsTrue(runs.Any(run => run.Format.Strikethrough));
        Assert.IsTrue(runs.Any(run => run.Format.ColorHex == "#EC6C08"));
    }

    [TestMethod]
    public async Task Odt_round_trip_preserves_structure()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Odt, "structure");
        var serializer = new DocumentSerializer();

        await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), path, DocumentFormat.Odt);
        var reloaded = (await serializer.LoadAsync(path)).Document;

        var paragraphs = reloaded.Blocks.OfType<DocumentParagraph>().ToArray();

        Assert.AreEqual(1, paragraphs.Count(paragraph => paragraph.OutlineLevel == 1));
        Assert.AreEqual(3, paragraphs.Count(paragraph => paragraph.List?.Kind == ListKind.Numbered));
        Assert.AreEqual(2, paragraphs.Count(paragraph => paragraph.List?.Kind == ListKind.Bullet));
        Assert.AreEqual(1, reloaded.Blocks.OfType<DocumentTable>().Count());
        Assert.AreEqual(1, reloaded.Blocks.OfType<PageBreak>().Count());
    }

    /// <summary>
    /// The mimetype entry has to be first and stored, or other readers reject the file.
    /// </summary>
    [TestMethod]
    public async Task Odt_export_writes_a_conformant_package()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Odt, "package");

        await new DocumentSerializer().SaveAsync(
            DocumentFixtures.BuildRichSample(),
            path,
            DocumentFormat.Odt);

        using var archive = System.IO.Compression.ZipFile.OpenRead(path);

        Assert.AreEqual("mimetype", archive.Entries[0].FullName);
        Assert.AreEqual(
            archive.Entries[0].Length,
            archive.Entries[0].CompressedLength,
            "The mimetype entry must be stored uncompressed.");

        foreach (var required in new[] { "content.xml", "styles.xml", "meta.xml", "META-INF/manifest.xml" })
        {
            Assert.IsNotNull(archive.GetEntry(required), $"Missing required part: {required}");
        }
    }

    [TestMethod]
    public async Task Odt_import_rejects_a_damaged_file_with_a_readable_message()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Odt, "damaged");

        await File.WriteAllTextAsync(path, "this is not a zip archive at all");

        var exception = await Assert.ThrowsExactlyAsync<DocumentFormatException>(
            () => new DocumentSerializer().LoadAsync(path));

        Assert.Contains("ODT", exception.UserMessage);
    }

    // ── PDF ──────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Pdf_export_then_extract_recovers_cyrillic_text()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Pdf);
        var serializer = new DocumentSerializer();

        await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), path, DocumentFormat.Pdf);
        var reloaded = (await serializer.LoadAsync(path)).Document;
        var text = reloaded.ToPlainText();

        // The whole point of embedding the font: Cyrillic must come back as letters,
        // never as boxes or question marks.
        Assert.Contains("Ёжик", text);
        Assert.Contains("зачёркнутый", text);
        Assert.DoesNotContain("□", text);
        Assert.IsFalse(
            text.Count(c => c == '?') > 3,
            "Cyrillic must not degrade to question marks in the exported PDF.");
    }

    [TestMethod]
    public async Task Pdf_import_reconstructs_headings_lists_and_page_breaks()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Pdf, "structure");
        var serializer = new DocumentSerializer();

        await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), path, DocumentFormat.Pdf);
        var result = await serializer.LoadAsync(path);

        var paragraphs = result.Document.Blocks.OfType<DocumentParagraph>().ToArray();

        Assert.IsTrue(paragraphs.Any(paragraph => paragraph.IsHeading), "No heading was reconstructed.");
        Assert.IsTrue(paragraphs.Any(paragraph => paragraph.List is not null), "No list item was reconstructed.");
        Assert.IsTrue(result.Document.Blocks.OfType<PageBreak>().Any(), "Page boundaries were not preserved.");
    }

    /// <summary>
    /// PDF reconstruction is inference, and the product must say so rather than
    /// claim a fidelity it cannot have.
    /// </summary>
    [TestMethod]
    public async Task Pdf_import_always_reports_that_structure_was_inferred()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Pdf, "warning");
        var serializer = new DocumentSerializer();

        await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), path, DocumentFormat.Pdf);
        var result = await serializer.LoadAsync(path);

        Assert.IsTrue(result.Warnings.Any(warning => warning.Code == "pdf-structure-reconstructed"));
    }

    [TestMethod]
    public async Task Pdf_import_rejects_a_damaged_file_with_a_readable_message()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Pdf, "damaged");

        await File.WriteAllTextAsync(path, "%PDF-1.7 but nothing else that makes sense");

        var exception = await Assert.ThrowsExactlyAsync<DocumentFormatException>(
            () => new DocumentSerializer().LoadAsync(path));

        Assert.Contains("PDF", exception.UserMessage);
    }

    // ── Serializer behaviour ─────────────────────────────────────────────────

    [TestMethod]
    public async Task Missing_file_reports_a_readable_message()
    {
        var exception = await Assert.ThrowsExactlyAsync<DocumentFormatException>(
            () => new DocumentSerializer().LoadAsync(
                Path.Combine(Path.GetTempPath(), "wl-does-not-exist-" + Guid.NewGuid() + ".docx")));

        Assert.Contains("не найден", exception.UserMessage);
    }

    [TestMethod]
    public async Task Unsupported_extension_is_refused_rather_than_guessed()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = Path.Combine(workspace.Root, "thing.rtf");
        await File.WriteAllTextAsync(path, "content");

        await Assert.ThrowsExactlyAsync<DocumentFormatException>(
            () => new DocumentSerializer().LoadAsync(path));
    }

    /// <summary>
    /// A save that fails must not leave the previous file destroyed.
    /// </summary>
    [TestMethod]
    public async Task Cancelled_export_leaves_no_partial_file()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Docx, "cancelled");

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => new DocumentSerializer().SaveAsync(
                DocumentFixtures.BuildRichSample(),
                path,
                DocumentFormat.Docx,
                progress: null,
                cancellationToken: cancellation.Token));

        Assert.IsFalse(File.Exists(path));
        Assert.IsFalse(File.Exists(path + ".writelite-tmp"));
    }

    [TestMethod]
    public async Task Export_reports_progress_that_reaches_completion()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Docx, "progress");

        var reports = new List<DocumentProgress>();
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new Progress<DocumentProgress>(report =>
        {
            reports.Add(report);
            if (report.Fraction is 1) completed.TrySetResult();
        });

        await new DocumentSerializer().SaveAsync(
            DocumentFixtures.BuildRichSample(),
            path,
            DocumentFormat.Docx,
            progress);

        // Progress<T> posts to the captured synchronisation context. Waiting for the
        // semantic completion signal avoids a timing-dependent sleep under parallel load.
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsNotEmpty(reports);
        Assert.IsTrue(reports.Any(report => report.Fraction is 1), "Progress never reported completion.");
    }
}
