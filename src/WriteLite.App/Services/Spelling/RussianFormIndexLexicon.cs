using System.Diagnostics;
using WriteLite.Language.Core;
using WriteLite.Language.Russian;

namespace WriteLite.Services.Spelling;

/// <summary>
/// Exposes the three-million-form Russian index as a spelling lexicon.
/// </summary>
/// <remarks>
/// Hunspell answers "is this word derivable from a stem plus affixes", which is
/// a different question from "is this a form Russians actually write". The index
/// carries the full OpenCorpora paradigm set plus the curated modern vocabulary
/// — brands, internet and gaming slang, obscenities — that a 2008 stem list has
/// no way to contain, and it attaches part of speech, register flags and corpus
/// frequency to every form so suggestions can be ranked on more than closeness.
/// The two run side by side: a word either lexicon knows is not an error.
/// </remarks>
public sealed class RussianFormIndexLexicon : ISpellingLexicon, IDisposable
{
    private readonly RussianFormIndex _index;
    private readonly RussianCandidateGenerator _generator;
    private readonly RussianMorphologyAlternatives _alternatives;
    private bool _disposed;

    private RussianFormIndexLexicon(
        RussianFormIndex index,
        TimeSpan loadTime,
        ILayoutTargetLexicon? latinLexicon)
    {
        _index = index;

        // The English lexicon is what lets layout recovery reach a Latin target — "пшерги"
        // for "github". It loads lazily and only for a Cyrillic token the Russian index has
        // already rejected, so it costs nothing on text without typos.
        _generator = new RussianCandidateGenerator(index, latinLexicon);
        _alternatives = new RussianMorphologyAlternatives(index, RussianConfusionSets.TryLoad());
        LoadTime = loadTime;
    }

    public string Name => "writelite-ru-forms";
    public LanguageCode Language => LanguageCode.Russian;
    public bool IsReady => !_disposed;
    public int ApproximateWordCount => _index.WordCount;
    public TimeSpan LoadTime { get; }
    public string Source => RussianFormIndex.Source;
    public string License => RussianFormIndex.License;
    public RussianFormIndex Index => _index;

    /// <summary>Loads the index, or returns null when the artefacts are not deployed.</summary>
    public static RussianFormIndexLexicon? TryLoad(
        string? directory = null,
        ILayoutTargetLexicon? latinLexicon = null)
    {
        var sw = Stopwatch.StartNew();
        var index = RussianFormIndex.Load(directory);
        sw.Stop();
        if (index is null)
        {
            CompatibilityLogger.Technical("ru-form-index-missing", "falling back to hunspell only");
            return null;
        }

        CompatibilityLogger.Technical(
            "ru-form-index-loaded",
            $"forms={index.WordCount} meta={(index.HasMetadata ? 1 : 0)} ms={sw.Elapsed.TotalMilliseconds:F0}");
        return new RussianFormIndexLexicon(index, sw.Elapsed, latinLexicon ?? new EnglishLayoutLexicon());
    }

    public bool ContainsExact(string word)
    {
        if (_disposed || string.IsNullOrEmpty(word)) return false;
        return _index.Contains(word);
    }

    public IReadOnlyList<string> Suggest(string word, int maxSuggestions = 5)
        => RankCandidates(word, maxSuggestions).Select(c => c.Word).ToArray();

    /// <summary>Ranked candidates with their scores, for callers that need the confidence.</summary>
    public IReadOnlyList<RussianCandidate> RankCandidates(
        string word,
        int take = 5,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(word)) return [];
        var ranked = _generator.Rank(word, Math.Max(take, 5), cancellationToken);
        return RussianCorrectionConfidence.Suggestions(ranked, take);
    }

    /// <summary>
    /// Alternatives for a token that is <em>already a valid word</em>, which the spelling
    /// generator structurally cannot produce.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Suggest"/> on purpose. A caller asking for suggestions is
    /// asking about a misspelling and must keep getting nothing for a correct word; a caller
    /// asking for alternatives is asking a context question and has to be prepared to answer
    /// it. Merging the two would put тся/ться candidates in front of the spelling layer,
    /// which has no way to choose between them.
    /// </remarks>
    public IReadOnlyList<string> ContextualAlternatives(string word)
        => _disposed ? [] : _alternatives.For(word);

    /// <summary>How many confusion sets the alternatives layer loaded, for reporting.</summary>
    public int ConfusionSetCount => _alternatives.ConfusionSetCount;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _index.Dispose();
    }
}
