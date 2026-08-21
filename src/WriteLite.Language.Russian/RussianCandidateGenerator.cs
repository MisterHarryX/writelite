using WriteLite.Language.Core;

namespace WriteLite.Language.Russian;

/// <summary>How a candidate was produced. Different origins deserve different trust.</summary>
public enum RussianCandidateOrigin
{
    EditDistance = 0,

    /// <summary>Latin keystrokes reinterpreted as the Russian letters on the same keys.</summary>
    KeyboardLayout = 1,

    Homoglyph = 2,
    WordSplit = 3,
    Phonetic = 4,

    /// <summary>
    /// Cyrillic keystrokes reinterpreted as the Latin letters on the same keys — the other
    /// direction, which resolves against an English lexicon rather than the Russian index.
    /// </summary>
    KeyboardLayoutLatin = 5,
}

public sealed record RussianCandidate(
    string Word,
    int Distance,
    RussianCandidateOrigin Origin,
    double Score,
    RussianFormIndex.FormInfo Info)
{
    /// <summary>Set when the candidate is a two-word split of a merged token.</summary>
    public bool IsMultiWord => Word.Contains(' ');
}

/// <summary>
/// Produces and ranks correction candidates for a Russian token.
/// </summary>
/// <remarks>
/// Hunspell's suggester ranks by its own generic heuristics, which are not tuned
/// for Russian typing: it turned "дила" into "дали" rather than "дела" often
/// enough that the project had to keep a hand-written override table. This
/// generator scores candidates on the signals that actually discriminate —
/// edit distance, key adjacency, corpus frequency, the specific phonetic and
/// orthographic confusions Russian writers make, and register flags — so the
/// override table stops being the only thing standing between a user and a
/// wrong correction.
/// </remarks>
public sealed class RussianCandidateGenerator : ISpellingCandidateGenerator
{
    private const int EditDistanceOneCap = 1024;
    private const int EditDistanceTwoCap = 4096;

    private readonly RussianFormIndex _index;
    private readonly ILayoutTargetLexicon? _latinLexicon;

    public RussianCandidateGenerator(RussianFormIndex index, ILayoutTargetLexicon? latinLexicon = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _latinLexicon = latinLexicon;
    }

    public IReadOnlyList<string> Generate(string word, LanguageCode language, int maxCandidates = 32)
        => Rank(word, maxCandidates).Select(c => c.Word).ToArray();

    /// <summary>Ranked candidates, best first.</summary>
    public IReadOnlyList<RussianCandidate> Rank(
        string word,
        int take = 8,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(word) || word.Length < 2) return [];

        var lower = word.ToLowerInvariant();

        // Carried separately because scoring works on the lowercased form: the register
        // penalty that stops «мере» becoming «Море» must not also stop «Шолохава» becoming
        // «Шолохова», and only the typed capitalisation separates those two.
        var typedCapitalised = char.IsUpper(word[0]);
        var candidates = new Dictionary<string, RussianCandidate>(StringComparer.Ordinal);

        void Offer(string candidate, int distance, RussianCandidateOrigin origin, bool technical = false)
        {
            if (string.IsNullOrEmpty(candidate)) return;
            if (CorrectionCandidateValidityPolicy.IsMeaninglessSpellingCandidate(lower, candidate)) return;
            var info = candidate.Contains(' ') || origin is RussianCandidateOrigin.KeyboardLayoutLatin
                ? default
                : _index.GetInfo(candidate);
            var score = RussianCandidateScorer.Score(
                lower, candidate, distance, origin, info, technical, typedCapitalised);
            if (candidates.TryGetValue(candidate, out var existing) && existing.Score >= score) return;
            candidates[candidate] = new RussianCandidate(candidate, distance, origin, score, info);
        }

        // A whole word typed on the wrong layout is not a spelling error at all
        // and edit distance will never find it, so it is checked first.
        if (RussianKeyboard.IsLatinLayoutText(word))
        {
            var converted = RussianKeyboard.LatinToRussianLayout(lower);
            if (converted is not null && _index.Contains(converted))
            {
                Offer(converted, 0, RussianCandidateOrigin.KeyboardLayout);
            }
        }

        OfferLatinLayoutTarget(word, lower, Offer);

        // Mixed Cyrillic/Latin: usually a lookalike character, not a typo.
        if (RussianKeyboard.HasMixedScript(word))
        {
            var normalized = RussianKeyboard.NormalizeHomoglyphs(lower);
            if (normalized is not null && _index.Contains(normalized))
            {
                Offer(normalized, 0, RussianCandidateOrigin.Homoglyph);
            }
        }

        // One edit first. The result cap is a safety valve, not a ranking step:
        // the traversal visits the graph in lexicographic order, so truncating a
        // distance-2 sweep at a few dozen hits would silently drop everything
        // after "по…" and answer "превет" with "поревёт" instead of "привет".
        // Escalating only when the tight budget comes back empty keeps the
        // common case cheap and the wide case complete.
        var matches = _index.FindWithin(lower, 1, EditDistanceOneCap, cancellationToken);
        if (matches.Count == 0 && lower.Length > 4)
        {
            matches = _index.FindWithin(lower, 2, EditDistanceTwoCap, cancellationToken);
        }

