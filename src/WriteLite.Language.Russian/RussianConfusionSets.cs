using System.Text.Json;

namespace WriteLite.Language.Russian;

/// <summary>
/// Groups of Russian words that are individually correct and mutually
/// confusable, so only context can pick the right one.
/// </summary>
/// <remarks>
/// "Компания проводит исследование" and "Кампания проводит исследование" are
/// both sequences of real Russian words, so no dictionary, edit distance or
/// frequency table can flag either. A spell checker built only on those signals
/// is structurally blind to this class of error. These sets tell the pipeline
/// which tokens are worth a second, context-aware look; the contextual reranker
/// then decides whether the sentence prefers a different member.
///
/// Authored in-repo (CC0-1.0) and shipped as
/// <c>resources/lexical/ru-confusion-sets.json</c>.
/// </remarks>
public sealed class RussianConfusionSets
{
    private readonly Dictionary<string, string[]> _alternatives;

    private RussianConfusionSets(Dictionary<string, string[]> alternatives, int setCount)
    {
        _alternatives = alternatives;
        SetCount = setCount;
    }

    public int SetCount { get; }
    public int WordCount => _alternatives.Count;

    /// <summary>Loads the sets, or returns null when the resource is not deployed.</summary>
    public static RussianConfusionSets? TryLoad(string? path = null)
    {
        var resolved = path ?? Resolve();
        if (resolved is null || !File.Exists(resolved)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(resolved));
            if (!document.RootElement.TryGetProperty("sets", out var sets)) return null;

            var alternatives = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var setCount = 0;
            foreach (var set in sets.EnumerateArray())
            {
                if (!set.TryGetProperty("members", out var members)) continue;
                var words = members.EnumerateArray()
                    .Select(m => m.GetString())
                    .Where(w => !string.IsNullOrWhiteSpace(w))
                    .Select(w => w!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (words.Length < 2) continue;
                setCount++;

                foreach (var word in words)
                {
                    if (!alternatives.TryGetValue(word, out var list))
                    {
                        list = [];
                        alternatives[word] = list;
                    }

                    // A word can appear in several sets (тся/ться forms overlap
                    // heavily), so alternatives accumulate rather than replace.
                    foreach (var other in words)
                    {
                        if (!string.Equals(other, word, StringComparison.OrdinalIgnoreCase)
                            && !list.Contains(other, StringComparer.OrdinalIgnoreCase))
                        {
                            list.Add(other);
                        }
                    }
                }
            }

            return new RussianConfusionSets(
                alternatives.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                setCount);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static string? Resolve()
    {
        foreach (var root in new[]
                 {
                     AppContext.BaseDirectory,
                     Path.Combine(AppContext.BaseDirectory, "resources", "lexical"),
                     Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "resources", "lexical")),
                     Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "resources", "lexical")),
                 })
        {
            var candidate = Path.Combine(root, "ru-confusion-sets.json");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>Words this one is confusable with, or empty.</summary>
    public IReadOnlyList<string> AlternativesFor(string word)
        => _alternatives.TryGetValue(word, out var alternatives) ? alternatives : [];

    public bool IsConfusable(string word) => _alternatives.ContainsKey(word);
}
