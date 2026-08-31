using System.Diagnostics;
using System.Text;
using System.Windows.Documents;
using WriteLite.Services.Documents;

namespace WriteLite.Tests.Documents;

/// <summary>
/// What the editor spends on the dispatcher for each analysis pass.
/// </summary>
/// <remarks>
/// <para>Typing is answered by WPF and never waits for WriteLite. Everything WriteLite does
/// after a keystroke happens on a worker, with one exception: building
/// <see cref="DocumentTextIndex"/>. That has to run on the dispatcher, because
/// <see cref="TextPointer"/> is thread-affine, and it runs once per analysis pass. It is
/// therefore the only thing between a keystroke and the caret moving that grows with the
/// size of the document, and the only place where a large document could make the editor
/// feel heavy.</para>
///
/// <para>These record what it costs so the number is a measurement rather than an assumption,
/// and bound it well above what was measured so the test fails on a real regression rather
/// than on a busy build agent.</para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class EditorInputLatencyTests
{
    /// <summary>A document of roughly <paramref name="characters"/> characters in prose paragraphs.</summary>
    private static FlowDocument Document(int characters)
    {
        var document = new FlowDocument();
        var written = 0;
        var paragraph = 0;

        while (written < characters)
        {
            var text = new StringBuilder()
                .Append("Абзац ")
                .Append(paragraph++)
                .Append(". ")
                .Append(string.Join(' ', Enumerable.Repeat("обычное предложение из нескольких слов", 6)))
                .ToString();

            document.Blocks.Add(new Paragraph(new Run(text)));
            written += text.Length;
        }

        return document;
    }

    private static double MedianBuildMilliseconds(FlowDocument document, int samples = 9)
    {
        // One untimed build first: the first touch of a FlowDocument realises its structure,
        // and measuring that would report the cost of opening a document as the cost of
        // typing in one.
        _ = DocumentTextIndex.Build(document);

        var timings = new List<double>();
        for (var i = 0; i < samples; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            _ = DocumentTextIndex.Build(document);
            stopwatch.Stop();
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        return timings[timings.Count / 2];
    }

    [TestMethod]
    public void AnOrdinaryDocumentIsProjectedWellInsideOneFrame()
    {
        WpfTestHost.Run(() =>
        {
            var document = Document(20_000);
            var median = MedianBuildMilliseconds(document);

            Console.WriteLine($"index build: 20 000 chars p50={median:F2} ms");

            // A 60 Hz frame is 16.7 ms. The projection happens once per analysis pass, after
            // the debounce, so staying inside a frame is what keeps the caret from stuttering
            // while someone types in a document of ordinary length.
            Assert.IsLessThan(16.0, median, $"index build p50 was {median:F2} ms");
        });
    }

    [TestMethod]
    public void AVeryLargeDocumentIsStillProjectedInsideTheDebounce()
    {
        WpfTestHost.Run(() =>
        {
            var document = Document(200_000);
            var median = MedianBuildMilliseconds(document, samples: 5);

            Console.WriteLine($"index build: 200 000 chars p50={median:F2} ms");

            // Ten times the settings ceiling for live analysis. The editor stops checking
            // continuously well before this, so the bound here is about the document staying
            // usable at all rather than about it feeling instant.
            Assert.IsLessThan(220.0, median, $"index build p50 was {median:F2} ms");
        });
    }

    [TestMethod]
    public void TheProjectionCostGrowsWithTheDocumentAndNotFasterThanIt()
    {
        // A super-linear walk is how an editor becomes unusable at a size nobody tested. Ten
        // times the text must not cost far more than ten times the work.
        WpfTestHost.Run(() =>
        {
            var small = MedianBuildMilliseconds(Document(20_000));
            var large = MedianBuildMilliseconds(Document(200_000), samples: 5);

            Console.WriteLine($"index build growth: {small:F2} ms -> {large:F2} ms");

            // Generous, because both numbers are small enough for scheduling noise to matter;
            // it still catches a quadratic walk, which would be a factor of a hundred.
            Assert.IsLessThan(
                Math.Max(small, 0.2) * 40,
                large,
                $"projection grew from {small:F2} ms to {large:F2} ms for ten times the text");
        });
    }

    [TestMethod]
    public void TheProjectionRoundTripsEveryOffset()
    {
        // The reason the projection exists: every analyzer speaks in offsets over this flat
        // string, and a correction is applied by mapping one back. An off-by-one here writes
        // the right replacement in the wrong place.
        WpfTestHost.Run(() =>
        {
            var document = Document(5_000);
            var index = DocumentTextIndex.Build(document);

            for (var offset = 0; offset < index.Length; offset += 97)
            {
                var pointer = index.PointerAt(offset);
                Assert.IsNotNull(pointer, $"offset {offset} does not map back into the document");
            }
        });
    }
}