        foreach (var match in matches)
        {
            Offer(match.Word, match.Distance, RussianCandidateOrigin.EditDistance);
        }

        foreach (var split in SplitCandidates(lower))
        {
            Offer(split, 1, RussianCandidateOrigin.WordSplit);
        }

        // Frequency breaks a tie before the alphabet does. «двер» scored «две» and «дверь»
        // at an identical 0.752 on the frozen corpus, and the alphabet chose «две» — a
        // correction decided by nothing at all. Frequency is a weak signal, which is why it
        // is a tie-break and not a term in the score, but it is a signal.
        return candidates.Values
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Distance)
            .ThenBy(c => c.Info.HasFrequency ? c.Info.FrequencyRank : int.MaxValue)
            .ThenBy(c => c.Word, StringComparer.Ordinal)
            .Take(take)
            .ToArray();
    }

    /// <summary>
    /// The other layout direction: Cyrillic keystrokes that were meant to be English.
    /// </summary>
    /// <remarks>
    /// Phase 4 found this direction missing entirely. The converter mapped ЙЦУКЕН→QWERTY and
    /// then resolved the result against the <em>Russian</em> lexicon only, so an English word
    /// typed with the Russian layout active could never be recovered: «пшерги» was answered
    /// with «перги», «пщщпду» with «пощаду», «знерщт» with «зверят». Three of the four wrong
    /// layout corrections in that run were this.
    ///
    /// The guards below are what keep this from becoming a machine for turning Russian into
    /// English lookalikes (§10, §12):
    ///
    /// <list type="number">
    /// <item>The token must be entirely Cyrillic. A mixed-script token is a homoglyph
    /// problem, not a layout one.</item>
    /// <item>It must not be a Russian word. «Открой GitHub» and «Запусти npm run build» never
    /// reach here at all, and neither does any correctly spelled Russian word.</item>
    /// <item>Its layout image must be in the English lexicon. Most Cyrillic strings map to
    /// consonant soup, which is exactly what makes this test discriminating.</item>
    /// <item>A two-letter image must be technical vocabulary. Short strings map to real
    /// English words by accident far too often for membership alone to mean anything.</item>
    /// </list>
    ///
    /// Passing all four still only <em>offers</em> the candidate. Whether it is shown depends
    /// on scoring against every Russian candidate, which is the §11 requirement not to take
    /// the first lexical hit.
    /// </remarks>
    private void OfferLatinLayoutTarget(
        string word,
        string lower,
        Action<string, int, RussianCandidateOrigin, bool> offer)
    {
        if (_latinLexicon is null || !RussianKeyboard.IsCyrillicText(word) || _index.Contains(lower))
        {
            return;
        }

        var latin = RussianKeyboard.RussianToLatinLayout(lower);
        if (latin is null || latin.Length < 2 || !latin.All(char.IsAsciiLetter))
        {
            return;
        }

        var technical = _latinLexicon.IsTechnicalTerm(latin);
        if (!technical && (latin.Length < 3 || !_latinLexicon.Contains(latin)))
        {
            return;
        }

        offer(RestoreCase(word, latin), 0, RussianCandidateOrigin.KeyboardLayoutLatin, technical);
    }

    /// <summary>
    /// Carries the typed capitalisation onto the recovered word, so «Пшерги» at the start of
    /// a sentence comes back as «Github» rather than lowercased mid-sentence.
    /// </summary>
    private static string RestoreCase(string original, string candidate)
    {
        if (candidate.Length == 0) return candidate;
        if (original.Length > 1 && original.All(c => !char.IsLetter(c) || char.IsUpper(c)))
        {
            return candidate.ToUpperInvariant();
        }

        return char.IsUpper(original[0])
            ? char.ToUpperInvariant(candidate[0]) + candidate[1..]
            : candidate;
    }

    /// <summary>
    /// "потомучто" -> "потому что". Both halves must be known and long enough
    /// that the split is not an accident of Russian's many two-letter words.
    /// </summary>
    private IEnumerable<string> SplitCandidates(string word)
    {
        if (word.Length < 6 || word.Contains(' ')) yield break;
        for (var i = 2; i <= word.Length - 2; i++)
        {
            var left = word[..i];
            var right = word[i..];
            if (!_index.Contains(left) || !_index.Contains(right)) continue;

            var leftInfo = _index.GetInfo(left);
            var rightInfo = _index.GetInfo(right);
            // Require both halves to be reasonably common; otherwise almost any
            // long word can be cut into two obscure but "known" forms.
            if (!leftInfo.HasFrequency || !rightInfo.HasFrequency) continue;
            if (leftInfo.FrequencyRank > 20000 || rightInfo.FrequencyRank > 20000) continue;
            yield return left + " " + right;
        }
    }
}
