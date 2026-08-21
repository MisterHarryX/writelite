using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using WriteLite.Documents.Model;
using WriteLite.Services.Documents;
using WpfParagraph = System.Windows.Documents.Paragraph;
using WpfList = System.Windows.Documents.List;
using WpfTable = System.Windows.Documents.Table;

namespace WriteLite.Tests.Documents;

/// <summary>
/// The bridge between the document model and the editing surface.
/// </summary>
/// <remarks>
/// Not parallelised: FlowDocument and TextPointer are thread-affine, so every case
/// runs on the shared STA dispatcher that the rest of the UI tests use.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class EditorDocumentBridgeTests
{
    private static EditorTypography Typography() => new(
        new FontFamily("Segoe UI"),
        12,
        Brushes.White,
        Brushes.Gray,
        Brushes.Orange);

    // ── Model → FlowDocument → model ─────────────────────────────────────────

    [TestMethod]
    public void Round_trip_through_the_editing_surface_preserves_text() => WpfTestHost.Run(() =>
    {
        var source = DocumentFixtures.BuildRichSample();

        var flow = FlowDocumentBridge.ToFlowDocument(source, Typography());
        var result = FlowDocumentBridge.ToDocumentModel(flow);

        Assert.Contains(DocumentFixtures.CyrillicSentence, result.ToPlainText());
        Assert.Contains("Заголовок первого уровня", result.ToPlainText());
    });

    [TestMethod]
    public void Round_trip_preserves_headings_lists_tables_and_page_breaks() => WpfTestHost.Run(() =>
    {
        var flow = FlowDocumentBridge.ToFlowDocument(DocumentFixtures.BuildRichSample(), Typography());
        var result = FlowDocumentBridge.ToDocumentModel(flow);

        var paragraphs = result.Blocks.OfType<DocumentParagraph>().ToArray();

        Assert.AreEqual(1, paragraphs.Count(paragraph => paragraph.OutlineLevel == 1));
        Assert.AreEqual(1, paragraphs.Count(paragraph => paragraph.OutlineLevel == 2));
        Assert.AreEqual(3, paragraphs.Count(paragraph => paragraph.List?.Kind == ListKind.Numbered));
        Assert.AreEqual(2, paragraphs.Count(paragraph => paragraph.List?.Kind == ListKind.Bullet));
        Assert.AreEqual(1, result.Blocks.OfType<DocumentTable>().Count());
        Assert.AreEqual(1, result.Blocks.OfType<PageBreak>().Count());
    });

    [TestMethod]
    public void Round_trip_preserves_inline_formatting() => WpfTestHost.Run(() =>
    {
        var flow = FlowDocumentBridge.ToFlowDocument(DocumentFixtures.BuildRichSample(), Typography());
        var result = FlowDocumentBridge.ToDocumentModel(flow);

        var runs = result.Blocks
            .OfType<DocumentParagraph>()
            .SelectMany(paragraph => paragraph.Inlines)
            .OfType<TextRun>()
            .ToArray();

        Assert.IsTrue(runs.Any(run => run.Format.Bold && run.Text.Contains("жирный")));
        Assert.IsTrue(runs.Any(run => run.Format.Italic && run.Text.Contains("курсив")));
        Assert.IsTrue(runs.Any(run => run.Format.Underline && run.Text.Contains("подчёркнутый")));
        Assert.IsTrue(runs.Any(run => run.Format.Strikethrough && run.Text.Contains("зачёркнутый")));
        Assert.IsTrue(runs.Any(run => run.Format.ColorHex == "#EC6C08"));
    });

    [TestMethod]
    public void Round_trip_preserves_paragraph_layout() => WpfTestHost.Run(() =>
    {
        var flow = FlowDocumentBridge.ToFlowDocument(DocumentFixtures.BuildRichSample(), Typography());
        var result = FlowDocumentBridge.ToDocumentModel(flow);

        var justified = result.Blocks
            .OfType<DocumentParagraph>()
            .Single(paragraph => paragraph.Format.Alignment == TextAlign.Justify);

        Assert.AreEqual(1.5, justified.Format.LineSpacing, 0.05);
        Assert.AreEqual(6, justified.Format.SpaceBeforePt, 0.5);
        Assert.AreEqual(12, justified.Format.SpaceAfterPt, 0.5);
        Assert.AreEqual(18, justified.Format.IndentLeftPt, 0.5);
        Assert.AreEqual(24, justified.Format.FirstLineIndentPt, 0.5);
    });

    [TestMethod]
    public void Round_trip_preserves_hyperlinks() => WpfTestHost.Run(() =>
    {
        var flow = FlowDocumentBridge.ToFlowDocument(DocumentFixtures.BuildRichSample(), Typography());
        var result = FlowDocumentBridge.ToDocumentModel(flow);

        var link = result.Blocks
            .OfType<DocumentParagraph>()
            .SelectMany(paragraph => paragraph.Inlines)
            .OfType<HyperlinkRun>()
            .Single();

        Assert.AreEqual("пример", link.Text);
        Assert.Contains("example.org", link.Target!);
    });

    [TestMethod]
    public void Lists_become_real_wpf_lists_with_the_right_marker() => WpfTestHost.Run(() =>
    {
        var flow = FlowDocumentBridge.ToFlowDocument(DocumentFixtures.BuildRichSample(), Typography());
        var lists = flow.Blocks.OfType<WpfList>().ToArray();

        Assert.AreEqual(2, lists.Length);
        Assert.IsTrue(lists.Any(list => list.MarkerStyle == TextMarkerStyle.Decimal));
        Assert.IsTrue(lists.Any(list => list.MarkerStyle == TextMarkerStyle.Disc));
    });

    [TestMethod]
    public void Tables_become_real_wpf_tables() => WpfTestHost.Run(() =>
    {
        var flow = FlowDocumentBridge.ToFlowDocument(DocumentFixtures.BuildRichSample(), Typography());
        var table = flow.Blocks.OfType<WpfTable>().Single();

        Assert.AreEqual(3, table.Columns.Count);
        Assert.AreEqual(4, table.RowGroups[0].Rows.Count);
    });

    [TestMethod]
    public void An_empty_document_still_has_a_paragraph_to_type_into() => WpfTestHost.Run(() =>
    {
        var flow = FlowDocumentBridge.ToFlowDocument(WlDocument.Empty(), Typography());

        Assert.IsGreaterThanOrEqualTo(1, flow.Blocks.Count);
    });

    // ── Text index ───────────────────────────────────────────────────────────

    /// <summary>
    /// The flat projection is what the whole language stack analyses.
    /// </summary>
    [TestMethod]
    public void Text_index_projects_paragraphs_separated_by_newlines() => WpfTestHost.Run(() =>
    {
        var flow = new FlowDocument();
        flow.Blocks.Add(new WpfParagraph(new Run("Первый абзац")));
        flow.Blocks.Add(new WpfParagraph(new Run("Второй абзац")));

        var index = DocumentTextIndex.Build(flow);

        Assert.AreEqual("Первый абзац\nВторой абзац", index.Text);
    });

    [TestMethod]
    public void Text_index_joins_runs_of_different_formatting_into_one_string() => WpfTestHost.Run(() =>
    {
        var paragraph = new WpfParagraph();
        paragraph.Inlines.Add(new Run("Обычный "));
        paragraph.Inlines.Add(new Run("жирный") { FontWeight = FontWeights.Bold });
        paragraph.Inlines.Add(new Run(" конец"));

        var flow = new FlowDocument(paragraph);
        var index = DocumentTextIndex.Build(flow);

        Assert.AreEqual("Обычный жирный конец", index.Text);
    });

    /// <summary>
    /// Round-tripping an offset through the index is what makes corrections land
    /// on the right characters.
    /// </summary>
    [TestMethod]
    public void Text_index_maps_offsets_back_to_the_exact_range() => WpfTestHost.Run(() =>
    {
        var paragraph = new WpfParagraph();
        paragraph.Inlines.Add(new Run("Обычный "));
        paragraph.Inlines.Add(new Run("жирный") { FontWeight = FontWeights.Bold });
        paragraph.Inlines.Add(new Run(" конец"));

        var index = DocumentTextIndex.Build(new FlowDocument(paragraph));

        var start = index.Text.IndexOf("жирный", StringComparison.Ordinal);
        var range = index.RangeFor(start, "жирный".Length);

        Assert.IsNotNull(range);
        Assert.AreEqual("жирный", range!.Text);
    });

    [TestMethod]
    public void Text_index_maps_offsets_across_paragraph_boundaries() => WpfTestHost.Run(() =>
    {
        var flow = new FlowDocument();
        flow.Blocks.Add(new WpfParagraph(new Run("Первый")));
        flow.Blocks.Add(new WpfParagraph(new Run("Второй")));

        var index = DocumentTextIndex.Build(flow);

        var start = index.Text.IndexOf("Второй", StringComparison.Ordinal);
        var range = index.RangeFor(start, "Второй".Length);

        Assert.IsNotNull(range);
        Assert.AreEqual("Второй", range!.Text);
    });

    [TestMethod]
    public void Text_index_reads_text_inside_lists_and_tables() => WpfTestHost.Run(() =>
    {
        var flow = FlowDocumentBridge.ToFlowDocument(DocumentFixtures.BuildRichSample(), Typography());
        var index = DocumentTextIndex.Build(flow);

        Assert.Contains("Нумерованный пункт 1", index.Text);
        Assert.Contains("Маркер один", index.Text);
        Assert.Contains("Формат", index.Text);
    });

    [TestMethod]
    public void Text_index_round_trips_a_pointer_back_to_its_offset() => WpfTestHost.Run(() =>
    {
        var flow = new FlowDocument(new WpfParagraph(new Run("Проверка позиции")));
        var index = DocumentTextIndex.Build(flow);

        var offset = index.Text.IndexOf("позиции", StringComparison.Ordinal);
        var pointer = index.PointerAt(offset);

        Assert.IsNotNull(pointer);
        Assert.AreEqual(offset, index.OffsetOf(pointer!));
    });

    /// <summary>
    /// A stale index must refuse to hand out positions.
    /// </summary>
    /// <remarks>
    /// This is the guard that stops a correction computed against the old document
    /// from being applied to the new one and replacing the wrong span.
    /// </remarks>
    [TestMethod]
    public void Text_index_knows_when_the_document_moved_on() => WpfTestHost.Run(() =>
    {
        var flow = new FlowDocument(new WpfParagraph(new Run("Текст")));
        var index = DocumentTextIndex.Build(flow, generation: 7);

        Assert.IsFalse(index.IsStale(7));
        Assert.IsTrue(index.IsStale(8));
    });

    [TestMethod]
    public void Text_index_rejects_out_of_range_requests() => WpfTestHost.Run(() =>
    {
        var index = DocumentTextIndex.Build(new FlowDocument(new WpfParagraph(new Run("Короткий"))));

        Assert.IsNull(index.RangeFor(-1, 3));
        Assert.IsNull(index.RangeFor(0, index.Length + 10));
    });

    // ── Units ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Point_and_pixel_conversion_round_trips()
    {
        Assert.AreEqual(12, FlowDocumentBridge.ToPoints(FlowDocumentBridge.ToPixels(12)), 0.001);
        Assert.AreEqual(16, FlowDocumentBridge.ToPixels(12), 0.001);
    }

    [TestMethod]
    public void Colour_parsing_rejects_malformed_values()
    {
        Assert.IsNull(FlowDocumentBridge.ParseBrush("not a colour"));
        Assert.IsNull(FlowDocumentBridge.ParseBrush("#GGGGGG"));
        Assert.IsNull(FlowDocumentBridge.ParseBrush(null));
        Assert.IsNotNull(FlowDocumentBridge.ParseBrush("#EC6C08"));
    }
}
