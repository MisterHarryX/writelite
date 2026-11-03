using System.IO;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WriteLite.Documents.Model;
using WriteLite.Services.Documents;

namespace WriteLite.Tests.Documents;

/// <summary>
/// Pictures survive the editor: a picture in the FlowDocument becomes an image resource in the
/// model (which the DOCX/ODT/PDF exporters write) and comes back as a picture, not as «[описание]».
/// </summary>
[TestClass]
public sealed class EditorImageRoundTripTests
{
    private static byte[] TinyPng()
    {
        var bitmap = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 3 * 4], 4 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    [TestMethod]
    public void A_picture_round_trips_through_the_document_model() => WpfTestHost.Run(() =>
    {
        var typography = new EditorTypography(new FontFamily("Segoe UI"), 12, Brushes.Black, Brushes.Gray, Brushes.Blue);
        var flow = new FlowDocument();
        var paragraph = new Paragraph(new Run("До картинки "));
        paragraph.Inlines.Add(FlowDocumentBridge.CreateImageInline(new DocumentImage(TinyPng(), "image/png", 30, 20)));
        flow.Blocks.Add(paragraph);

        var model = FlowDocumentBridge.ToDocumentModel(flow);
        Assert.AreEqual(1, model.Resources.Images.Count, "The picture is stored as a resource.");
        var run = model.Blocks.OfType<DocumentParagraph>().Single().Inlines.OfType<ImageRun>().Single();
        Assert.AreEqual(30, run.WidthPt, 0.5);

        var back = FlowDocumentBridge.ToFlowDocument(model, typography);
        var container = ((Paragraph)back.Blocks.FirstBlock).Inlines.OfType<InlineUIContainer>().SingleOrDefault();
        Assert.IsNotNull(container, "The picture comes back as a picture.");
    });
}
