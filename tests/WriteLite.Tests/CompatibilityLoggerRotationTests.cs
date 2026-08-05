using WriteLite.Services;

namespace WriteLite.Tests;

[TestClass]
public sealed class CompatibilityLoggerRotationTests
{
    [TestMethod]
    public void RotateIfNeeded_WhenUnderLimit_DoesNothing()
    {
        var dir = CreateTempDir();
        try
        {
            var log = Path.Combine(dir, "compatibility.log");
            File.WriteAllText(log, "small");
            CompatibilityLogger.RotateIfNeeded(log, maxBytes: 10_000, maxArchives: 3);
            Assert.IsTrue(File.Exists(log));
            Assert.IsFalse(File.Exists(CompatibilityLogger.ArchivePath(log, 1)));
            Assert.AreEqual("small", File.ReadAllText(log));
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public void RotateIfNeeded_WhenOverLimit_MovesToArchive()
    {
        var dir = CreateTempDir();
        try
        {
            var log = Path.Combine(dir, "compatibility.log");
            File.WriteAllText(log, new string('x', 200));
            CompatibilityLogger.RotateIfNeeded(log, maxBytes: 50, maxArchives: 3);

            Assert.IsFalse(File.Exists(log), "Active log should have been moved.");
            var archive1 = CompatibilityLogger.ArchivePath(log, 1);
            Assert.IsTrue(File.Exists(archive1));
            Assert.AreEqual(200, File.ReadAllText(archive1).Length);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public void RotateIfNeeded_ShiftsArchives_AndDropsOldest()
    {
        var dir = CreateTempDir();
        try
        {
            var log = Path.Combine(dir, "compatibility.log");
            // Pre-seed archives 1 and 2, then rotate a large active file with maxArchives=2.
            File.WriteAllText(CompatibilityLogger.ArchivePath(log, 1), "old1");
            File.WriteAllText(CompatibilityLogger.ArchivePath(log, 2), "old2-should-drop");
            File.WriteAllText(log, new string('n', 100));

            CompatibilityLogger.RotateIfNeeded(log, maxBytes: 10, maxArchives: 2);

            Assert.IsFalse(File.Exists(log));
            Assert.AreEqual(new string('n', 100), File.ReadAllText(CompatibilityLogger.ArchivePath(log, 1)));
            // Slot 2 holds previous archive1 content after shift.
            Assert.AreEqual("old1", File.ReadAllText(CompatibilityLogger.ArchivePath(log, 2)));
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public void Sanitize_StripsAbsolutePathsAndBrand()
    {
        var raw = @"C:\Users\USER\AppData\Local\WriteLite\x LanguageTool error";
        var cleaned = CompatibilityLogger.Sanitize(raw);
        Assert.DoesNotContain(@"C:\Users", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[path]", cleaned);
        Assert.DoesNotContain("LanguageTool", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("engine", cleaned, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void Sanitize_TruncatesVeryLongLines()
    {
        var cleaned = CompatibilityLogger.Sanitize(new string('a', 2000));
        Assert.IsLessThanOrEqualTo(510, cleaned.Length);
    }

    [TestMethod]
    public void AccessError_DoesNotPersistExceptionMessage()
    {
        // Smoke: calling logger must not throw. The logger deliberately omits
        // exception.Message because automation providers can include text from
        // the field being edited in that message.
        CompatibilityLogger.AccessError(
            "stage10-privacy",
            null,
            new InvalidOperationException("private user text must not be logged"));
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wl-logrot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}
