using System.Diagnostics;
using System.IO;
using WriteLite.Language.Core;

namespace WriteLite.Services.Spelling;

/// <summary>
/// The English side of keyboard-layout recovery: a membership set, nothing more.
/// </summary>
/// <remarks>
/// WriteLite ships a 42 MB English lexical pack (Open English WordNet 2024) with synsets,
/// definitions and examples. Opening it to ask "is this an English word" while someone is
/// typing is not an option, so <c>ai/scripts/build_en_layout_lexicon.py</c> reduces it to a
/// sorted word list — about 79 000 entries, roughly 700 KB — together with the function
/// words WordNet structurally omits and the technical vocabulary WriteLite users write in
/// the middle of Russian prose.
///
/// Loading is lazy and happens at most once. The only caller is the layout branch of the
/// Russian candidate generator, which is reached only for a token that is entirely Cyrillic
/// and already known not to be a Russian word — a rare enough path that a one-off 700 KB
/// read cannot land on a keystroke, and one that never runs at all for text without typos.
/// </remarks>
public sealed class EnglishLayoutLexicon : ILayoutTargetLexicon
{
    private readonly Lazy<Data> _data;

    public EnglishLayoutLexicon(string? directory = null)
    {
        var root = directory ?? Path.Combine(AppContext.BaseDirectory, "resources", "lexical");
        _data = new Lazy<Data>(() => Load(root), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Words available, or zero when the resource is not deployed.</summary>
    public int WordCount => _data.Value.Words.Count;

    public bool Contains(string word)
        => !string.IsNullOrEmpty(word) && _data.Value.Words.Contains(word);

    public bool IsTechnicalTerm(string word)
        => !string.IsNullOrEmpty(word) && _data.Value.Technical.Contains(word);

    private static Data Load(string directory)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var technical = new HashSet<string>(StringComparer.Ordinal);
        var sw = Stopwatch.StartNew();

        try
        {
            var wordsPath = Path.Combine(directory, "en-layout-words.txt");
            if (File.Exists(wordsPath))
            {
                foreach (var line in File.ReadLines(wordsPath))
                {
                    if (line.Length > 0)
                    {
                        words.Add(line);
                    }
                }
            }

            // Read separately rather than merged away, because "is a technical term" is a
            // ranking signal in its own right and the generated list cannot distinguish it
            // from an ordinary WordNet noun. Function words are membership only — they are
            // short, and a short Cyrillic string collides with a short English word far too
            // easily for membership alone to be worth a rank bonus.
            foreach (var word in ReadCurated(Path.Combine(directory, "en-layout-function-words.txt")))
            {
                words.Add(word);
            }

            foreach (var word in ReadCurated(Path.Combine(directory, "en-layout-technical.txt")))
            {
                words.Add(word);
                technical.Add(word);
            }
        }
        catch (IOException)
        {
            // A missing or unreadable list means layout recovery keeps the behaviour it had
            // before this file existed. It must never be the reason spelling stops working.
        }

        sw.Stop();
        CompatibilityLogger.Technical(
            "en-layout-lexicon-loaded",
            $"words={words.Count} technical={technical.Count} ms={sw.Elapsed.TotalMilliseconds:F0}");

        return new Data(words, technical);
    }

    private static IEnumerable<string> ReadCurated(string path)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw;
            var comment = line.IndexOf('#', StringComparison.Ordinal);
            if (comment >= 0)
            {
                line = line[..comment];
            }

            line = line.Trim().ToLowerInvariant();
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }

    private sealed record Data(HashSet<string> Words, HashSet<string> Technical);
}
