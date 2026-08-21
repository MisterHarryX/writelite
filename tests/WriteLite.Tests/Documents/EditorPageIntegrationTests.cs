using System.Windows;
using System.Windows.Documents;
using WriteLite.Documents;
using WriteLite.Documents.Model;
using WriteLite.Services.Documents;
using WriteLite.Views.Pages;
using WpfParagraph = System.Windows.Documents.Paragraph;

namespace WriteLite.Tests.Documents;

/// <summary>
/// The editor page itself, driven the way a person drives it.
/// </summary>
/// <remarks>
/// These go through the real <see cref="EditorPage"/> in a real visual tree rather
/// than through the services underneath it, because the interesting failures live
/// in the seams: a document that imports correctly but renders empty, formatting
/// that applies to the selection but never reaches the saved file, a text index
/// that is right until the first edit.
///
/// Not parallelised — one shared STA dispatcher, like every other UI test here.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class EditorPageIntegrationTests
{
    /// <summary>
    /// Builds a page that cannot reach the real application data directory.
    /// </summary>
    /// <remarks>
    /// A page left over from a failing test used to autosave into
    /// <c>%LOCALAPPDATA%\WriteLite\recovery</c>, and the next page to load found the
    /// snapshot and asked about it — hanging the shared dispatcher, and worse,
    /// greeting the user's own next launch with a test document.
    /// </remarks>
    private static EditorPage NewPage(DocumentFixtures.Workspace workspace)
    {
        var page = new EditorPage
        {
            Recovery = new DocumentRecoveryService(Path.Combine(workspace.Root, "recovery"))
        };

        // Never block the dispatcher on a dialog nobody can answer.
        page.ReportError = (_, _) => { };
        page.AskConfirmation = (_, _) => true;
        return page;
    }

    /// <summary>
    /// Hosts the page off-screen so templates expand and TextPointer geometry works.
    /// </summary>
    /// <remarks>
    /// Parked far off-screen for the same reason the render smoke tests do it: a
    /// window under the real mouse pointer starts hover storyboards that outlive
    /// the test.
    /// </remarks>
    private static Window Host(EditorPage page)
    {
        WpfTestHost.EnsureThemeApplied();

        var window = new Window
        {
            Width = 1280,
            Height = 800,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
            Content = page
        };

        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static string PlainText(EditorPage page) =>
        DocumentTextIndex.Build(page.Editor.Document).Text;

    // ── Opening ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Opening_a_docx_puts_its_text_in_the_editor() => WpfTestHost.RunAsync(async () =>
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Docx);
        await new DocumentSerializer().SaveAsync(
            DocumentFixtures.BuildRichSample(),
            path,
            DocumentFormat.Docx);

        var page = NewPage(workspace);
        var window = Host(page);

        try
        {
            await page.OpenFromShellAsync(path);
            await WpfTestHost.PumpAsync();

            var text = PlainText(page);
            Assert.Contains("Заголовок первого уровня", text);
            Assert.Contains(DocumentFixtures.CyrillicSentence, text);
            Assert.AreEqual(path, page.CurrentDocumentPath);
        }
        finally
        {
            window.Close();
            page.Dispose();
        }
    });

    [TestMethod]
    public void Opening_a_document_of_each_format_produces_editable_text() => WpfTestHost.RunAsync(async () =>
    {
        using var workspace = new DocumentFixtures.Workspace();
        var serializer = new DocumentSerializer();

        foreach (var format in new[]
                 {
                     DocumentFormat.Docx,
                     DocumentFormat.Odt,
                     DocumentFormat.Txt,
                     DocumentFormat.Pdf
                 })
        {
            var path = workspace.PathFor(format);
            await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), path, format);

            var page = NewPage(workspace);
            var window = Host(page);

            try
            {
                await page.OpenFromShellAsync(path);
                await WpfTestHost.PumpAsync(150);

                var text = PlainText(page);
                Assert.Contains("Ёжик", text, $"{format} did not reach the editor.");

                // Editable means the caret can be placed and text typed.
                Assert.IsFalse(page.Editor.IsReadOnly, $"{format} opened read-only.");
            }
            finally
            {
                window.Close();
                page.Dispose();
            }
        }
    });

    [TestMethod]
    public void Opening_a_docx_preserves_headings_and_lists_on_the_surface() => WpfTestHost.RunAsync(async () =>
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Docx);
        await new DocumentSerializer().SaveAsync(
            DocumentFixtures.BuildRichSample(),
            path,
            DocumentFormat.Docx);

        var page = NewPage(workspace);
        var window = Host(page);

        try
        {
            await page.OpenFromShellAsync(path);
            await WpfTestHost.PumpAsync();

            Assert.IsTrue(
                page.Editor.Document.Blocks.OfType<System.Windows.Documents.List>().Any(),
                "Lists did not survive into the editing surface.");

            var model = FlowDocumentBridge.ToDocumentModel(page.Editor.Document);
            Assert.IsTrue(model.Blocks.OfType<DocumentParagraph>().Any(paragraph => paragraph.IsHeading));
        }
        finally
        {
            window.Close();
            page.Dispose();
        }
    });

    // ── Editing and saving ───────────────────────────────────────────────────

    /// <summary>
    /// The full loop: open, edit, save, reopen — the thing a writer actually does.
    /// </summary>
    [TestMethod]
    public void Text_typed_into_the_editor_survives_a_save_and_reopen() => WpfTestHost.RunAsync(async () =>
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Docx);
        var serializer = new DocumentSerializer();

        await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), path, DocumentFormat.Docx);

        var page = NewPage(workspace);
        var window = Host(page);

        try
        {
            await page.OpenFromShellAsync(path);
            await WpfTestHost.PumpAsync();

            // Type at the very end, the way a person continues a document.
            page.Editor.CaretPosition = page.Editor.Document.ContentEnd;
            page.Editor.CaretPosition.InsertTextInRun("Добавленное предложение.");

            var edited = FlowDocumentBridge.ToDocumentModel(page.Editor.Document);
            var saved = Path.Combine(workspace.Root, "edited.docx");
            await serializer.SaveAsync(edited, saved, DocumentFormat.Docx);

            var reloaded = (await serializer.LoadAsync(saved)).Document.ToPlainText();

            Assert.Contains("Добавленное предложение.", reloaded);
            Assert.Contains(DocumentFixtures.CyrillicSentence, reloaded);
        }
        finally
        {
            window.Close();
            page.Dispose();
        }
    });

    /// <summary>
    /// Formatting applied in the editor has to reach the file, not just the screen.
    /// </summary>
    [TestMethod]
    public void Formatting_applied_to_a_selection_reaches_the_saved_document() => WpfTestHost.RunAsync(async () =>
    {
        using var workspace = new DocumentFixtures.Workspace();
        var serializer = new DocumentSerializer();

        var page = NewPage(workspace);
        var window = Host(page);

        try
        {
            var paragraph = new WpfParagraph(new Run("Обычное предложение для проверки."));
            page.Editor.Document = new FlowDocument(paragraph);
            await WpfTestHost.PumpAsync(80);

            page.Editor.Selection.Select(paragraph.ContentStart, paragraph.ContentEnd);
            page.Editor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Bold);
            page.Editor.Selection.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Italic);

            var model = FlowDocumentBridge.ToDocumentModel(page.Editor.Document);
            var saved = Path.Combine(workspace.Root, "formatted.docx");
            await serializer.SaveAsync(model, saved, DocumentFormat.Docx);

            var runs = (await serializer.LoadAsync(saved)).Document
                .Blocks.OfType<DocumentParagraph>()
                .SelectMany(item => item.Inlines)
                .OfType<TextRun>()
                .ToArray();

            Assert.IsTrue(runs.Any(run => run.Format.Bold), "Bold did not reach the file.");
            Assert.IsTrue(runs.Any(run => run.Format.Italic), "Italic did not reach the file.");
        }
        finally
        {
            window.Close();
            page.Dispose();
        }
    });

    // ── Analysis integration ─────────────────────────────────────────────────

    /// <summary>
    /// Imported text must be analysable by the existing spelling and grammar stack.
    /// </summary>
    /// <remarks>
    /// This is the seam the rich-text move put at risk: the language layer speaks in
    /// flat offsets, and the editor no longer holds a flat string. If the projection
    /// were wrong, imported documents would silently stop being checked.
    /// </remarks>
    [TestMethod]
    public void Imported_text_is_visible_to_the_analysis_projection() => WpfTestHost.RunAsync(async () =>
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Odt);
        await new DocumentSerializer().SaveAsync(
            DocumentFixtures.BuildRichSample(),
            path,
            DocumentFormat.Odt);

        var page = NewPage(workspace);
        var window = Host(page);

        try
        {
            await page.OpenFromShellAsync(path);
            await WpfTestHost.PumpAsync();

            var index = DocumentTextIndex.Build(page.Editor.Document);

            // The projection has to see every kind of container, or whole regions of
            // an imported document would go unchecked.
            Assert.Contains("Заголовок первого уровня", index.Text);
            Assert.Contains("Нумерованный пункт 1", index.Text);
            Assert.Contains("Формат", index.Text);

            // And an offset from that projection has to resolve back to the same words.
            var offset = index.Text.IndexOf("Ёжик", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, offset);

            var range = index.RangeFor(offset, "Ёжик".Length);
            Assert.IsNotNull(range);
            Assert.AreEqual("Ёжик", range!.Text);
        }
        finally
        {
            window.Close();
            page.Dispose();
        }
    });

    // ── New document ─────────────────────────────────────────────────────────

    [TestMethod]
    public void A_new_page_starts_with_an_empty_editable_document() => WpfTestHost.RunAsync(async () =>
    {
        using var workspace = new DocumentFixtures.Workspace();

        var page = NewPage(workspace);
        var window = Host(page);

        try
        {
            await WpfTestHost.PumpAsync(80);

            Assert.IsNull(page.CurrentDocumentPath);
            Assert.IsEmpty(PlainText(page).Trim());
            Assert.IsGreaterThanOrEqualTo(1, page.Editor.Document.Blocks.Count);
        }
        finally
        {
            window.Close();
            page.Dispose();
        }
    });

    // ── Failure paths ────────────────────────────────────────────────────────

    /// <summary>
    /// A damaged file must be reported and must not cost the writer their work.
    /// </summary>
    [TestMethod]
    public void Opening_an_unreadable_file_reports_it_and_leaves_the_editor_usable() => WpfTestHost.RunAsync(async () =>
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Docx, "broken");
        await File.WriteAllBytesAsync(path, [0x50, 0x4B, 0x03, 0x04, 0x00, 0x01]);

        var page = NewPage(workspace);
        var errors = new List<(string Title, string Message)>();
        page.ReportError = (title, message) => errors.Add((title, message));

        var window = Host(page);

        try
        {
            page.Editor.Document = new FlowDocument(new WpfParagraph(new Run("Существующий текст.")));
            await WpfTestHost.PumpAsync(80);

            await page.OpenFromShellAsync(path);
            await WpfTestHost.PumpAsync(200);

            Assert.IsNotEmpty(errors, "A damaged file must be reported to the user.");
            Assert.Contains("DOCX", errors[0].Message);

            // The message is for a person: no exception types, no stack traces.
            Assert.DoesNotContain("Exception", errors[0].Message);

            // And the document they were working on is still there.
            Assert.Contains("Существующий текст.", PlainText(page));
        }
        finally
        {
            window.Close();
            page.Dispose();
        }
    });

    /// <summary>
    /// Declining the unsaved-changes prompt must abandon the open, not the work.
    /// </summary>
    [TestMethod]
    public void Declining_the_unsaved_changes_prompt_keeps_the_current_document() => WpfTestHost.RunAsync(async () =>
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Docx);
        await new DocumentSerializer().SaveAsync(
            DocumentFixtures.BuildRichSample(),
            path,
            DocumentFormat.Docx);

        var page = NewPage(workspace);
        page.AskConfirmation = (_, _) => false;

        var window = Host(page);

        try
        {
            page.Editor.Document = new FlowDocument(
                new WpfParagraph(new Run("Черновик, который нельзя потерять.")));
            await WpfTestHost.PumpAsync(80);

            await page.OpenFromShellAsync(path);
            await WpfTestHost.PumpAsync(150);

            Assert.Contains("Черновик, который нельзя потерять.", PlainText(page));
            Assert.IsNull(page.CurrentDocumentPath);
        }
        finally
        {
            window.Close();
            page.Dispose();
        }
    });
}
