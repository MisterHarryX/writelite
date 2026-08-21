using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CandCov;

internal sealed record CovError(
    int Start,
    int End,
    string Original,
    string Expected,
    string Type);

internal sealed record CovItem(
    string Id,
    string Category,
    string Domain,
    string Source,
    string Target,
    IReadOnlyList<CovError> Errors,
    IReadOnlyList<string> MustNotChange);

/// <summary>
/// Reads both corpus shapes the project has: the frozen golden benchmark (which carries a
/// <c>category</c>) and the synthesised error corpus (which carries a <c>domain</c> and puts
/// the error class on <c>errors[].type</c>). They agree on everything that matters here —
/// <c>source</c>, <c>target</c> and spans into <c>source</c> — so one reader serves both and
/// the same coverage number can be quoted for a development split and for the golden set.
/// </summary>
/// <remarks>
/// <para>The two disagree on one thing, and it is the field this tool is entirely about. The
/// golden set records an <em>annotation</em>: <c>original</c> is the broken text and
/// <c>replacement</c> is the fix. The error corpus records a <em>corruption</em>:
/// <c>original</c> is the word the generator started from and <c>replacement</c> is what it
/// wrote instead. The two names therefore mean opposite things, and reading either one by
/// name gives the wrong answer on half the project's data.</para>
///
/// <para>Resolved by position rather than by name: whichever field is not what
/// <c>source</c> actually contains at the span is the answer being looked for. That is true
/// under both conventions and stays true if a third corpus arrives with a third convention.</para>
/// </remarks>
internal static class Corpus
{
    public static IReadOnlyList<CovItem> Load(string path)
    {
        var items = new List<CovItem>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var raw = JsonSerializer.Deserialize<RawItem>(line);
            if (raw?.Source is null) continue;

            items.Add(new CovItem(
                raw.Id ?? "",
                raw.Category ?? "unknown",
                raw.Domain ?? "unknown",
                raw.Source,
                raw.Target ?? raw.Source,
                (raw.Errors ?? [])
                    .Where(e => e.Start >= 0 && e.End > e.Start && e.End <= raw.Source.Length)
                    .Select(e => Normalise(raw.Source, e))
                    .Where(e => e.Expected.Length > 0)
                    .ToList(),
                raw.MustNotChange ?? []));
        }

        return items;
    }

    private static CovError Normalise(string source, RawError raw)
    {
        var written = source[raw.Start..raw.End];
        var original = raw.Original ?? "";
        var replacement = raw.Replacement ?? "";

        var expected =
            !string.Equals(replacement, written, StringComparison.Ordinal) && replacement.Length > 0
                ? replacement
                : !string.Equals(original, written, StringComparison.Ordinal) && original.Length > 0
                    ? original
                    : "";

        return new CovError(raw.Start, raw.End, written, expected, raw.Type ?? "unknown");
    }

    private sealed class RawItem
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("domain")] public string? Domain { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("target")] public string? Target { get; set; }
        [JsonPropertyName("errors")] public List<RawError>? Errors { get; set; }
        [JsonPropertyName("must_not_change")] public List<string>? MustNotChange { get; set; }
    }

    private sealed class RawError
    {
        [JsonPropertyName("start")] public int Start { get; set; }
        [JsonPropertyName("end")] public int End { get; set; }
        [JsonPropertyName("original")] public string? Original { get; set; }
        [JsonPropertyName("replacement")] public string? Replacement { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }
}
