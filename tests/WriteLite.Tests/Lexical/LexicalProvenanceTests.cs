using System.Security.Cryptography;
using System.Text.Json;

namespace WriteLite.Tests.Lexical;

/// <summary>
/// Keeps resources/lexical/sources.manifest.json honest.
///
/// The manifest is what we can point at to show where every shipped byte came
/// from and under which licence. Before these tests it was hand-maintained and
/// had already drifted from the artifacts it described, so the numbers in the
/// documentation were not the numbers on disk.
/// </summary>
[TestClass]
public sealed class LexicalProvenanceTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WriteLite.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        Assert.Inconclusive("repository root not found from the test output directory.");
        return "";
    }

    private static JsonElement Manifest()
    {
        var path = Path.Combine(RepoRoot(), "resources", "lexical", "sources.manifest.json");
        Assert.IsTrue(File.Exists(path), $"provenance manifest missing: {path}");
        return JsonDocument.Parse(File.ReadAllBytes(path)).RootElement;
    }

    private static string Sha256Hex(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [TestMethod]
    public void EveryArtifact_MatchesItsRecordedHashAndSize()
    {
        var root = RepoRoot();
        var checkedAny = false;

        foreach (var artifact in Manifest().GetProperty("artifacts").EnumerateArray())
        {
            var relative = artifact.GetProperty("path").GetString()!;
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

            // writelight-lexical-open.json is a build output and is git-ignored,
            // so a clean checkout legitimately does not have it yet.
            if (!File.Exists(path)) continue;

            // The SQLite database records its own build date, so its bytes are not
            // reproducible across days and it declares no hash. Everything that
            // declares one must match exactly.
            if (!artifact.TryGetProperty("sha256", out var expectedHash)) continue;
            checkedAny = true;

            var info = new FileInfo(path);
            Assert.AreEqual(artifact.GetProperty("bytes").GetInt64(), info.Length,
                $"{relative}: size differs from the manifest; rerun the builder and update the manifest.");
            Assert.AreEqual(expectedHash.GetString(), Sha256Hex(path),
                $"{relative}: sha256 differs from the manifest.");
        }

        Assert.IsTrue(checkedAny, "no artifact listed in the manifest was present on disk.");
    }

    [TestMethod]
    public void EveryJsonPack_HasTheRecordedEntryCount()
    {
        var root = RepoRoot();

        foreach (var artifact in Manifest().GetProperty("artifacts").EnumerateArray())
        {
            if (!artifact.TryGetProperty("entries", out var expected)) continue;

            var relative = artifact.GetProperty("path").GetString()!;
            // The SQLite database also declares an entry count, but it is verified
            // by SqliteLexicalStoreTests, not by parsing it as JSON.
            if (!relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var actual = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.GetArrayLength()
                : document.RootElement.GetProperty("entries").GetArrayLength();

            Assert.AreEqual(expected.GetInt32(), actual, $"{relative}: entry count differs from the manifest.");
        }
    }

    [TestMethod]
    public void EveryBundledSource_DeclaresLicenceAndRedistributionTerms()
    {
        foreach (var source in Manifest().GetProperty("sources").EnumerateArray())
        {
            var name = source.GetProperty("sourceName").GetString();
            if (!source.GetProperty("inDefaultRelease").GetBoolean()) continue;

            foreach (var field in new[] { "license", "attributionRequirements", "commercialUse", "redistribution" })
            {
                Assert.IsTrue(source.TryGetProperty(field, out var value), $"{name}: missing '{field}'.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(value.GetString()), $"{name}: '{field}' is empty.");
            }

            var redistribution = source.GetProperty("redistribution").GetString()!;
            Assert.IsFalse(redistribution.Contains("blocked", StringComparison.OrdinalIgnoreCase),
                $"{name}: shipped in the default release but redistribution is blocked.");
        }
    }

    [TestMethod]
    public void RestrictedSources_AreNotShipped()
    {
        // Ozhegov and Dal have no verified redistribution licence. They may only
        // ever arrive as a user-supplied pack, never inside the default release.
        foreach (var source in Manifest().GetProperty("sources").EnumerateArray())
        {
            var name = source.GetProperty("sourceName").GetString() ?? "";
            if (name.Contains("Ozhegov", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Dal ", StringComparison.OrdinalIgnoreCase))
            {
                Assert.IsFalse(source.GetProperty("inDefaultRelease").GetBoolean(),
                    $"{name} must never be part of the default release.");
            }
        }
    }

    [TestMethod]
    public void IncompatiblyLicensedSources_AreNotMergedIntoALexicalPack()
    {
        // The AOT/Abramov thesaurus is LGPL-2.1, which cannot be combined with the
        // CC-BY-SA-4.0 Russian pack into a single work. A copyleft-licensed file
        // shipped on its own (the ru_RU Hunspell dictionary) is fine -- what is
        // forbidden is merging it into a differently licensed pack.
        foreach (var source in Manifest().GetProperty("sources").EnumerateArray())
        {
            var license = source.GetProperty("license").GetString() ?? "";
            if (!license.StartsWith("LGPL", StringComparison.OrdinalIgnoreCase)
                && !license.StartsWith("GPL", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var merged = source.GetProperty("mergedIntoPack");
            Assert.AreEqual(JsonValueKind.Null, merged.ValueKind,
                $"{source.GetProperty("sourceName").GetString()}: copyleft data must not be merged into a lexical pack.");
        }
    }

    [TestMethod]
    public void EverySourceMergedIntoAPack_SharesThatPacksLicence()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["writelight-lexical-open"] = "CC-BY-SA-4.0",
            ["writelight-lexical-en"] = "CC-BY-4.0",
            // The Russian form index is share-alike because OpenCorpora is; the
            // frequency and curated layers folded into it are permissive.
            ["writelight-ru-forms"] = "CC-BY-SA-3.0",
            // RU ↔ EN translations come from OpenRussian's translations_en column
            // and inherit that source's share-alike terms.
            ["writelight-translations-ru-en"] = "CC-BY-SA-4.0",
        };

        // Permissive licences carry no share-alike obligation, so they may be
        // combined into a pack of any licence as long as their notice travels
        // with it. Copyleft data merging into a differently licensed pack is
        // caught separately by IncompatiblyLicensedSources_AreNotMergedIntoALexicalPack.
        var permissive = new[] { "CC0", "MIT", "BSD", "Apache" };

        foreach (var source in Manifest().GetProperty("sources").EnumerateArray())
        {
            var merged = source.GetProperty("mergedIntoPack");
            if (merged.ValueKind == JsonValueKind.Null) continue;

            var pack = merged.GetString()!;
            Assert.IsTrue(expected.ContainsKey(pack), $"unknown target pack '{pack}'.");

            var license = source.GetProperty("license").GetString()!;
            var name = source.GetProperty("sourceName").GetString();
            var isPermissive = permissive.Any(p => license.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(license == expected[pack] || isPermissive,
                $"{name}: licence '{license}' is not compatible with pack '{pack}' ({expected[pack]}).");
        }
    }
}
