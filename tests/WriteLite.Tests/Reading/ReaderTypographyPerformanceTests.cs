using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Documents;
using WriteLite.Documents;
using WriteLite.Services.Reading;

namespace WriteLite.Tests.Reading;

/// <summary>
/// What changing the reader's type size costs, against what it used to cost.
/// </summary>
/// <remarks>
/// The old path for one press of «+» was: re-read the source file from disk, re-parse
/// it into a document model, and rebuild the entire <see cref="FlowDocument"/> — every
/// paragraph, every run — with the parse off the dispatcher and the rebuild on it. The
/// new path rewrites the metrics on the blocks that are already there.
///
/// Both are measured here on the same book so the comparison is a measurement rather
/// than an argument. The numbers are printed rather than asserted tightly: what is
/// asserted is the property that matters and cannot drift — that applying typography
/// costs a small fraction of a rebuild, and that it produces no garbage for the marks
/// to lose their place in.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ReaderTypographyPerformanceTests
{
    /// <summary>Paragraph count of a real book, rather than of a test fixture.</summary>
    private const int Paragraphs = 3_000;

    private string _root = string.Empty;
    private string _bookPath = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "wl-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _bookPath = Path.Combine(_root, "book.txt");

        var text = new StringBuilder();
        for (var index = 0; index < Paragraphs; index++)
        {
            if (index % 75 == 0)
            {
                text.AppendLine($"Глава {index / 75 + 1}. О предмете");
                text.AppendLine();
            }

            text.Append("Абзац ").Append(index).Append(". ");
            for (var word = 0; word < 26; word++)
            {
                text.Append("нормализация избыточность декларативный ");
            }

            text.AppendLine();
            text.AppendLine();
        }

        File.WriteAllText(_bookPath, text.ToString(), Encoding.UTF8);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory left behind is not a test failure.
        }
    }

    [TestMethod]
    public void Applying_typography_costs_a_fraction_of_rebuilding_the_book()
    {
        WpfTestHost.Run(() =>
        {
            var serializer = new DocumentSerializer();

            var importClock = Stopwatch.StartNew();
            var imported = serializer.LoadAsync(_bookPath).GetAwaiter().GetResult();
            importClock.Stop();

            var buildClock = Stopwatch.StartNew();
            var document = ReadingDocument.Build(imported.Document, ReaderTypography.Default);
            buildClock.Stop();

            Assert.IsTrue(document.BlockCount > 2_000, $"Only {document.BlockCount} blocks — check the fixture.");

            // The old cost of one press of «+»: import plus build, every time.
            var rebuild = importClock.Elapsed + buildClock.Elapsed;

            // The new cost of one press of «+».
            var sizes = new[] { 18d, 24, 14, 30, 16, 28, 20, 25 };
            var applyClock = Stopwatch.StartNew();

            foreach (var size in sizes)
            {
                document.ApplyTypography(new ReaderTypography { FontSize = size, LineSpacing = 1.7 });
            }

            applyClock.Stop();
            var apply = applyClock.Elapsed / sizes.Length;

            Console.WriteLine(
                $"blocks={document.BlockCount} chars={document.Length}\n" +
                $"  import          {importClock.Elapsed.TotalMilliseconds,8:0.0} ms\n" +
                $"  build           {buildClock.Elapsed.TotalMilliseconds,8:0.0} ms\n" +
                $"  rebuild (old)   {rebuild.TotalMilliseconds,8:0.0} ms per type change\n" +
                $"  apply   (new)   {apply.TotalMilliseconds,8:0.0} ms per type change\n" +
                $"  ratio           {rebuild.TotalMilliseconds / Math.Max(apply.TotalMilliseconds, 0.001),8:0.0}×");

            // Deliberately not a tuned ratio. What this guards is the shape of the work:
            // a rebuild walks every run and re-parses the file, an apply walks the
            // blocks. If someone reintroduces the rebuild, the two costs converge, and
            // a quarter is far enough apart to say so on any machine while being far
            // enough from the measured margin not to fail on a slow one.
            Assert.IsTrue(
                apply.TotalMilliseconds * 4 < rebuild.TotalMilliseconds,
                $"Applying typography took {apply.TotalMilliseconds:0.0} ms against a rebuild's " +
                $"{rebuild.TotalMilliseconds:0.0} ms. Changing a reading preference has started " +
                "doing work proportional to the book again.");
        });
    }

    [TestMethod]
    public void Applying_typography_keeps_every_run_and_therefore_every_anchor()
    {
        WpfTestHost.Run(() =>
        {
            var imported = new DocumentSerializer().LoadAsync(_bookPath).GetAwaiter().GetResult();
            var document = ReadingDocument.Build(imported.Document, ReaderTypography.Default);

            var probe = document.Length / 2;
            var before = document.PointerAt(probe);
            var runsBefore = CountRuns(document);
            var textBefore = document.Text;

            document.ApplyTypography(new ReaderTypography { FontSize = 29, LineSpacing = 2.4 });

            Assert.AreEqual(runsBefore, CountRuns(document), "No run may be created or destroyed.");
            Assert.AreSame(textBefore, document.Text, "The flattened text is the book, and the book did not change.");
            Assert.AreEqual(probe, document.OffsetOf(before), "Every stored offset still resolves to the same place.");
            Assert.AreEqual(29, document.Flow.FontSize, 0.001);
            Assert.AreEqual(29 * 2.4, document.Flow.LineHeight, 0.001);
        });
    }

    private static int CountRuns(ReadingDocument document)
    {
        var count = 0;

        foreach (var block in document.Flow.Blocks)
        {
            count += block switch
            {
                Paragraph paragraph => paragraph.Inlines.OfType<Run>().Count(),
                Table table => table.RowGroups
                    .SelectMany(group => group.Rows)
                    .SelectMany(row => row.Cells)
                    .SelectMany(cell => cell.Blocks.OfType<Paragraph>())
                    .SelectMany(paragraph => paragraph.Inlines.OfType<Run>())
                    .Count(),
                _ => 0
            };
        }

        return count;
    }
}
