using WriteLite.Documents;
using WriteLite.Services.Documents;

namespace WriteLite.Tests.Documents;

/// <summary>
/// Workspace rules: what may be saved where, recents, and crash recovery.
/// </summary>
[TestClass]
public sealed class DocumentWorkspaceTests
{
    // ── Save targets ─────────────────────────────────────────────────────────

    /// <summary>
    /// Editing a PDF must never write back over the original.
    /// </summary>
    /// <remarks>
    /// A PDF is reconstructed from glyph positions, so saving in place would
    /// replace the user's file with WriteLite's interpretation of it — an
    /// unrecoverable downgrade. Save has to become Save As.
    /// </remarks>
    [TestMethod]
    public async Task A_document_opened_from_pdf_cannot_be_saved_in_place()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = workspace.PathFor(DocumentFormat.Pdf);

        var serializer = new DocumentSerializer();
        await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), path, DocumentFormat.Pdf);

        var service = new DocumentWorkspaceService(serializer, new RecentDocumentsStore(
            Path.Combine(workspace.Root, "recent.json")));

        await service.OpenAsync(path);

        Assert.AreEqual(DocumentFormat.Pdf, service.CurrentFormat);
        Assert.IsFalse(service.CanSaveInPlace);
    }

    [TestMethod]
    public async Task Editable_formats_can_be_saved_in_place()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var serializer = new DocumentSerializer();
        var service = new DocumentWorkspaceService(serializer, new RecentDocumentsStore(
            Path.Combine(workspace.Root, "recent.json")));

        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt, DocumentFormat.Txt })
        {
            var path = workspace.PathFor(format);
            await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), path, format);
            await service.OpenAsync(path);

            Assert.IsTrue(service.CanSaveInPlace, $"{format} should be editable in place.");
        }
    }

    /// <summary>Exporting a copy must not change which file is being edited.</summary>
    [TestMethod]
    public async Task Exporting_does_not_rebind_the_open_document()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var serializer = new DocumentSerializer();
        var service = new DocumentWorkspaceService(serializer, new RecentDocumentsStore(
            Path.Combine(workspace.Root, "recent.json")));

        var source = workspace.PathFor(DocumentFormat.Docx);
        await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), source, DocumentFormat.Docx);
        var opened = await service.OpenAsync(source);

        await service.ExportAsync(
            opened.Document,
            Path.Combine(workspace.Root, "copy.pdf"),
            DocumentFormat.Pdf);

        Assert.AreEqual(source, service.CurrentPath);
        Assert.AreEqual(DocumentFormat.Docx, service.CurrentFormat);
    }

    /// <summary>Saving as PDF is an export, so it must not make the PDF the edited file either.</summary>
    [TestMethod]
    public async Task Saving_as_pdf_leaves_the_editable_document_bound()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var serializer = new DocumentSerializer();
        var service = new DocumentWorkspaceService(serializer, new RecentDocumentsStore(
            Path.Combine(workspace.Root, "recent.json")));

        var source = workspace.PathFor(DocumentFormat.Docx);
        await serializer.SaveAsync(DocumentFixtures.BuildRichSample(), source, DocumentFormat.Docx);
        var opened = await service.OpenAsync(source);

        await service.SaveAsync(
            opened.Document,
            Path.Combine(workspace.Root, "as.pdf"),
            DocumentFormat.Pdf);

        Assert.AreEqual(source, service.CurrentPath);
    }

    [TestMethod]
    public void Suggested_file_names_are_valid_on_windows()
    {
        var service = new DocumentWorkspaceService();
        service.Reset("Отчёт: за 2026/квартал 1");

        var name = service.SuggestFileName(DocumentFormat.Docx);

        Assert.EndsWith(".docx", name);
        Assert.IsFalse(name.Intersect(Path.GetInvalidFileNameChars()).Any());
    }

    // ── Recents ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Recent_documents_are_most_recent_first_and_deduplicated()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var store = new RecentDocumentsStore(Path.Combine(workspace.Root, "recent.json"));

        store.Add(@"C:\docs\first.docx");
        store.Add(@"C:\docs\second.docx");
        store.Add(@"C:\docs\first.docx");

        var entries = store.Load();

        Assert.HasCount(2, entries);
        Assert.AreEqual(@"C:\docs\first.docx", entries[0].Path);
    }

    [TestMethod]
    public void Recent_documents_survive_a_reload()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = Path.Combine(workspace.Root, "recent.json");

        new RecentDocumentsStore(path).Add(@"C:\docs\kept.docx");

        var reloaded = new RecentDocumentsStore(path).Load();

        Assert.HasCount(1, reloaded);
        Assert.AreEqual("kept.docx", reloaded[0].Name);
    }

    [TestMethod]
    public void Clearing_recent_documents_empties_the_list()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var store = new RecentDocumentsStore(Path.Combine(workspace.Root, "recent.json"));

        store.Add(@"C:\docs\a.docx");
        store.Clear();

        Assert.IsEmpty(store.Load());
    }

    [TestMethod]
    public void A_corrupt_recent_list_degrades_to_empty_rather_than_throwing()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var path = Path.Combine(workspace.Root, "recent.json");
        File.WriteAllText(path, "{ this is not valid json");

        Assert.IsEmpty(new RecentDocumentsStore(path).Load());
    }

    // ── Recovery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Autosave must write to WriteLite's own directory, never to the user's file.
    /// </summary>
    [TestMethod]
    public async Task Autosave_writes_a_snapshot_without_touching_the_original()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var original = workspace.PathFor(DocumentFormat.Docx);
        await new DocumentSerializer().SaveAsync(
            DocumentFixtures.BuildRichSample(),
            original,
            DocumentFormat.Docx);

        var before = await File.ReadAllBytesAsync(original);

        var recoveryDirectory = Path.Combine(workspace.Root, "recovery");
        await using var recovery = new DocumentRecoveryService(recoveryDirectory);

        var edited = DocumentFixtures.BuildRichSample();
        edited.CurrentSection().Blocks.Add(
            WriteLite.Documents.Model.DocumentParagraph.FromText("Несохранённое дополнение"));

        await recovery.SaveSnapshotAsync(edited, original);

        Assert.IsTrue(Directory.EnumerateFiles(recoveryDirectory, "*.docx").Any());
        Assert.AreEqual(
            Convert.ToHexString(before),
            Convert.ToHexString(await File.ReadAllBytesAsync(original)),
            "Autosave must never rewrite the user's own file.");
    }

    [TestMethod]
    public async Task A_snapshot_from_another_session_is_offered_back()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var recoveryDirectory = Path.Combine(workspace.Root, "recovery");

        await using (var previousSession = new DocumentRecoveryService(recoveryDirectory))
        {
            await previousSession.SaveSnapshotAsync(
                DocumentFixtures.BuildRichSample(),
                @"C:\docs\was-open.docx");
        }

        await using var currentSession = new DocumentRecoveryService(recoveryDirectory);
        var orphans = currentSession.FindOrphaned();

        Assert.HasCount(1, orphans);
        Assert.AreEqual("was-open.docx", orphans[0].DisplayName);
        Assert.IsTrue(File.Exists(orphans[0].SnapshotPath));
    }

    [TestMethod]
    public async Task A_recovered_snapshot_reopens_with_its_content_intact()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var recoveryDirectory = Path.Combine(workspace.Root, "recovery");

        await using (var previousSession = new DocumentRecoveryService(recoveryDirectory))
        {
            await previousSession.SaveSnapshotAsync(DocumentFixtures.BuildRichSample(), null);
        }

        await using var currentSession = new DocumentRecoveryService(recoveryDirectory);
        var snapshot = currentSession.FindOrphaned().Single();

        var recovered = await new DocumentSerializer().LoadAsync(snapshot.SnapshotPath);

        Assert.Contains(DocumentFixtures.CyrillicSentence, recovered.Document.ToPlainText());
    }

    /// <summary>
    /// Declining recovery must delete the copy rather than leave the user's
    /// document sitting in the application directory.
    /// </summary>
    [TestMethod]
    public async Task Discarding_a_snapshot_removes_it_from_disk()
    {
        using var workspace = new DocumentFixtures.Workspace();
        var recoveryDirectory = Path.Combine(workspace.Root, "recovery");

        await using (var previousSession = new DocumentRecoveryService(recoveryDirectory))
        {
            await previousSession.SaveSnapshotAsync(DocumentFixtures.BuildRichSample(), null);
        }

        await using var currentSession = new DocumentRecoveryService(recoveryDirectory);
        var snapshot = currentSession.FindOrphaned().Single();

        currentSession.DiscardSnapshot(snapshot);

        Assert.IsFalse(File.Exists(snapshot.SnapshotPath));
        Assert.IsEmpty(currentSession.FindOrphaned());
    }

    [TestMethod]
    public async Task A_session_does_not_offer_back_its_own_snapshot()
    {
        using var workspace = new DocumentFixtures.Workspace();
        await using var recovery = new DocumentRecoveryService(Path.Combine(workspace.Root, "recovery"));

        await recovery.SaveSnapshotAsync(DocumentFixtures.BuildRichSample(), null);

        Assert.IsEmpty(recovery.FindOrphaned());
    }

    // ── Format helpers ───────────────────────────────────────────────────────

    [TestMethod]
    public void Format_detection_covers_every_supported_extension()
    {
        Assert.AreEqual(DocumentFormat.Docx, DocumentFormats.FromPath(@"C:\a\b.docx"));
        Assert.AreEqual(DocumentFormat.Odt, DocumentFormats.FromPath(@"C:\a\b.ODT"));
        Assert.AreEqual(DocumentFormat.Pdf, DocumentFormats.FromPath("b.pdf"));
        Assert.AreEqual(DocumentFormat.Txt, DocumentFormats.FromPath("b.txt"));
        Assert.AreEqual(DocumentFormat.Unknown, DocumentFormats.FromPath("b.rtf"));
        Assert.AreEqual(DocumentFormat.Unknown, DocumentFormats.FromPath("b"));
    }

    [TestMethod]
    public void Every_importable_format_has_an_extension_and_a_display_name()
    {
        foreach (var format in DocumentFormats.Importable)
        {
            Assert.IsNotEmpty(DocumentFormats.Extension(format));
            Assert.AreNotEqual("—", DocumentFormats.DisplayName(format));
        }
    }
}
