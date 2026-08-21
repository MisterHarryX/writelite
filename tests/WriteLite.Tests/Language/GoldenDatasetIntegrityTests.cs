using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WriteLite.Tests.Language;

/// <summary>
/// Keeps the frozen Russian benchmark frozen, and keeps it out of the training data.
/// </summary>
/// <remarks>
/// A golden set earns its name only if something enforces it. Before these tests the
/// corpus was untracked, its recorded hash disagreed with the file on disk (a CRLF
/// conversion, diagnosed 2026-08-12), and nothing anywhere would have noticed if an
/// item had been edited or if training data had started to overlap it.
///
/// The expected hash and version are pinned as constants <em>here</em>, not read from
/// the manifest. Reading them from the manifest would make the test agree with whatever
/// the manifest currently says, so regenerating the corpus would update both and the
/// test would pass while the benchmark silently changed underneath every published
/// result. Changing the corpus therefore has to be a deliberate two-file edit.
/// </remarks>
[TestClass]
public sealed class GoldenDatasetIntegrityTests
{
    /// <summary>
    /// sha256 of ru_frozen_v1.jsonl, over its exact bytes: UTF-8, LF line endings.
    /// Reproduce with `python ai/data/benchmark/_gen/build.py`.
    /// </summary>
    private const string GoldenSha256 =
        "16adfd7c763d8c17c696040a90178a8e7e9c83f5d6d09f99eb017538ec91293d";

    private const int GoldenVersion = 1;
    private const int GoldenItemCount = 841;

    /// <summary>
    /// Splits a training pipeline is allowed to read. The golden corpus is deliberately
    /// absent, and <see cref="TrainingSplits_ShareNoItemWithGolden"/> is what proves the
    /// separation actually holds rather than merely being intended.
    /// </summary>
    private static readonly string[] TrainingSplits =
    [
        "train.jsonl", "validation.jsonl", "test.jsonl",
        "rerank_train.jsonl", "rerank_validation.jsonl", "rerank_test.jsonl",
    ];

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

    private static string GoldenPath() => Path.Combine(
        RepoRoot(), "ai", "data", "benchmark", "ru_frozen_v1.jsonl");

