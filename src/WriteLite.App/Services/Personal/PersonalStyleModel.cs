using System.Text.RegularExpressions;

namespace WriteLite.Services.Personal;

/// <summary>Where a piece of text the model learned from came from.</summary>
public enum StyleLearningSource
{
    /// <summary>Typed in the WriteLite editor.</summary>
    Editor,

    /// <summary>Typed into another application's text field.</summary>
    OtherApp,

    /// <summary>One of the calibration texts.</summary>
    Calibration,

    /// <summary>A document the user declared as their own.</summary>
    Import,
}

/// <summary>A word the model expects the user to be typing.</summary>
/// <param name="Word">The whole word, in the casing it would be written.</param>
/// <param name="Completion">What would be inserted at the caret: the rest of the word, or the next word.</param>
/// <param name="Confidence">Share of the candidates' combined score held by this one, 0–1.</param>
/// <param name="IsNextWord">True when the caret sits after a space and the whole word is predicted.</param>
public sealed record WordPrediction(string Word, string Completion, double Confidence, bool IsNextWord);

/// <summary>Why the personal layer reordered a correction list.</summary>
public enum PersonalRankReason
{
    None,

    /// <summary>The user has corrected this exact typo to this word before.</summary>
    LearnedCorrection,

    /// <summary>The user writes this candidate often and the lexicon's first choice never.</summary>
    FrequentWord,
}

/// <summary>A correction list after the personal layer has looked at it.</summary>
public sealed record SuggestionRanking(IReadOnlyList<string> Suggestions, PersonalRankReason Reason, int Evidence)
{
    public static SuggestionRanking Unchanged(IReadOnlyList<string> suggestions) => new(suggestions, PersonalRankReason.None, 0);
}

/// <summary>A word the user has demonstrably written through «ё» and has now written through «е».</summary>
public sealed record YoPreference(int Start, int Length, string Written, string Preferred, int YoCount, int ECount);

/// <summary>One learned typo and the word it was corrected to.</summary>
public sealed record LearnedCorrection(string Typed, string Intended, int Count);

/// <summary>A word the checker does not know and the user keeps writing.</summary>
public sealed record FrequentUnknownWord(string Word, int Count, int Days);

/// <summary>What the model knows, for the «Мой стиль» page.</summary>
public sealed record PersonalStyleSnapshot(
    int TotalWords,
    int DistinctWords,
    int Sentences,
    double AverageSentenceLength,
    double CommasPerSentence,
    double? YoShare,
    double? GuillemetShare,
    double ExclamationShare,
    double QuestionShare,
    int CalibrationTexts,
    int PredictionsAccepted,
    IReadOnlyList<(string Word, int Count)> TopWords,
    IReadOnlyList<FrequentUnknownWord> FrequentUnknown,
    IReadOnlyList<LearnedCorrection> Corrections)
{
    public static readonly PersonalStyleSnapshot Empty = new(0, 0, 0, 0, 0, null, null, 0, 0, 0, 0, [], [], []);
}

/// <summary>
/// The user's own vocabulary and phrasing, learned from what they write.
/// </summary>
/// <remarks>
/// <para><b>What is stored.</b> Lower-cased word counts, counts of the word pairs and triples
/// the user writes, typos they corrected and what to, and a handful of punctuation counters.
/// Never a sentence: a 64-bit hash per learned sentence is kept only so the same sentence,
/// offered again on the next analysis pass, is not counted twice. That is the whole of the
/// privacy surface, and <see cref="Reset"/> removes it.</para>
///
/// <para><b>Only known words become vocabulary.</b> A word the spelling lexicon rejects is
/// counted separately and never predicted, so a habitual misspelling is not taught back to
/// the user as their style. The «Мой стиль» page lists the frequent ones so the user can add
/// the real ones (names, jargon) to their dictionary, which is the only way such a word
/// enters prediction.</para>
///
/// <para><b>Not thread-safe.</b> <see cref="PersonalStyleService"/> owns the lock; this type
/// is the arithmetic and is kept free of I/O so it can be tested as such.</para>
/// </remarks>
public sealed partial class PersonalStyleModel
{
    public const int MaxWords = 60_000;
    public const int MaxNgramEntries = 200_000;
    public const int MaxUnknown = 5_000;
    public const int MaxCorrections = 5_000;
    public const int MaxSentenceHashes = 60_000;

