using WriteLite.Services.Lexical;

namespace WriteLite.Tests.Lexical;

[TestClass]
public sealed class LexicalPackCatalogServiceTests
{
    [TestMethod]
    public void Snapshot_ReportsBundledCorePackAndLicense()
    {
        var lexicalRoot = Path.Combine(AppContext.BaseDirectory, "resources", "lexical");
        var service = new LexicalPackCatalogService(lexicalRoot, NewTempDirectory(), Path.Combine(AppContext.BaseDirectory, "missing-engine"));
        var core = service.Snapshot().Single(pack => pack.Id == "writelight-lexical-core");

        Assert.AreEqual("CC0-1.0", core.License);
        Assert.IsGreaterThanOrEqualTo(170, core.EntryCount ?? 0);
        Assert.AreEqual("Русский", core.Language);
        Assert.IsFalse(core.DataType.Contains("перевод", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(core.IsBuiltIn);
        Assert.IsTrue(core.IsValid);
    }

    [TestMethod]
    public void Snapshot_ReportsLargeOpenRussianPack()
    {
        var lexicalRoot = Path.Combine(AppContext.BaseDirectory, "resources", "lexical");
        var service = new LexicalPackCatalogService(lexicalRoot, NewTempDirectory(), Path.Combine(AppContext.BaseDirectory, "missing-engine"));
        var open = service.Snapshot().Single(pack => pack.Id == "writelight-lexical-open");

        Assert.AreEqual("CC-BY-SA-4.0", open.License);
        Assert.IsGreaterThanOrEqualTo(58_000, open.EntryCount ?? 0);
        Assert.AreEqual("Русский", open.Language);
        Assert.IsTrue(open.IsBuiltIn);
        Assert.IsTrue(open.IsValid);
    }

    /// <summary>
    /// The user-facing list must never contain a WriteLite pack.
    /// </summary>
    /// <remarks>
    /// The old dictionary screen listed the built-in packs — with their file sizes,
    /// licence strings and paths — and offered Delete and Disable on them. The service
    /// refused both, so the buttons could only ever produce an error dialog. Built-in
    /// dictionaries are application infrastructure; «Мои словари» is fed by
    /// <see cref="LexicalPackCatalogService.UserPacks"/>, and this is the guarantee
    /// that keeps them out of it.
    /// </remarks>
    [TestMethod]
    public void UserPacks_ExcludesEveryBuiltInPack()
    {
        var lexicalRoot = Path.Combine(AppContext.BaseDirectory, "resources", "lexical");
        var userRoot = NewTempDirectory();
        try
        {
            var service = new LexicalPackCatalogService(lexicalRoot, userRoot, lexicalRoot);

            // Snapshot sees the bundled packs; the user-facing list must not.
            Assert.IsTrue(
                service.Snapshot().Any(pack => pack.IsBuiltIn),
                "The fixture has no built-in packs, so this test would prove nothing.");

            var visible = service.UserPacks();

            Assert.IsEmpty(visible, "A built-in WriteLite pack reached the user-facing dictionary list.");
        }
        finally
        {
            Directory.Delete(userRoot, recursive: true);
        }
    }

    /// <summary>Built-ins stay refused even if something does reach them.</summary>
    [TestMethod]
    public void BuiltInPacks_CannotBeDisabledOrRemoved()
    {
        var lexicalRoot = Path.Combine(AppContext.BaseDirectory, "resources", "lexical");
        var userRoot = NewTempDirectory();
        try
        {
            var service = new LexicalPackCatalogService(lexicalRoot, userRoot, Path.Combine(userRoot, "engine-empty"));
            var builtIn = service.Snapshot().First(pack => pack.IsBuiltIn);

            Assert.ThrowsExactly<InvalidOperationException>(() => service.SetEnabled(builtIn, false));
            Assert.ThrowsExactly<InvalidOperationException>(() => service.Remove(builtIn));
            Assert.IsTrue(File.Exists(builtIn.Path), "A built-in pack file was removed.");
        }
        finally
        {
            Directory.Delete(userRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task Import_UsesValidatedUserPackAndCanDisableIt()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "resources", "lexical", "writelight-lexical-core.json");
        var userRoot = NewTempDirectory();
        try
        {
            var service = new LexicalPackCatalogService(Path.Combine(userRoot, "bundled-empty"), userRoot, Path.Combine(userRoot, "engine-empty"));
            var imported = await service.ImportAsync(source);
            Assert.IsTrue(imported.IsValid);
            Assert.IsFalse(imported.IsBuiltIn);

            service.SetEnabled(imported, false);
            var disabled = service.Snapshot().Single(pack => pack.Id == imported.Id);
            Assert.IsFalse(disabled.IsEnabled);

            service.Remove(disabled);
            Assert.IsEmpty(service.Snapshot());
        }
        finally
        {
            Directory.Delete(userRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task Import_CorruptPackFailsWithoutLeavingTemporaryFile()
    {
        var userRoot = NewTempDirectory();
        var source = Path.Combine(userRoot, "broken.json");
        await File.WriteAllTextAsync(source, "{broken");
        var service = new LexicalPackCatalogService(Path.Combine(userRoot, "bundled"), Path.Combine(userRoot, "packs"), Path.Combine(userRoot, "engine"));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(source));
        var packsRoot = Path.Combine(userRoot, "packs");
        Assert.IsFalse(Directory.Exists(packsRoot) && Directory.EnumerateFiles(packsRoot, "*.tmp").Any());
        Directory.Delete(userRoot, recursive: true);
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "writelite-pack-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
