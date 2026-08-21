using System.IO;
using WriteLite.Services.Lexical;

namespace WriteLite.Tests.Lexical;

/// <summary>
/// What the release actually ships for the dictionary, and that it is enough.
/// </summary>
/// <remarks>
/// Phase 7 §58 measured 94 MB of duplication here: <c>writelight-lexical-open.json</c>
/// (53 MB) and <c>writelight-lexical-en.json</c> (41 MB) hold content that
/// <c>writelight-lexical.db</c> already carries in full, and
/// <see cref="OfflineLexicalKnowledgeService"/> opens the database first and returns before
/// it reads either of them.
///
/// Excluding them was tried and reverted, because <c>LexicalPackCatalogService</c> enumerates
/// installed packs by reading those files directly — the 94 MB is also where the CC-BY-SA-4.0
/// and CC-BY-4.0 attribution shown in the dictionary UI comes from. These tests pin the
/// arrangement as it actually ships, so that whoever reclaims the space later finds out
/// immediately if they broke the catalog or the dictionary while doing it.
/// </remarks>
[TestClass]
public sealed class ShippedLexicalResourcesTests
{
    private static string LexicalDirectory()
        => Path.Combine(AppContext.BaseDirectory, "resources", "lexical");

    [TestMethod]
    public void TheLexicalDatabaseIsShipped()
    {
        var path = Path.Combine(LexicalDirectory(), "writelight-lexical.db");

        Assert.IsTrue(File.Exists(path), path);
        Assert.IsGreaterThan(1_000_000L, new FileInfo(path).Length, "database is implausibly small");
    }

    [TestMethod]
    public void TheJsonPacksAreShippedForTheCatalogAndTheFallbackPath()
    {
        // Not because the dictionary needs them — the database serves every lookup — but
        // because the pack catalog reads them to list what is installed and under which
        // licence. See the class remarks before deleting either.
        foreach (var name in new[]
        {
            "writelight-lexical-open.json",
            "writelight-lexical-en.json",
            "writelight-lexical-core.json",
        })
        {
            Assert.IsTrue(File.Exists(Path.Combine(LexicalDirectory(), name)), name);
        }
    }

    [TestMethod]
    public void TheRawSourceCorpusIsNotShipped()
    {
        // resources/lexical/source is build input for the packs, not a runtime resource.
        Assert.IsFalse(Directory.Exists(Path.Combine(LexicalDirectory(), "source")));
    }

    [TestMethod]
    public void TheDictionaryLoadsFromTheShippedFilesAndKnowsBothLanguages()
    {
        // Through the real service, from the real deployed directory — the check that the
        // packaging change did not leave the product with a 176-entry dictionary.
        var service = new OfflineLexicalKnowledgeService();
        service.LoadFromDirectory(LexicalDirectory());

        Assert.IsTrue(service.IsPackLoaded, service.LoadError ?? "pack not loaded");
        Assert.IsTrue(service.IsEnglishPackLoaded, "English lexical data is missing");
    }
}