    /// <summary>Marks the start of a sentence in the pair table.</summary>
    internal const string SentenceStart = "<s>";

    /// <summary>
    /// Pairs of words that are both correct and mean different things. A user who writes «всё»
    /// a hundred times has said nothing about «все».
    /// </summary>
    private static readonly HashSet<string> YoHomographs = new(StringComparer.Ordinal)
    {
        "все", "нем", "небо", "осел", "слез", "мел", "лен", "падеж", "совершенный",
        "узнаем", "признаем", "передохнем", "истекший", "желчь", "береста", "елка",
    };

    internal readonly Dictionary<string, WordEntry> Words = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, Dictionary<string, int>> Bigrams = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, Dictionary<string, int>> Trigrams = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, UnknownEntry> Unknown = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, Dictionary<string, int>> Corrections = new(StringComparer.Ordinal);
    internal readonly StyleCounters Counters = new();

    private readonly HashSet<ulong> _sentenceHashes = [];
    private readonly Queue<ulong> _sentenceOrder = new();

    /// <summary>«е»-spelling → «ё»-spelling, for every learned word that contains «ё».</summary>
    private readonly Dictionary<string, string> _yoIndex = new(StringComparer.Ordinal);

    /// <summary>Vocabulary keys in ordinal order, for prefix lookup. Null when stale.</summary>
    private string[]? _sorted;

    private int _bigramEntries;
    private int _trigramEntries;

    /// <summary>Moves on every change, so a caller can tell whether there is anything to save.</summary>
    public long Revision { get; private set; }

    public int DistinctWords => Words.Count;

    public int TotalWords => Counters.Words;

    internal IEnumerable<ulong> SentenceHashes => _sentenceOrder;

    internal sealed class WordEntry
    {
        public int Count;

        /// <summary>Times written with a capital where a sentence did not force one.</summary>
        public int Capitalized;

        public int LastDay;
    }

    internal sealed class UnknownEntry
    {
        public int Count;
        public int Days;
        public int LastDay;
        public string Display = string.Empty;
    }

    internal sealed class StyleCounters
    {
        public int Words;
        public int Sentences;
        public int Commas;
        public int Guillemets;
        public int StraightQuotes;
        public int Exclamations;
        public int Questions;
        public int CalibrationTexts;
        public int PredictionsAccepted;
    }

    // ── Learning ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Learns from a text, sentence by sentence, skipping sentences already learned.
    /// </summary>
    /// <param name="text">Text the user wrote.</param>
    /// <param name="isKnownWord">The spelling lexicon. A word it rejects is not vocabulary.</param>
    /// <param name="day">Days since the epoch, for recency.</param>
    /// <param name="includeTrailingSentence">
    /// False when the caret may still be in the last sentence: an unterminated last sentence is
    /// then left for a later pass, when it is finished.
    /// </param>
    /// <returns>Number of words learned.</returns>
    public int Learn(string text, Func<string, bool> isKnownWord, int day, bool includeTrailingSentence)
    {
        var learned = 0;
        foreach (var sentence in SentencesToLearn(text, includeTrailingSentence))
        {
            learned += LearnSentence(sentence, isKnownWord, day);
        }

        return learned;
    }

