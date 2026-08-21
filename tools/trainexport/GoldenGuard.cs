using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrainExport;

/// <summary>
/// Refuses to let anything from the golden benchmark into a training export.
/// </summary>
/// <remarks>
/// <para>Golden data may be read to understand what is failing. It may not be trained on.
/// Every published quality number in <c>docs/</c> is a statement about those exact 841
/// items, and a model that has seen them turns the whole series of before/after tables into
/// a measurement of memorisation — silently, and irreversibly, because there is no way to
/// un-train a model or to tell after the fact which items it had seen.</para>
///
/// <para>Blocking by id alone would be theatre: the ids never leave the benchmark file, and
/// nothing would stop a pipeline that copied the sentences. The guard is therefore on
/// <em>content</em> — a normalised hash of every golden sentence, in both its broken and its
/// corrected form — with ids kept as a second, cheaper check. Normalisation folds case,
/// collapses whitespace and drops punctuation, so re-punctuating or re-casing a golden
/// sentence does not smuggle it through.</para>
///
/// <para>The exporter counts what it blocks and writes that count into the manifest. Zero is
/// the expected number and a non-zero one is a finding, not a nuisance: it means a source
/// corpus overlaps the benchmark and the overlap needs explaining.</para>
/// </remarks>
internal sealed class GoldenGuard
{
    private readonly HashSet<string> _sentenceHashes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    public int BlockedCount { get; private set; }

    public int GoldenSentenceCount => _sentenceHashes.Count;

    public int GoldenItemCount => _ids.Count;

    public static GoldenGuard FromCorpus(string goldenPath)
    {
        var guard = new GoldenGuard();
        foreach (var line in File.ReadLines(goldenPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } idText)
            {
                guard._ids.Add(idText);
            }

            // Both forms. The corrected sentence is what a training example would most
            // naturally contain, and it is the one an exporter is most likely to reach for.
            foreach (var field in new[] { "source", "target" })
            {
                if (root.TryGetProperty(field, out var value) && value.GetString() is { Length: > 0 } text)
                {
                    guard._sentenceHashes.Add(Fingerprint(text));
                }
            }
        }

        return guard;
    }

    /// <summary>True when this example must not be exported.</summary>
    public bool IsGolden(string sentence, string? id = null)
    {
        var blocked = (id is not null && _ids.Contains(id)) || _sentenceHashes.Contains(Fingerprint(sentence));
        if (blocked)
        {
            BlockedCount++;
        }

        return blocked;
    }

    /// <summary>
    /// A content fingerprint that survives re-casing, re-spacing and re-punctuation.
    /// </summary>
    /// <remarks>
    /// Punctuation is dropped deliberately, even though one of the exported tasks is about
    /// punctuation: the question here is "is this the same sentence", and a golden item with
    /// its commas moved is still the golden item.
    /// </remarks>
    public static string Fingerprint(string sentence)
    {
        var builder = new StringBuilder(sentence.Length);
        var lastWasSpace = true;
        foreach (var ch in sentence)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch) == 'ё' ? 'е' : char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        var normalised = builder.ToString().Trim();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));
    }
}