    private static string MetaPath() => Path.Combine(
        RepoRoot(), "ai", "data", "benchmark", "ru_frozen_v1.meta.json");

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [TestMethod]
    public void Golden_MatchesItsPinnedHash()
    {
        var path = GoldenPath();
        Assert.IsTrue(File.Exists(path), $"golden corpus missing: {path}");

        var bytes = File.ReadAllBytes(path);
        var actual = Sha256Hex(bytes);

        if (actual == GoldenSha256)
        {
            return;
        }

        // Distinguish the two ways this fails, because the fixes are different.
        var normalised = Sha256Hex(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal)));

        Assert.Fail(normalised == GoldenSha256
            ? "The golden corpus has CRLF line endings; its content is intact but its bytes are "
              + "not canonical. Repair with `python ai/data/benchmark/_gen/build.py --force`. "
              + "If this recurs, .gitattributes has lost its `*.jsonl -text` rule."
            : $"The golden corpus content has changed.\n  expected {GoldenSha256}\n  actual   {actual}\n"
              + "ru_frozen_v1 is frozen: published benchmark results are only comparable against "
              + "these exact items. Add a ru_frozen_v2 instead of editing v1.");
    }

    [TestMethod]
    public void Golden_ManifestAgreesWithThePinnedConstants()
    {
        var path = MetaPath();
        Assert.IsTrue(File.Exists(path), $"golden manifest missing: {path}");

        var meta = JsonDocument.Parse(File.ReadAllBytes(path)).RootElement;

        Assert.AreEqual(
            GoldenSha256,
            meta.GetProperty("sha256").GetString(),
            "manifest sha256 disagrees with the hash pinned in this test — one of them was "
            + "changed without the other, which is exactly the drift this pair is here to catch");

        Assert.AreEqual(
            GoldenVersion,
            meta.GetProperty("version").GetInt32(),
            "manifest version disagrees with the version pinned in this test");

        Assert.AreEqual(GoldenItemCount, meta.GetProperty("item_count").GetInt32());
        Assert.AreEqual("golden", meta.GetProperty("role").GetString());
    }

    [TestMethod]
    public void Golden_HasTheItemCountAndCategoriesItsManifestClaims()
    {
        var meta = JsonDocument.Parse(File.ReadAllBytes(MetaPath())).RootElement;
        var lines = File.ReadAllLines(GoldenPath())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        Assert.HasCount(GoldenItemCount, lines, "item count changed");

        var actual = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var category = JsonDocument.Parse(line).RootElement.GetProperty("category").GetString() ?? "?";
            actual[category] = actual.GetValueOrDefault(category) + 1;
        }

        foreach (var declared in meta.GetProperty("counts_by_category").EnumerateObject())
        {
            Assert.AreEqual(
                declared.Value.GetInt32(),
                actual.GetValueOrDefault(declared.Name),
                $"category '{declared.Name}' count differs from the manifest");
        }

        Assert.HasCount(
            meta.GetProperty("counts_by_category").EnumerateObject().Count(),
            actual,
            "the corpus contains categories the manifest does not declare");
    }

    [TestMethod]
    public void TrainingSplits_ShareNoItemWithGolden()
    {
        var errorsDir = Path.Combine(RepoRoot(), "ai", "data", "errors");
        if (!Directory.Exists(errorsDir))
        {
            Assert.Inconclusive(
                $"training splits not present at {errorsDir}; regenerate with "
                + "ai/scripts/build_ru_error_corpus.py to run this check");
            return;
        }

        var golden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(GoldenPath()))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var item = JsonDocument.Parse(line).RootElement;
            golden.Add(Normalise(item.GetProperty("source").GetString()));
            golden.Add(Normalise(item.GetProperty("target").GetString()));
        }

        var checkedAny = false;
        foreach (var split in TrainingSplits)
        {
            var path = Path.Combine(errorsDir, split);
            if (!File.Exists(path)) continue;
            checkedAny = true;

            var leaks = new List<string>();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                foreach (var property in JsonDocument.Parse(line).RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String) continue;
                    var value = Normalise(property.Value.GetString());
                    if (value.Length > 0 && golden.Contains(value))
                    {
                        leaks.Add($"{split}: {property.Value.GetString()}");
                        break;
                    }
                }

                if (leaks.Count >= 5) break;
            }

            Assert.IsEmpty(
                leaks,
                $"training data contains golden items — a model trained on this would be scored "
                + $"on sentences it memorised:\n  {string.Join("\n  ", leaks)}");
        }

        Assert.IsTrue(checkedAny, "no training splits were found to check");
    }

    [TestMethod]
    public void TrainingConfigs_DoNotReferenceGoldenData()
    {
        var configDir = Path.Combine(RepoRoot(), "ai", "configs");
        Assert.IsTrue(Directory.Exists(configDir), $"training config directory missing: {configDir}");

        string[] forbidden = ["ru_frozen", "benchmark", "golden"];
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(configDir, "*.yaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var token in forbidden)
            {
                if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{Path.GetFileName(file)} references '{token}'");
                }
            }
        }

        Assert.IsEmpty(
            offenders,
            "a training configuration points at the golden benchmark, which would train on the "
            + "set used to report quality:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The generated training export must not contain a golden item, by id or by content.
    /// </summary>
    /// <remarks>
    /// <para><see cref="TrainingSplits_ShareNoItemWithGolden"/> checks the corpora a training
    /// pipeline reads. This checks what <c>tools/trainexport</c> actually wrote — the file a
    /// training run would be pointed at. The exporter has its own guard; this is the
    /// independent check that the guard worked, and it deliberately reimplements the
    /// normalisation rather than calling the exporter's, because a guard that validates
    /// itself validates nothing.</para>
    ///
    /// <para>Fingerprinting is stricter here than in <see cref="Normalise"/>: punctuation and
    /// case are both dropped, so re-punctuating or re-casing a golden sentence — which is
    /// exactly what a punctuation-task exporter does to its inputs — cannot slip one past.
    /// </para>
    /// </remarks>
    [TestMethod]
    public void TrainingExport_ContainsNoGoldenItem()
    {
        var exportDir = Path.Combine(RepoRoot(), "ai", "data", "training");
        if (!Directory.Exists(exportDir))
        {
            Assert.Inconclusive(
                $"no training export at {exportDir}; generate one with tools/trainexport to run this check");
            return;
        }

        var goldenIds = new HashSet<string>(StringComparer.Ordinal);
        var goldenContent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(GoldenPath()))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var item = JsonDocument.Parse(line).RootElement;
            if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } idText)
            {
                goldenIds.Add(idText);
            }

            goldenContent.Add(Fingerprint(item.GetProperty("source").GetString()));
            goldenContent.Add(Fingerprint(item.GetProperty("target").GetString()));
        }

        var leaks = new List<string>();
        var files = Directory.GetFiles(exportDir, "*.jsonl");
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var row = JsonDocument.Parse(line).RootElement;

                if (row.TryGetProperty("id", out var id)
                    && id.GetString() is { Length: > 0 } idText
                    && goldenIds.Contains(idText))
                {
                    leaks.Add($"{name}: golden id '{idText}'");
                }

                if (row.TryGetProperty("sentence", out var sentence)
                    && sentence.ValueKind == JsonValueKind.String)
                {
                    var fingerprint = Fingerprint(sentence.GetString());
                    if (fingerprint.Length > 0 && goldenContent.Contains(fingerprint))
                    {
                        leaks.Add($"{name}: golden sentence '{sentence.GetString()}'");
                    }
                }

                if (leaks.Count >= 5) break;
            }
        }

        Assert.IsNotEmpty(files, $"training export directory {exportDir} contains no .jsonl files");
        Assert.IsEmpty(
            leaks,
            "the training export contains golden benchmark items. Training on these would make "
            + "every published quality number a measurement of memorisation:\n  "
            + string.Join("\n  ", leaks));
    }

    /// <summary>
    /// The export must declare that it was guarded, and that nothing was blocked.
    /// </summary>
    /// <remarks>
    /// A non-zero block count is not a pass. It means a source corpus overlaps the benchmark,
    /// and that overlap needs explaining before the data is used — the guard stopping it this
    /// time says nothing about the next exporter someone writes.
    /// </remarks>
    [TestMethod]
    public void TrainingExport_DeclaresACleanGoldenGuard()
    {
        var manifestPath = Path.Combine(RepoRoot(), "ai", "data", "training", "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Assert.Inconclusive($"no training export manifest at {manifestPath}");
            return;
        }

        var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath)).RootElement;

        Assert.AreEqual(
            GoldenItemCount,
            manifest.GetProperty("goldenItemsGuarded").GetInt32(),
            "the export was guarded against a different number of golden items than the corpus has");

        Assert.AreEqual(
            0,
            manifest.GetProperty("blockedAsGolden").GetInt32(),
            "the exporter blocked golden items, which means a source corpus overlaps the benchmark");
    }

    /// <summary>Whitespace-collapsed, case-folded — so trivial reformatting cannot hide a leak.</summary>
    private static string Normalise(string? value)
        => string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Letters and digits only, case- and ё-folded. Survives re-punctuation and re-spacing.
    /// </summary>
    private static string Fingerprint(string? value)
    {
        var builder = new StringBuilder((value ?? "").Length);
        var lastWasSpace = true;
        foreach (var ch in value ?? "")
        {
            if (char.IsLetterOrDigit(ch))
            {
                var lower = char.ToLowerInvariant(ch);
                builder.Append(lower == 'ё' ? 'е' : lower);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }
}