    /// <summary>The sentences of <paramref name="text"/> worth learning, in order.</summary>
    internal static IEnumerable<string> SentencesToLearn(string text, bool includeTrailingSentence)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var protectedSpans = ProtectedTextSpans.Find(text);
        var spans = LanguageEngine.SentenceWindowBuilder.Split(text);
        for (var i = 0; i < spans.Count; i++)
        {
            var (start, length) = spans[i];
            var sentence = text.Substring(start, length);
            if (string.IsNullOrWhiteSpace(sentence)) continue;

            if (i == spans.Count - 1 && !includeTrailingSentence && !IsTerminated(sentence)) continue;

            // A link or a path in the middle of a sentence is not the user's phrasing; dropping
            // the sentence is cheaper than reasoning about what is left of it.
            if (ProtectedTextSpans.Overlaps(start, length, protectedSpans)) continue;
            if (LooksLikeCode(sentence)) continue;

            yield return sentence;
        }
    }

    /// <summary>Learns one sentence. Returns the number of words learned, 0 when already known.</summary>
    public int LearnSentence(string sentence, Func<string, bool> isKnownWord, int day)
    {
        var hash = SentenceHash(sentence);
        if (hash == 0 || _sentenceHashes.Contains(hash)) return 0;

        var tokens = Tokenize(sentence);
        if (tokens.Count == 0) return 0;

        RememberSentence(hash);

        string? previous2 = null;
        string? previous1 = SentenceStart;
        var learned = 0;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var key = Normalize(token);

            if (!isKnownWord(token))
            {
                CountUnknown(key, token, day);

                // A transition into or out of an unknown token is not phrasing worth predicting.
                previous2 = null;
                previous1 = null;
                continue;
            }

            if (!Words.TryGetValue(key, out var entry))
            {
                entry = new WordEntry();
                Words[key] = entry;
                _sorted = null;
                if (key.Contains('ё')) _yoIndex[key.Replace('ё', 'е')] = key;
            }

            entry.Count++;
            entry.LastDay = day;
            if (i > 0 && char.IsUpper(token[0]) && !IsAllUpper(token)) entry.Capitalized++;
            learned++;

            if (previous1 is not null)
            {
                if (Increment(Bigrams, previous1, key)) _bigramEntries++;
                if (previous2 is not null && Increment(Trigrams, previous2 + " " + previous1, key)) _trigramEntries++;
            }

            previous2 = previous1;
            previous1 = key;
        }

        Counters.Words += tokens.Count;
        Counters.Sentences++;
        foreach (var character in sentence)
        {
            switch (character)
            {
                case ',': Counters.Commas++; break;
                case '«': Counters.Guillemets++; break;
                case '"': Counters.StraightQuotes++; break;
            }
        }

        var trimmed = sentence.TrimEnd();
        if (trimmed.EndsWith('!')) Counters.Exclamations++;
        else if (trimmed.EndsWith('?')) Counters.Questions++;

        Prune();
        Revision++;
        return learned;
    }

    /// <summary>Remembers that <paramref name="typed"/> was corrected to <paramref name="intended"/>.</summary>
    public bool RecordCorrection(string typed, string intended)
    {
        var from = Normalize(typed.Trim());
        var to = Normalize(intended.Trim());
        if (from.Length < 2 || to.Length < 2 || from == to) return false;
        if (!Tokenize(from).SequenceEqual([from]) || to.Any(char.IsDigit)) return false;

        Increment(Corrections, from, to);
        if (Corrections.Count > MaxCorrections)
        {
            foreach (var stale in Corrections.Where(p => p.Value.Values.Sum() <= 1).Select(p => p.Key).Take(Corrections.Count - MaxCorrections + 100).ToArray())
            {
                Corrections.Remove(stale);
            }
        }

        Revision++;
        return true;
    }

    /// <summary>Forgets a learned correction.</summary>
    public bool ForgetCorrection(string typed, string intended)
    {
        var from = Normalize(typed);
        if (!Corrections.TryGetValue(from, out var targets) || !targets.Remove(Normalize(intended))) return false;
        if (targets.Count == 0) Corrections.Remove(from);
        Revision++;
        return true;
    }

    /// <summary>Drops a word from the frequent-unknown list (it was added to the dictionary or dismissed).</summary>
    public bool ForgetUnknown(string word)
    {
        if (!Unknown.Remove(Normalize(word))) return false;
        Revision++;
        return true;
    }

    public void RecordCalibrationText()
    {
        Counters.CalibrationTexts++;
        Revision++;
    }

    public void RecordPredictionAccepted()
    {
        Counters.PredictionsAccepted++;
        Revision++;
    }

    /// <summary>Forgets everything.</summary>
    public void Reset()
    {
        Words.Clear();
        Bigrams.Clear();
        Trigrams.Clear();
        Unknown.Clear();
        Corrections.Clear();
        _sentenceHashes.Clear();
        _sentenceOrder.Clear();
        _yoIndex.Clear();
        _sorted = null;
        _bigramEntries = 0;
        _trigramEntries = 0;
        Counters.Words = Counters.Sentences = Counters.Commas = Counters.Guillemets = Counters.StraightQuotes
            = Counters.Exclamations = Counters.Questions = Counters.PredictionsAccepted = Counters.CalibrationTexts = 0;
        Revision++;
    }

    // ── Prediction ────────────────────────────────────────────────────────────

    /// <summary>
    /// The word the user is most probably typing, or null when the evidence is not clear.
    /// </summary>
    /// <param name="textBeforeCaret">The paragraph up to the caret.</param>
    /// <remarks>
    /// <para>An interpolated trigram model over the user's own text: the two previous words,
    /// the previous word, and the typed prefix each vote, weighted in that order. Words are
    /// only ever drawn from the learned vocabulary, so nothing the user has not written before
    /// is suggested.</para>
    ///
    /// <para>The gates are deliberately strict. A wrong word painted in grey costs the user a
    /// glance every time it appears; a missing one costs nothing. So a completion must add at
    /// least two letters, the word must have been written at least twice, and it must hold a
    /// clear share of the candidates — more so the less has been typed.</para>
    /// </remarks>
    public WordPrediction? Predict(string textBeforeCaret)
    {
        if (string.IsNullOrEmpty(textBeforeCaret) || Words.Count == 0) return null;

        // The sentence fragment the caret is in.
        var boundary = textBeforeCaret.LastIndexOfAny(['.', '!', '?', '…', '\n', '\r']);
        var fragment = boundary >= 0 ? textBeforeCaret[(boundary + 1)..] : textBeforeCaret;

        var tokens = TokenRegex().Matches(fragment);
        string prefix;
        int contextCount;
        var last = fragment[^1];

        if (char.IsLetter(last))
        {
            if (tokens.Count == 0) return null;
            var current = tokens[^1];
            if (current.Index + current.Length != fragment.Length) return null;
            prefix = current.Value;
            contextCount = tokens.Count - 1;
        }
        else if (last == ' ')
        {
            // After a finished word (or a comma), never at the start of a sentence.
            var before = fragment.TrimEnd();
            if (before.Length == 0 || !(char.IsLetter(before[^1]) || before[^1] == ',')) return null;
            if (tokens.Count == 0) return null;
            prefix = string.Empty;
            contextCount = tokens.Count;
        }
        else
        {
            return null;
        }

        var previous1 = contextCount >= 1 ? Normalize(tokens[contextCount - 1].Value) : SentenceStart;
        var previous2 = contextCount >= 2 ? Normalize(tokens[contextCount - 2].Value) : contextCount == 1 ? SentenceStart : null;
        if (prefix.Length == 0 && previous1 == SentenceStart) return null;

        var lowerPrefix = Normalize(prefix);
        Bigrams.TryGetValue(previous1, out var bigram);
        Dictionary<string, int>? trigram = null;
        if (previous2 is not null) Trigrams.TryGetValue(previous2 + " " + previous1, out trigram);

        var bigramTotal = bigram?.Values.Sum() ?? 0;
        var trigramTotal = trigram?.Values.Sum() ?? 0;

        var candidates = new HashSet<string>(StringComparer.Ordinal);
        if (bigram is not null) candidates.UnionWith(bigram.Keys.Where(k => k.StartsWith(lowerPrefix, StringComparison.Ordinal)));
        if (trigram is not null) candidates.UnionWith(trigram.Keys.Where(k => k.StartsWith(lowerPrefix, StringComparison.Ordinal)));

        var prefixMatches = lowerPrefix.Length >= 2 ? PrefixMatches(lowerPrefix, 40) : [];
        candidates.UnionWith(prefixMatches);
        var prefixTotal = prefixMatches.Sum(w => Words[w].Count);

        string? best = null;
        double bestScore = 0, sum = 0;
        foreach (var candidate in candidates)
        {
            if (!Words.TryGetValue(candidate, out var entry)) continue;
            if (candidate.Length < lowerPrefix.Length + 2) continue;

            var pTri = trigram is not null && trigramTotal > 0 && trigram.TryGetValue(candidate, out var t) ? (double)t / trigramTotal : 0;
            var pBi = bigram is not null && bigramTotal > 0 && bigram.TryGetValue(candidate, out var b) ? (double)b / bigramTotal : 0;
            var pUni = prefixTotal > 0 ? (double)entry.Count / prefixTotal : 0;
            var score = (0.55 * pTri) + (0.30 * pBi) + (0.15 * pUni);
            if (score <= 0) continue;

            sum += score;
            if (score > bestScore || (score == bestScore && string.CompareOrdinal(candidate, best) < 0))
            {
                bestScore = score;
                best = candidate;
            }
        }

        if (best is null || sum <= 0) return null;

        var confidence = bestScore / sum;
        var bestEntry = Words[best];
        var bigramCount = bigram is not null && bigram.TryGetValue(best, out var bc) ? bc : 0;

        var accepted = lowerPrefix.Length switch
        {
            0 => bigramCount >= 3 && confidence >= 0.5,
            1 => bigramCount >= 2 && confidence >= 0.6,
            2 or 3 => bestEntry.Count >= 2 && confidence >= 0.45,
            _ => bestEntry.Count >= 2 && confidence >= 0.3,
        };
        if (!accepted) return null;

        var word = ApplyCasing(DisplayForm(best, bestEntry), prefix);
        var completion = word[prefix.Length..];
        return new WordPrediction(word, completion, Math.Round(confidence, 3), prefix.Length == 0);
    }

    /// <summary>Most-written vocabulary words starting with <paramref name="prefix"/>.</summary>
    private List<string> PrefixMatches(string prefix, int take)
    {
        _sorted ??= Words.Keys.Order(StringComparer.Ordinal).ToArray();
        var sorted = _sorted;

        var low = Array.BinarySearch(sorted, prefix, StringComparer.Ordinal);
        if (low < 0) low = ~low;

        var matches = new List<string>();
        for (var i = low; i < sorted.Length && sorted[i].StartsWith(prefix, StringComparison.Ordinal); i++)
        {
            matches.Add(sorted[i]);
        }

        return matches.Count <= take
            ? matches
            : matches.OrderByDescending(w => Words[w].Count).Take(take).ToList();
    }

    private static string DisplayForm(string key, WordEntry entry)
        => entry.Count > 0 && entry.Capitalized * 5 >= entry.Count * 4 ? Capitalize(key) : key;

    /// <summary>The word in the casing the typed prefix asks for.</summary>
    private static string ApplyCasing(string word, string prefix)
    {
        if (prefix.Length == 0) return word;
        if (prefix.Length >= 2 && IsAllUpper(prefix)) return word.ToUpperInvariant();

        // Keep exactly the letters the user typed; only the rest follows the learned form.
        return prefix + word[prefix.Length..];
    }

    // ── Correction ranking ────────────────────────────────────────────────────

    /// <summary>
    /// Reorders a spelling checker's candidates by what this user has meant before.
    /// </summary>
    /// <param name="typed">The word as written.</param>
    /// <param name="suggestions">The checker's candidates, best first.</param>
    /// <param name="isKnownWord">Lets a learned correction be offered even when the checker missed it.</param>
    public SuggestionRanking Rank(string typed, IReadOnlyList<string> suggestions, Func<string, bool>? isKnownWord = null)
    {
        var key = Normalize(typed);

        if (Corrections.TryGetValue(key, out var targets) && targets.Count > 0)
        {
            var (intended, count) = targets.MaxBy(p => p.Value);
            var index = IndexOf(suggestions, intended);
            if (index >= 0)
            {
                return new SuggestionRanking(MoveToFront(suggestions, index), PersonalRankReason.LearnedCorrection, count);
            }

            // Twice is a habit, and a habit is worth offering even when the edit-distance search
            // did not reach it — provided the lexicon agrees the word exists.
            if (count >= 2 && isKnownWord?.Invoke(intended) == true)
            {
                var cased = MatchCase(intended, typed);
                return new SuggestionRanking([cased, .. suggestions.Where(s => !s.Equals(cased, StringComparison.OrdinalIgnoreCase))], PersonalRankReason.LearnedCorrection, count);
            }
        }

        if (suggestions.Count < 2) return SuggestionRanking.Unchanged(suggestions);
        if (CountOf(suggestions[0]) > 0) return SuggestionRanking.Unchanged(suggestions);

        var bestIndex = -1;
        var bestCount = 0;
        for (var i = 1; i < suggestions.Count; i++)
        {
            var c = CountOf(suggestions[i]);
            if (c > bestCount)
            {
                bestCount = c;
                bestIndex = i;
            }
        }

        return bestIndex > 0 && bestCount >= 3
            ? new SuggestionRanking(MoveToFront(suggestions, bestIndex), PersonalRankReason.FrequentWord, bestCount)
            : SuggestionRanking.Unchanged(suggestions);
    }

    public int CountOf(string word) => Words.TryGetValue(Normalize(word), out var entry) ? entry.Count : 0;

    // ── Personal style findings ───────────────────────────────────────────────

    /// <summary>
    /// Words in <paramref name="text"/> written through «е» that this user writes through «ё».
    /// </summary>
    /// <remarks>
    /// Per word, not per user: someone who writes «ещё» has said nothing about «твердый». The
    /// preference has to be demonstrated — at least three «ё» spellings and nine in ten of the
    /// total — and known е/ё homographs («все»/«всё») are never touched, because there the two
    /// letters are two words.
    /// </remarks>
    public IReadOnlyList<YoPreference> FindYoPreferences(string text)
    {
        if (_yoIndex.Count == 0 || string.IsNullOrEmpty(text)) return [];

        var protectedSpans = ProtectedTextSpans.Find(text);
        var found = new List<YoPreference>();
        foreach (Match match in TokenRegex().Matches(text))
        {
            var key = Normalize(match.Value);
            if (key.Contains('ё') || !key.Contains('е')) continue;
            if (YoHomographs.Contains(key)) continue;
            if (!_yoIndex.TryGetValue(key, out var yoKey)) continue;
            if (ProtectedTextSpans.Overlaps(match.Index, match.Length, protectedSpans)) continue;

            var yoCount = Words.TryGetValue(yoKey, out var yo) ? yo.Count : 0;
            var eCount = Words.TryGetValue(key, out var e) ? e.Count : 0;
            if (yoCount < 3 || eCount * 9 > yoCount) continue;

            found.Add(new YoPreference(match.Index, match.Length, match.Value, MatchCase(yoKey, match.Value), yoCount, eCount));
        }

        return found;
    }

    // ── Reporting ─────────────────────────────────────────────────────────────

    public PersonalStyleSnapshot Snapshot()
    {
        var c = Counters;
        var yoWords = _yoIndex.Values.Sum(k => Words.TryGetValue(k, out var w) ? w.Count : 0);
        var eWords = _yoIndex.Keys.Where(k => !YoHomographs.Contains(k)).Sum(k => Words.TryGetValue(k, out var w) ? w.Count : 0);
        var quotes = c.Guillemets + (c.StraightQuotes / 2);

        return new PersonalStyleSnapshot(
            c.Words,
            Words.Count,
            c.Sentences,
            c.Sentences == 0 ? 0 : Math.Round((double)c.Words / c.Sentences, 1),
            c.Sentences == 0 ? 0 : Math.Round((double)c.Commas / c.Sentences, 2),
            yoWords + eWords >= 5 ? (double)yoWords / (yoWords + eWords) : null,
            quotes >= 3 ? (double)c.Guillemets / quotes : null,
            c.Sentences == 0 ? 0 : (double)c.Exclamations / c.Sentences,
            c.Sentences == 0 ? 0 : (double)c.Questions / c.Sentences,
            c.CalibrationTexts,
            c.PredictionsAccepted,
            Words
                .Where(p => p.Key.Length >= 4)
                .OrderByDescending(p => p.Value.Count)
                .ThenBy(p => p.Key, StringComparer.Ordinal)
                .Take(40)
                .Select(p => (DisplayForm(p.Key, p.Value), p.Value.Count))
                .ToArray(),
            Unknown
                .Where(p => p.Value.Count >= 3)
                .OrderByDescending(p => p.Value.Count)
                .ThenBy(p => p.Key, StringComparer.Ordinal)
                .Take(50)
                .Select(p => new FrequentUnknownWord(p.Value.Display, p.Value.Count, p.Value.Days))
                .ToArray(),
            Corrections
                .SelectMany(p => p.Value.Select(t => new LearnedCorrection(p.Key, t.Key, t.Value)))
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Typed, StringComparer.Ordinal)
                .Take(50)
                .ToArray());
    }

    // ── Persistence support ───────────────────────────────────────────────────

    internal void RestoreWord(string key, int count, int capitalized, int lastDay)
    {
        if (string.IsNullOrEmpty(key) || count <= 0) return;
        Words[key] = new WordEntry { Count = count, Capitalized = Math.Clamp(capitalized, 0, count), LastDay = lastDay };
        if (key.Contains('ё')) _yoIndex[key.Replace('ё', 'е')] = key;
        _sorted = null;
    }

    internal void RestoreUnknown(string key, int count, int days, int lastDay, string display)
    {
        if (string.IsNullOrEmpty(key) || count <= 0) return;
        Unknown[key] = new UnknownEntry { Count = count, Days = Math.Max(1, days), LastDay = lastDay, Display = string.IsNullOrEmpty(display) ? key : display };
    }

    internal void RestoreNgrams(Dictionary<string, Dictionary<string, int>>? bigrams, Dictionary<string, Dictionary<string, int>>? trigrams)
    {
        _bigramEntries = Copy(bigrams, Bigrams);
        _trigramEntries = Copy(trigrams, Trigrams);

        static int Copy(Dictionary<string, Dictionary<string, int>>? from, Dictionary<string, Dictionary<string, int>> to)
        {
            var entries = 0;
            if (from is null) return 0;
            foreach (var (key, successors) in from)
            {
                if (successors is null) continue;
                var copy = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var (next, count) in successors)
                {
                    if (count > 0) copy[next] = count;
                }

                if (copy.Count == 0) continue;
                to[key] = copy;
                entries += copy.Count;
            }

            return entries;
        }
    }

    internal void RestoreCorrections(Dictionary<string, Dictionary<string, int>>? corrections)
    {
        if (corrections is null) return;
        foreach (var (typed, targets) in corrections)
        {
            if (targets is null) continue;
            foreach (var (intended, count) in targets)
            {
                if (count > 0) Increment(Corrections, typed, intended, count);
            }
        }
    }

    internal void RestoreSentenceHashes(IEnumerable<ulong>? hashes)
    {
        if (hashes is null) return;
        foreach (var hash in hashes) RememberSentence(hash);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Words in reading order: letters, with inner hyphens and apostrophes.</summary>
    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        foreach (Match match in TokenRegex().Matches(text))
        {
            if (match.Length <= 64) tokens.Add(match.Value);
        }

        return tokens;
    }

    internal static string Normalize(string word) => word.ToLowerInvariant().Replace('‑', '-').Replace('’', '\'');

    private static bool IsTerminated(string sentence)
    {
        var trimmed = sentence.TrimEnd();
        if (trimmed.Length == 0) return false;
        if (sentence.EndsWith('\n')) return true;
        return trimmed[^1] is '.' or '!' or '?' or '…' or '»' or '"' or ')';
    }

    /// <summary>
    /// A line that is mostly symbols is source code, a table or a log, and none of those is
    /// how the user writes.
    /// </summary>
    private static bool LooksLikeCode(string sentence)
    {
        var letters = 0;
        var symbols = 0;
        foreach (var character in sentence)
        {
            if (char.IsLetter(character) || char.IsWhiteSpace(character)) letters++;
            else if (character is '{' or '}' or ';' or '=' or '<' or '>' or '[' or ']' or '_' or '\\' or '/' or '|' or '#' or '$') symbols++;
        }

        return symbols >= 3 && symbols * 8 > letters;
    }

    /// <summary>FNV-1a over the sentence with case and whitespace folded. 0 means "nothing to hash".</summary>
    internal static ulong SentenceHash(string sentence)
    {
        const ulong Offset = 14695981039346656037;
        const ulong Prime = 1099511628211;

        var hash = Offset;
        var any = false;
        var space = false;
        foreach (var raw in sentence)
        {
            if (char.IsWhiteSpace(raw))
            {
                space = any;
                continue;
            }

            if (space)
            {
                hash = (hash ^ ' ') * Prime;
                space = false;
            }

            hash = (hash ^ char.ToLowerInvariant(raw)) * Prime;
            any = true;
        }

        return any ? hash : 0;
    }

    private void RememberSentence(ulong hash)
    {
        if (!_sentenceHashes.Add(hash)) return;
        _sentenceOrder.Enqueue(hash);
        while (_sentenceOrder.Count > MaxSentenceHashes)
        {
            _sentenceHashes.Remove(_sentenceOrder.Dequeue());
        }
    }

    private void CountUnknown(string key, string display, int day)
    {
        if (key.Length < 2) return;
        if (!Unknown.TryGetValue(key, out var entry))
        {
            if (Unknown.Count >= MaxUnknown)
            {
                // Make room by dropping the once-seen oldest entries.
                foreach (var stale in Unknown.Where(p => p.Value.Count == 1).OrderBy(p => p.Value.LastDay).Take(MaxUnknown / 10).Select(p => p.Key).ToArray())
                {
                    Unknown.Remove(stale);
                }

                if (Unknown.Count >= MaxUnknown) return;
            }

            entry = new UnknownEntry { Display = display };
            Unknown[key] = entry;
        }

        entry.Count++;
        if (entry.LastDay != day)
        {
            entry.Days++;
            entry.LastDay = day;
        }
    }

    /// <summary>Keeps every table under its cap by dropping the rarest, oldest entries first.</summary>
    private void Prune()
    {
        if (Words.Count > MaxWords)
        {
            var remove = Words.Count - (MaxWords * 9 / 10);
            foreach (var key in Words.OrderBy(p => p.Value.Count).ThenBy(p => p.Value.LastDay).Take(remove).Select(p => p.Key).ToArray())
            {
                Words.Remove(key);
                if (key.Contains('ё')) _yoIndex.Remove(key.Replace('ё', 'е'));
            }

            _sorted = null;
        }

        if (_bigramEntries > MaxNgramEntries) _bigramEntries = PruneNgrams(Bigrams);
        if (_trigramEntries > MaxNgramEntries) _trigramEntries = PruneNgrams(Trigrams);
    }

    private static int PruneNgrams(Dictionary<string, Dictionary<string, int>> table)
    {
        // Singletons go first; they are most of any n-gram table and almost none of its signal.
        var threshold = 1;
        int entries;
        do
        {
            entries = 0;
            foreach (var key in table.Keys.ToArray())
            {
                var successors = table[key];
                foreach (var next in successors.Where(p => p.Value <= threshold).Select(p => p.Key).ToArray())
                {
                    successors.Remove(next);
                }

                if (successors.Count == 0) table.Remove(key);
                else entries += successors.Count;
            }

            threshold++;
        }
        while (entries > MaxNgramEntries * 9 / 10);

        return entries;
    }

    /// <returns>True when a new successor entry was created.</returns>
    private static bool Increment(Dictionary<string, Dictionary<string, int>> table, string key, string next, int by = 1)
    {
        if (!table.TryGetValue(key, out var successors))
        {
            successors = new Dictionary<string, int>(StringComparer.Ordinal);
            table[key] = successors;
        }

        successors.TryGetValue(next, out var count);
        successors[next] = count + by;
        return count == 0;
    }

    private static int IndexOf(IReadOnlyList<string> list, string word)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(Normalize(list[i]), word, StringComparison.Ordinal)) return i;
        }

        return -1;
    }

    private static IReadOnlyList<string> MoveToFront(IReadOnlyList<string> list, int index)
    {
        if (index == 0) return list;
        var result = new List<string>(list.Count) { list[index] };
        for (var i = 0; i < list.Count; i++)
        {
            if (i != index) result.Add(list[i]);
        }

        return result;
    }

    /// <summary><paramref name="word"/> in the casing of <paramref name="model"/>.</summary>
    internal static string MatchCase(string word, string model)
    {
        if (model.Length == 0 || word.Length == 0) return word;
        if (model.Length >= 2 && IsAllUpper(model)) return word.ToUpperInvariant();
        return char.IsUpper(model[0]) ? Capitalize(word) : word;
    }

    private static string Capitalize(string word)
        => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    private static bool IsAllUpper(string value)
    {
        var letters = 0;
        foreach (var character in value)
        {
            if (!char.IsLetter(character)) continue;
            if (!char.IsUpper(character)) return false;
            letters++;
        }

        return letters > 0;
    }

    [GeneratedRegex(@"\p{L}+(?:[-‑'’]\p{L}+)*", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
