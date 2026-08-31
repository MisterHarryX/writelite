using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WriteLite.Services.Reading;

namespace WriteLite.Tests.Reading;

/// <summary>
/// Cover images: that they are kept, that they survive the source going away, and that
/// nothing a reader can pick makes the shelf fall over.
/// </summary>
/// <remarks>
/// The store copies and re-encodes rather than remembering a path. That is the behaviour
/// worth pinning: a cover chosen from a Downloads folder has to still be there after the
/// download is cleaned up, which is exactly when a path-referencing implementation would
/// quietly lose it and give the reader no way to tell what happened.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class BookCoverStoreTests
{
    private string _root = string.Empty;
    private string _sources = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "wl-covers-" + Guid.NewGuid().ToString("N"));
        _sources = Path.Combine(_root, "src");
        Directory.CreateDirectory(_sources);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not a test failure.
        }
    }

    private BookCoverStore Store() => new(Path.Combine(_root, "covers"));

    /// <summary>Writes a real image file of the given size and encoding.</summary>
    private string WriteImage(string name, int width, int height, bool png = true, bool transparent = false)
    {
        var path = Path.Combine(_sources, name);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x30;                              // B
            pixels[i + 1] = 0x60;                          // G
            pixels[i + 2] = 0xC0;                          // R
            pixels[i + 3] = (byte)(transparent ? 0x40 : 0xFF);
        }

        var source = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);

        BitmapEncoder encoder = png ? new PngBitmapEncoder() : new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var output = File.Create(path);
        encoder.Save(output);
        return path;
    }

    [TestMethod]
    public void APngIsAccepted()
    {
        WpfTestHost.Run(() =>
        {
            var store = Store();
            var result = store.Set("book1", WriteImage("cover.png", 400, 600), previousVersion: 0);

            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.AreEqual(1, result.Version);
            Assert.IsTrue(store.Has("book1"));
            Assert.IsNotNull(store.Load("book1"));
        });
    }

    [TestMethod]
    public void AJpegIsAccepted()
    {
        WpfTestHost.Run(() =>
        {
            var store = Store();
            var result = store.Set("book1", WriteImage("cover.jpg", 400, 600, png: false), previousVersion: 0);

            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.IsTrue(store.Has("book1"));
        });
    }

    [TestMethod]
    public void TheCoverSurvivesTheSourceFileBeingDeleted()
    {
        // The whole reason the picture is copied instead of referenced.
        WpfTestHost.Run(() =>
        {
            var store = Store();
            var source = WriteImage("cover.png", 400, 600);

            Assert.IsTrue(store.Set("book1", source, 0).Succeeded);
            File.Delete(source);

            Assert.IsTrue(store.Has("book1"));
            Assert.IsNotNull(store.Load("book1"));
        });
    }

    [TestMethod]
    public void AVeryLargeImageIsStoredScaledDown()
    {
        WpfTestHost.Run(() =>
        {
            var store = Store();
            Assert.IsTrue(store.Set("book1", WriteImage("huge.png", 4000, 3000), 0).Succeeded);

            var stored = store.Load("book1")!;

            // Bounded on the long edge, so the shelf is not decoding a wall poster per card.
            Assert.IsLessThanOrEqualTo(1200, stored.PixelWidth);
            Assert.IsLessThanOrEqualTo(1200, stored.PixelHeight);

            // …and the proportions the reader chose are still the proportions stored.
            var ratio = (double)stored.PixelWidth / stored.PixelHeight;
            Assert.IsLessThan(0.02, Math.Abs(ratio - (4000.0 / 3000.0)));
        });
    }

    [TestMethod]
    public void AnUnusualAspectRatioIsKeptAsItIs()
    {
        WpfTestHost.Run(() =>
        {
            var store = Store();
            Assert.IsTrue(store.Set("book1", WriteImage("panorama.png", 2400, 200), 0).Succeeded);

            var stored = store.Load("book1")!;
            Assert.IsGreaterThan(stored.PixelHeight * 8, stored.PixelWidth);
        });
    }

    [TestMethod]
    public void TransparencyIsPreserved()
    {
        // PNG out, always: re-encoding to JPEG would flatten a transparent cover onto black
        // and there would be no way back to what the reader picked.
        WpfTestHost.Run(() =>
        {
            var store = Store();
            Assert.IsTrue(store.Set("book1", WriteImage("alpha.png", 300, 400, transparent: true), 0).Succeeded);

            var stored = store.Load("book1")!;
            Assert.IsTrue(
                stored.Format.ToString().Contains("a", StringComparison.OrdinalIgnoreCase),
                $"expected an alpha-bearing pixel format, got {stored.Format}");
        });
    }

    [TestMethod]
    public void ACorruptFileIsRefusedWithAMessageAndNotAnException()
    {
        WpfTestHost.Run(() =>
        {
            var store = Store();
            var path = Path.Combine(_sources, "broken.png");
            File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02, 0x03]);

            var result = store.Set("book1", path, 0);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(CoverFailure.NotAnImage, result.Failure);
            Assert.IsNotEmpty(result.Message);
            Assert.IsFalse(store.Has("book1"), "a refused cover must not be half-written");
        });
    }

    [TestMethod]
    public void AFileThatIsNotAnImageAtAllIsRefused()
    {
        WpfTestHost.Run(() =>
        {
            var store = Store();
            var path = Path.Combine(_sources, "notes.png");
            File.WriteAllText(path, "это вообще не картинка");

            Assert.AreEqual(CoverFailure.NotAnImage, store.Set("book1", path, 0).Failure);
        });
    }

    [TestMethod]
    public void AMissingFileIsRefused()
    {
        WpfTestHost.Run(() =>
        {
            var store = Store();
            var result = store.Set("book1", Path.Combine(_sources, "nothing-here.png"), 0);

            Assert.AreEqual(CoverFailure.FileMissing, result.Failure);
        });
    }

    [TestMethod]
    public void AnEmptyFileIsRefused()
    {
        WpfTestHost.Run(() =>
        {
            var store = Store();
            var path = Path.Combine(_sources, "empty.png");
            File.WriteAllBytes(path, []);

            Assert.AreEqual(CoverFailure.NotAnImage, store.Set("book1", path, 0).Failure);
        });
    }

    [TestMethod]
    public void ReplacingACoverKeepsTheOldOneIfTheNewOneIsRefused()
    {
        WpfTestHost.Run(() =>
        {
            var store = Store();
            Assert.IsTrue(store.Set("book1", WriteImage("good.png", 300, 400), 0).Succeeded);

            var broken = Path.Combine(_sources, "bad.png");
            File.WriteAllText(broken, "не картинка");

            Assert.IsFalse(store.Set("book1", broken, 1).Succeeded);
            Assert.IsTrue(store.Has("book1"), "a failed replacement must not destroy the cover in place");
        });
    }

    [TestMethod]
    public void ReplacingACoverAdvancesTheVersion()
    {
        // The version is the cache key. Without it, WPF keeps drawing the first picture for
        // the rest of the session because the path has not changed.
        WpfTestHost.Run(() =>
        {
            var store = Store();
            var first = store.Set("book1", WriteImage("a.png", 300, 400), 0);
            var second = store.Set("book1", WriteImage("b.png", 320, 420), first.Version);

            Assert.AreEqual(1, first.Version);
            Assert.AreEqual(2, second.Version);
        });
    }

    [TestMethod]
    public void RemovingACoverLeavesTheBookWithoutOne()
    {
        WpfTestHost.Run(() =>
        {
            var store = Store();
            Assert.IsTrue(store.Set("book1", WriteImage("cover.png", 300, 400), 0).Succeeded);

            Assert.IsTrue(store.Remove("book1"));
            Assert.IsFalse(store.Has("book1"));
            Assert.IsNull(store.Load("book1"));
        });
    }

    [TestMethod]
    public void RemovingACoverThatIsNotThereIsNotAFailure()
    {
        WpfTestHost.Run(() =>
        {
            var store = Store();
            Assert.IsTrue(store.Remove("never-had-one"), "the asked-for outcome already holds");
        });
    }

    [TestMethod]
    public void LoadingDoesNotHoldTheFileOpen()
    {
        // Otherwise replacing a cover fails while the shelf is showing it, which is every
        // time anyone would want to replace one.
        WpfTestHost.Run(() =>
        {
            var store = Store();
            Assert.IsTrue(store.Set("book1", WriteImage("cover.png", 300, 400), 0).Succeeded);

            _ = store.Load("book1");

            Assert.IsTrue(store.Set("book1", WriteImage("other.png", 300, 400), 1).Succeeded);
            Assert.IsTrue(store.Remove("book1"));
        });
    }

    [TestMethod]
    public void CoversAreKeptPerBook()
    {
        WpfTestHost.Run(() =>
        {
            var store = Store();
            Assert.IsTrue(store.Set("book1", WriteImage("one.png", 300, 400), 0).Succeeded);

            Assert.IsTrue(store.Has("book1"));
            Assert.IsFalse(store.Has("book2"));
        });
    }
}
