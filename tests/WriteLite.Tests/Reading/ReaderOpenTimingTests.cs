using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WriteLite.Documents;
using WriteLite.Services.Reading;

namespace WriteLite.Tests.Reading;

/// <summary>
/// How long the reader takes to put a large book on screen. Measurement only: runs when
/// WRITELITE_READER_BOOK points at a book file, and prints the stages.
/// </summary>
[TestClass]
public sealed class ReaderOpenTimingTests
{
    [TestMethod]
    public void Large_book_open_timing()
    {
        var path = Environment.GetEnvironmentVariable("WRITELITE_READER_BOOK");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) Assert.Inconclusive("WRITELITE_READER_BOOK not set");

        var sw = Stopwatch.StartNew();
        var imported = new DocumentSerializer().LoadAsync(path).GetAwaiter().GetResult();
        var load = sw.ElapsedMilliseconds;

        WpfTestHost.Run(() =>
        {
            WpfTestHost.EnsureThemeApplied();
            sw.Restart();
            var reading = ReadingDocument.Build(imported.Document, new ReaderTypography());
            var build = sw.ElapsedMilliseconds;

            var box = new RichTextBox { Width = 720 };
            var scroll = new ScrollViewer { Content = box };
            var window = new Window { Content = scroll, Width = 900, Height = 800, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            sw.Restart();
            box.Document = reading.Flow;
            window.UpdateLayout();
            var layout = sw.ElapsedMilliseconds;
            window.Close();
            Console.WriteLine($"chars={reading.Length} load={load}ms build={build}ms layout={layout}ms");
        });
    }
}
