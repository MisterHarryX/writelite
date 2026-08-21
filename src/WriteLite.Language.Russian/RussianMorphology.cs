using System.Collections.Concurrent;

namespace WriteLite.Language.Russian;

/// <summary>
/// Grammatical analysis and inflection of Russian surface forms, over the form index.
/// </summary>
/// <remarks>
/// <para><b>The problem this type exists to solve.</b> The form index stores one grammatical
/// analysis per spelling, because that is what its source data provides. Russian spellings are
/// massively ambiguous: «новым» is a masculine instrumental singular <em>and</em> a dative
/// plural, «инструкции» is genitive, dative, prepositional and nominative plural at once.
/// A rule that asks the index "what case is this" gets one of those answers and rewrites the
/// other three. That failure has been observed in this repository before, and it is the reason
/// the agreement rules below never consult a stored tag on its own.</para>
///
/// <para><b>What is trusted from where.</b> Lemma, part of speech, gender and animacy are
/// <em>lexical</em> properties: they do not vary across a paradigm, so the collapsed analysis
/// reports them correctly and they are taken from the index. Case and number vary, so they are
/// derived by generating the paradigm from the lemma and keeping every slot whose spelling is
/// the word in front of us. The two are then unioned, so the stored reading can add to the
/// derived set but never replace it.</para>
///
/// <para><b>Silence is the failure mode.</b> A word this type cannot analyse comes back with
/// an empty reading set, and every consumer treats an empty set as "no opinion". Indeclinables,
/// borrowings, abbreviations, surnames and genuinely irregular paradigms therefore produce no
/// findings at all rather than wrong ones. §2 of the sprint brief — a missed correction is
/// preferable to a destructive one — is implemented here, once, instead of in each rule.</para>
///
/// <para>Analyses are memoised per instance. The paradigm generator allocates a few dozen
/// short strings per call and the same handful of function words recur constantly in running
/// text; the cache is bounded and drops itself wholesale rather than tracking ages, because
/// the contents are pure functions of the index and never go stale.</para>
/// </remarks>
public sealed class RussianMorphology
{
    private readonly RussianFormIndex _index;
    private readonly ConcurrentDictionary<string, RuAnalysis> _nominals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RuVerbForm?> _verbs = new(StringComparer.Ordinal);

    /// <summary>Entries kept before the cache is dropped.</summary>
    /// <remarks>
    /// Enough for the vocabulary of a long document — measured, a 50 000-word Russian text has
    /// roughly 12 000 distinct forms. Dropping the whole map rather than evicting entries keeps
    /// the hot path a plain lookup with no bookkeeping, and the cost of a drop is recomputing
    /// analyses that are individually microseconds.
    /// </remarks>
    private const int CacheLimit = 40_000;

    public RussianMorphology(RussianFormIndex index)
        => _index = index ?? throw new ArgumentNullException(nameof(index));

    public static RussianMorphology? TryCreate(RussianFormIndex? index)
        => index is null ? null : new RussianMorphology(index);

    public RussianFormIndex Index => _index;

    /// <summary>True when the index knows this spelling.</summary>
    public bool Knows(string word) => _index.Contains(Normalise(word));

    /// <summary>The recorded part of speech, or <see cref="RussianPartOfSpeech.Unknown"/>.</summary>
    public RussianPartOfSpeech PartOfSpeech(string word)
    {
        var lower = Normalise(word);
        if (RussianPronounParadigm.LemmaOf(lower) is not null) return RussianPartOfSpeech.Pronoun;
        return _index.GetInfo(lower).PartOfSpeech;
    }

    public string Lemma(string word) => _index.GetInfo(Normalise(word)).Lemma ?? string.Empty;

    /// <summary>
    /// True when this form can stand before a noun and agree with it.
    /// </summary>
    /// <remarks>
    /// Membership is decided by the ending together with the recorded lemma, not by the
    /// recorded part of speech alone. A substantivised adjective is recorded as a noun —
    /// «новым» belongs to the lemma «новое» — and excluding it on that basis would exempt one
    /// of the commonest agreement errors in the language from the rule that catches it.
    /// </remarks>
    public bool IsModifier(string word)
    {
        var lower = Normalise(word);
        if (lower.Length == 0) return false;
        if (RussianPronounParadigm.LemmaOf(lower) is not null) return true;

        var info = _index.GetInfo(lower);
        if (info.PartOfSpeech is RussianPartOfSpeech.AdjectiveFull
            or RussianPartOfSpeech.ParticipleFull
            or RussianPartOfSpeech.Numeral)
        {
            return RussianAdjectivalParadigm.TrySplit(lower, out _, out _, out _);
        }

        // Personal pronouns are recorded as Pronoun and adjectival ones as AdjectiveFull, so
        // this branch would only ever admit «меня», «него», «тебя» — words that never modify a
        // noun. Admitting them let the government rule walk past «у меня» to the next noun and
        // report «у меня телефон» as a case error.
        return info.PartOfSpeech == RussianPartOfSpeech.Noun
               && IsAdjectivalLemma(info.Lemma ?? string.Empty)
               && RussianAdjectivalParadigm.TrySplit(lower, out _, out _, out _);
    }

    /// <summary>True when this form is a common noun the paradigm engine could analyse.</summary>
    public bool IsNoun(string word)
    {
        var lower = Normalise(word);
        if (RussianPronounParadigm.LemmaOf(lower) is not null) return false;
        return _index.GetInfo(lower).PartOfSpeech == RussianPartOfSpeech.Noun;
    }

    /// <summary>
    /// True when the index records this verb as intransitive.
    /// </summary>
    /// <remarks>
    /// Transitivity settles a question ambiguous spelling cannot: neuter and masculine
    /// inanimate nouns spell the accusative like the nominative, so «и выглянуло солнце» looks
    /// as though «солнце» could be an object. «Выглянуть» takes no object, so it cannot be —
    /// and the clause therefore has its own subject and its own comma.
    /// </remarks>
    public bool IsIntransitiveVerb(string word)
    {
        var info = _index.GetInfo(Normalise(word));
        if (info.PartOfSpeech is not (RussianPartOfSpeech.Verb or RussianPartOfSpeech.Infinitive))
        {
            return false;
        }

        return Tag(info).Contains("intr", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when some noun paradigm in the index explains this spelling.
    /// </summary>
    /// <remarks>
    /// Asked of a word the index has labelled something else. «покоя» is filed as a gerund of
    /// «покоить» and is, in every sentence anyone writes, the genitive of «покой» — so
    /// «не дал покоя один вопрос» was read as a gerund phrase. Running the candidate lemmas
    /// without regard to the recorded part of speech answers the question the label cannot.
    /// </remarks>
    public bool HasNounReading(string word)
    {
        var lower = Normalise(word);
        if (lower.Length < 3) return false;

        foreach (var candidate in CandidateLemmas(lower))
        {
            if (string.Equals(candidate, lower, StringComparison.Ordinal)) continue;
            if (!_index.Contains(candidate)) continue;

            var info = _index.GetInfo(candidate);
            if (info.PartOfSpeech != RussianPartOfSpeech.Noun) continue;
            if (!IsAttestedLemma(candidate, info)) continue;

            var gender = ShapeGender(candidate, GenderFromTag(Tag(info)));
            var animate = Tag(info).Contains("anim", StringComparison.Ordinal)
                          && !Tag(info).Contains("inan", StringComparison.Ordinal);
            if (RussianNounParadigm.Analyse(candidate, lower, gender, animate).Count > 0) return true;
        }

        return false;
    }

    /// <summary>True when the index marks this adjective as a superlative.</summary>
    public bool IsSuperlative(string word)
    {
        var info = _index.GetInfo(Normalise(word));
        return info.PartOfSpeech == RussianPartOfSpeech.AdjectiveFull
               && Tag(info).Contains("Supr", StringComparison.Ordinal);
    }

    /// <summary>True when the index marks this form as a proper name.</summary>
    public bool IsProperName(string word)
        => (_index.GetInfo(Normalise(word)).Flags & RussianFormFlags.ProperName) != 0;

    // ---- nominals ------------------------------------------------------------

    /// <summary>
    /// Every grammatical slot a noun, adjective, participle or determiner can be filling.
    /// </summary>
    public RuAnalysis Nominal(string word)
    {
        var lower = Normalise(word);
        if (lower.Length == 0) return RuAnalysis.Unknown;

        if (_nominals.TryGetValue(lower, out var cached)) return cached;

        var analysis = ComputeNominal(lower);

        if (_nominals.Count >= CacheLimit) _nominals.Clear();
        _nominals[lower] = analysis;
        return analysis;
    }

    private RuAnalysis ComputeNominal(string lower)
    {
        var pronoun = RussianPronounParadigm.Analyse(lower);
        if (pronoun.Count > 0)
        {
            return new RuAnalysis(pronoun, RuGender.None, false, true);
        }

        var info = _index.GetInfo(lower);
        if (info.PartOfSpeech == RussianPartOfSpeech.Unknown && !_index.Contains(lower))
        {
            return RuAnalysis.Unknown;
        }

        var tagged = FromTag(Tag(info));

        switch (info.PartOfSpeech)
        {
            case RussianPartOfSpeech.Noun:
            {
                var gender = GenderFromTag(Tag(info));
                var animate = Tag(info).Contains("anim", StringComparison.Ordinal)
                              && !Tag(info).Contains("inan", StringComparison.Ordinal);
                var lemma = info.Lemma is { Length: > 0 } ? info.Lemma : lower;

                // Same reason as in Inflect: an adjectival lemma has no noun paradigm, and
                // running one over it invents slots. «новое» declined as a noun claims «новое»
                // is its own prepositional singular.
                var derived = IsAdjectivalLemma(lemma ?? string.Empty)
                    ? []
                    : RussianNounParadigm.Analyse(lemma, lower, gender, animate);

                // Homonymy across lemmas, not only within one. The index records «воду» as the
                // dative of a masculine «вод», so the accusative of «вода» — the reading every
                // sentence containing it actually uses — is absent, and «в воду» looks like a
                // government error. Reconstructing the plausible nominatives of the surface
                // form and running each one that exists as a noun recovers those readings.
                foreach (var alternative in CandidateLemmas(lower))
                {
                    if (string.Equals(alternative, lemma, StringComparison.Ordinal)) continue;
                    var other = _index.GetInfo(alternative);
                    if (other.PartOfSpeech != RussianPartOfSpeech.Noun) continue;

                    // The candidate has to be a lemma, not merely a spelling that exists.
                    // Without this the surface form is always its own candidate — every
                    // spelling is in the index — so every oblique noun gained a spurious
                    // nominative and «задержали несколько рейсов» found «рейсов» as a singular
                    // subject. Testing the recorded tag for "nomn" does not work either: the
                    // index files «логика» as the genitive of «логик», so the lemma a reader
                    // means is the one that fails that test.
                    if (!IsAttestedLemma(alternative, other)) continue;

                    // Proper names are not expanded. Their homonyms are the accidents of a
                    // naming system — «Германия» the country against «германий» the element,
                    // «Роман» the name against «роман» the novel — and admitting those readings
                    // exempts precisely the words a writer is most likely to leave in the
                    // nominative. Common nouns are expanded freely, which is what recovers the
                    // accusative of «вода» for a «воду» the index files under «вод».
                    if ((info.Flags & RussianFormFlags.ProperName) != 0) break;
                    if ((other.Flags & RussianFormFlags.ProperName) != 0) continue;

                    // Gender from the candidate lemma's shape, not from its recorded tag. The
                    // index files «логика» as the genitive of the masculine «логик», so its tag
                    // says masculine — and the accusative «логику» recovered through it came
                    // back masculine too, leaving «эту логику» still a gender clash. A lemma's
                    // ending is what its gender follows.
                    var otherGender = ShapeGender(alternative, GenderFromTag(Tag(other)));
                    var otherAnimate = Tag(other).Contains("anim", StringComparison.Ordinal)
                                       && !Tag(other).Contains("inan", StringComparison.Ordinal);
                    var extra = RussianNounParadigm.Analyse(alternative, lower, otherGender, otherAnimate);
                    if (extra.Count > 0) derived = Union(derived, extra, RuGender.None);
                }

                var readings = Union(derived, tagged, gender);

                // A form that is spelt like an adjective usually is one, whatever the index
                // filed it under. «новым» is recorded as a noun of the substantivised «новое»;
                // «другом» is recorded as the instrumental of «друг», which leaves the
                // prepositional of «другой» — the reading «в другом городе» uses — missing
                // altogether, and the government rule reported correct text. Adjectival slots
                // are added whenever the stem has an adjectival nominative in the index, so the
                // evidence is lexical rather than a guess from the ending alone.
                if (RussianAdjectivalParadigm.TrySplit(lower, out var advStem, out _, out var slots)
                    && (IsAdjectivalLemma(lemma) || HasAdjectivalNominative(advStem)))
                {
                    readings = Union(readings, slots, RuGender.None);
                }

                return new RuAnalysis(readings, gender, animate, true);
            }

            case RussianPartOfSpeech.AdjectiveFull:
            case RussianPartOfSpeech.ParticipleFull:
            case RussianPartOfSpeech.Pronoun:
            case RussianPartOfSpeech.Numeral:
            {
                if (!RussianAdjectivalParadigm.TrySplit(lower, out _, out _, out var slots))
                {
                    return new RuAnalysis(tagged, RuGender.None, false, true);
                }

                return new RuAnalysis(Union(slots, tagged, RuGender.None), RuGender.None, false, true);
            }

            default:
                return new RuAnalysis(tagged, RuGender.None, false, true);
        }
    }

    /// <summary>
    /// The union of the derived slots and the index's own reading.
    /// </summary>
    /// <remarks>
    /// Unioning rather than choosing is deliberate. The generator is incomplete for irregular
    /// paradigms and the index is incomplete for ambiguous spellings, and the two are
    /// incomplete in different places. Adding a reading can only ever make two words agree that
    /// would otherwise have been reported — it costs recall and buys precision, which is the
    /// trade this project takes every time.
    /// </remarks>
    private static IReadOnlyList<RuReading> Union(
        IReadOnlyList<RuReading> derived,
        IReadOnlyList<RuReading> tagged,
        RuGender nounGender)
    {
        if (tagged.Count == 0) return derived;
        if (derived.Count == 0) return tagged;

        var result = new List<RuReading>(derived.Count + tagged.Count);
        result.AddRange(derived);
        foreach (var reading in tagged)
        {
            var normalised = nounGender != RuGender.None && reading.Number == RuNumber.Sing
                ? reading with { Gender = nounGender }
                : reading;
            if (!result.Contains(normalised)) result.Add(normalised);
        }

        return result;
    }

    // ---- verbs ---------------------------------------------------------------

    /// <summary>
    /// The agreement features of a verb form, or null when the word is not a verb.
    /// </summary>
    /// <remarks>
    /// Read from the ending rather than from the tag, for the same reason nominals are:
    /// «сдали» is unambiguous, but «любит» and «любите» differ by one letter and the index
    /// records one analysis for a spelling. Russian finite endings are a closed set of
    /// fourteen, they are not shared with any other part of speech in the positions checked
    /// here, and the part of speech itself still comes from the index.
    /// </remarks>
    public RuVerbForm? Verb(string word)
    {
        var lower = Normalise(word);
        if (lower.Length < 2) return null;

        if (_verbs.TryGetValue(lower, out var cached)) return cached;

        var computed = ComputeVerb(lower);
        if (_verbs.Count >= CacheLimit) _verbs.Clear();
        _verbs[lower] = computed;
        return computed;
    }

    private RuVerbForm? ComputeVerb(string lower)
    {
        // A determiner is never a predicate. «моём» is recorded as the first-person plural of
        // «мыть», which is true of the spelling and false of every sentence it occurs in.
        if (RussianPronounParadigm.LemmaOf(lower) is not null) return null;

        var info = _index.GetInfo(lower);
        var pos = info.PartOfSpeech;

        // GetInfo returns a default FormInfo for anything the metadata file does not cover,
        // and a default record struct carries a null Tag. Every tag test in this type goes
        // through Tag(), so an index deployed without metadata degrades to "no features"
        // rather than to a NullReferenceException on the analysis path.
        // An imperative has no subject to agree with: «В графе адресант укажите получателя»
        // was read as a second-person plural disagreeing with the singular «адресант».
        if (Tag(info).Contains("impr", StringComparison.Ordinal)) return null;

        // Short adjectives are admitted alongside verbs, because for the only question asked
        // here — does the predicate agree with its subject — they behave identically, and the
        // index collapses many past-tense verbs onto a homonymous short adjective («побежал»
        // is recorded as «побежалый»). Admitting them costs nothing: a short adjective marks
        // gender and number exactly as a past tense does, so a mismatch is a mismatch under
        // either reading and the correction is the same suffix swap.
        var predicative = pos == RussianPartOfSpeech.AdjectiveShort
                          || pos == RussianPartOfSpeech.ParticipleShort;
        if (pos is not (RussianPartOfSpeech.Verb or RussianPartOfSpeech.Infinitive) && !predicative)
        {
            return null;
        }

        if (predicative && !EndsPastLike(lower)) return null;

        if (pos == RussianPartOfSpeech.Infinitive
            || lower.EndsWith("ть", StringComparison.Ordinal)
            || lower.EndsWith("ться", StringComparison.Ordinal)
            || lower.EndsWith("чь", StringComparison.Ordinal)
            || lower.EndsWith("ти", StringComparison.Ordinal))
        {
            return new RuVerbForm(RuNumber.Sing, RuGender.None, RuPerson.None, false, true);
        }

        // Past tense: the -л suffix, optionally reflexive.
        var core = lower.EndsWith("сь", StringComparison.Ordinal) || lower.EndsWith("ся", StringComparison.Ordinal)
            ? lower[..^2]
            : lower;

        if (core.EndsWith("л", StringComparison.Ordinal))
            return new RuVerbForm(RuNumber.Sing, RuGender.Masc, RuPerson.None, true, false);
        if (core.EndsWith("ла", StringComparison.Ordinal))
            return new RuVerbForm(RuNumber.Sing, RuGender.Fem, RuPerson.None, true, false);
        if (core.EndsWith("ло", StringComparison.Ordinal))
            return new RuVerbForm(RuNumber.Sing, RuGender.Neut, RuPerson.None, true, false);
        if (core.EndsWith("ли", StringComparison.Ordinal))
            return new RuVerbForm(RuNumber.Plur, RuGender.None, RuPerson.None, true, false);

        // Present and simple future.
        foreach (var (ending, number, person) in FiniteEndings)
        {
            if (!core.EndsWith(ending, StringComparison.Ordinal)) continue;
            if (core.Length <= ending.Length) continue;
            return new RuVerbForm(number, RuGender.None, person, false, false);
        }

        return null;
    }

    /// <summary>Finite present/future endings, longest first.</summary>
    private static readonly (string Ending, RuNumber Number, RuPerson Person)[] FiniteEndings =
    [
        ("ете", RuNumber.Plur, RuPerson.Second),
        ("ите", RuNumber.Plur, RuPerson.Second),
        ("ешь", RuNumber.Sing, RuPerson.Second),
        ("ишь", RuNumber.Sing, RuPerson.Second),
        ("ём", RuNumber.Plur, RuPerson.First),
        ("ем", RuNumber.Plur, RuPerson.First),
        ("им", RuNumber.Plur, RuPerson.First),
        ("ют", RuNumber.Plur, RuPerson.Third),
        ("ут", RuNumber.Plur, RuPerson.Third),
        ("ят", RuNumber.Plur, RuPerson.Third),
        ("ат", RuNumber.Plur, RuPerson.Third),
        ("ёт", RuNumber.Sing, RuPerson.Third),
        ("ет", RuNumber.Sing, RuPerson.Third),
        ("ит", RuNumber.Sing, RuPerson.Third),
        ("ю", RuNumber.Sing, RuPerson.First),
        ("у", RuNumber.Sing, RuPerson.First),
    ];

    // ---- inflection ----------------------------------------------------------

    /// <summary>
    /// <paramref name="word"/> re-inflected into <paramref name="target"/>, or null.
    /// </summary>
    /// <remarks>
    /// Every candidate the paradigm tables produce is checked against the index before it is
    /// returned, so a correction is always a word that exists. Null is returned whenever that
    /// check leaves nothing, which is how an irregular paradigm turns into a detection with no
    /// replacement instead of into an invented word.
    /// </remarks>
    public string? Inflect(string word, RuReading target)
    {
        var lower = Normalise(word);
        if (lower.Length == 0) return null;

        var pronounLemma = RussianPronounParadigm.LemmaOf(lower);
        if (pronounLemma is not null)
        {
            var form = RussianPronounParadigm.Inflect(pronounLemma, target);
            return Same(form, lower) ? null : form;
        }

        var info = _index.GetInfo(lower);

        // A substantivised adjective is recorded as a noun but declines adjectivally, and
        // declining it as a noun produces real-looking nonsense: the noun stem of «новое» is
        // «ново», whose prepositional singular is «новое» itself, so «в новым доме» was
        // corrected to «в новое доме». Adjectival inflection is chosen by the shape of the
        // word rather than by the part of speech recorded for it.
        if (info.PartOfSpeech == RussianPartOfSpeech.Noun && IsAdjectivalLemma(info.Lemma ?? string.Empty))
        {
            return InflectAdjectivally(lower, info, target);
        }

        switch (info.PartOfSpeech)
        {
            case RussianPartOfSpeech.Noun:
            {
                var gender = GenderFromTag(Tag(info));
                var animate = Tag(info).Contains("anim", StringComparison.Ordinal)
                              && !Tag(info).Contains("inan", StringComparison.Ordinal);
                var lemma = info.Lemma is { Length: > 0 } ? info.Lemma : lower;
                foreach (var candidate in RussianNounParadigm.Generate(
                             lemma, target.Case, target.Number, gender, animate))
                {
                    if (!_index.Contains(candidate)) continue;
                    if (Same(candidate, lower)) continue;

                    // The generated form must actually be the same word: a short stem can
                    // collide with an unrelated lexeme, and offering one would be a silent
                    // meaning change of exactly the kind SemanticEditGuard exists to stop.
                    var check = _index.GetInfo(candidate);
                    if (check.PartOfSpeech != RussianPartOfSpeech.Noun) continue;
                    if (!string.Equals(check.Lemma ?? string.Empty, info.Lemma ?? string.Empty, StringComparison.OrdinalIgnoreCase)) continue;
                    return Canonical(candidate);
                }

                return null;
            }

            case RussianPartOfSpeech.AdjectiveFull:
            case RussianPartOfSpeech.ParticipleFull:
            case RussianPartOfSpeech.Pronoun:
            case RussianPartOfSpeech.Numeral:
                return InflectAdjectivally(lower, info, target);

            default:
                return null;
        }
    }

    private string? InflectAdjectivally(string lower, RussianFormIndex.FormInfo info, RuReading target)
    {
        if (!RussianAdjectivalParadigm.TrySplit(lower, out var stem, out var soft, out _)) return null;

        foreach (var candidate in RussianAdjectivalParadigm.Generate(stem, soft, target))
        {
            if (!_index.Contains(candidate)) continue;
            if (Same(candidate, lower)) continue;

            var check = _index.GetInfo(candidate);

            // The lemma has to match, but the part of speech need not: the index files
            // «новым» under a noun lemma and «новом» under the same lemma, while «новый» and
            // «новая» are adjectives — one paradigm split across two labels. Requiring the
            // labels to agree lost exactly the substantivised forms this branch exists for.
            if (info.Lemma is { Length: > 0 }
                && check.Lemma is { Length: > 0 }
                && !string.Equals(check.Lemma, info.Lemma, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (check.PartOfSpeech is not (RussianPartOfSpeech.AdjectiveFull
                or RussianPartOfSpeech.ParticipleFull
                or RussianPartOfSpeech.Pronoun
                or RussianPartOfSpeech.Numeral
                or RussianPartOfSpeech.Noun))
            {
                continue;
            }

            return Canonical(candidate);
        }

        return null;
    }

    /// <summary>A past-tense verb re-inflected for a subject's number and gender.</summary>
    /// <remarks>
    /// The past tense is formed from one stem plus «-л/-ла/-ло/-ли», so this is a suffix swap
    /// rather than a paradigm lookup — and it is still gated on the index, because «побежала»
    /// existing is what confirms «побежал» was a past-tense form and not a noun.
    /// </remarks>
    public string? InflectPast(string word, RuNumber number, RuGender gender)
    {
        var lower = Normalise(word);
        var reflexive = string.Empty;
        var core = lower;
        if (core.EndsWith("ся", StringComparison.Ordinal) || core.EndsWith("сь", StringComparison.Ordinal))
        {
            reflexive = core[^2..];
            core = core[..^2];
        }

        var stem = core switch
        {
            _ when core.EndsWith("ла", StringComparison.Ordinal) => core[..^2],
            _ when core.EndsWith("ло", StringComparison.Ordinal) => core[..^2],
            _ when core.EndsWith("ли", StringComparison.Ordinal) => core[..^2],
            _ when core.EndsWith("л", StringComparison.Ordinal) => core[..^1],
            _ => null,
        };

        if (stem is null || stem.Length == 0) return null;

        // The past tense is stem + «л» + an agreement vowel, so the vowel alone varies. Writing
        // the whole suffix here produced «сдалли» from «сдал».
        var suffix = number == RuNumber.Plur
            ? "и"
            : gender switch
            {
                RuGender.Fem => "а",
                RuGender.Neut => "о",
                _ => string.Empty,
            };

        var candidate = stem + "л" + suffix;
        if (reflexive.Length > 0)
        {
            // «-ся» after a consonant, «-сь» after a vowel.
            candidate += suffix.Length == 0 ? "ся" : "сь";
        }

        if (Same(candidate, lower)) return null;
        return _index.Contains(candidate) ? Canonical(candidate) : null;
    }

    /// <summary>A finite present/future verb re-inflected for a subject's number.</summary>
    public string? InflectFinite(string word, RuNumber number, RuPerson person)
    {
        var lower = Normalise(word);
        var reflexive = string.Empty;
        var core = lower;
        if (core.EndsWith("ся", StringComparison.Ordinal) || core.EndsWith("сь", StringComparison.Ordinal))
        {
            reflexive = "ся";
            core = core[..^2];
        }

        string? stem = null;
        foreach (var (ending, _, _) in FiniteEndings)
        {
            if (!core.EndsWith(ending, StringComparison.Ordinal)) continue;
            if (core.Length <= ending.Length) continue;
            stem = core[..^ending.Length];
            break;
        }

        if (stem is null) return null;

        foreach (var (ending, endingNumber, endingPerson) in FiniteEndings)
        {
            if (endingNumber != number) continue;
            if (person != RuPerson.None && endingPerson != person) continue;

            var candidate = stem + ending + reflexive;
            if (Same(candidate, lower)) continue;
            if (!_index.Contains(candidate)) continue;

            var check = _index.GetInfo(candidate);
            if (check.PartOfSpeech != RussianPartOfSpeech.Verb) continue;
            return Canonical(candidate);
        }

        return null;
    }

    // ---- helpers -------------------------------------------------------------

    private static string Normalise(string word)
        => string.IsNullOrEmpty(word) ? string.Empty : word.ToLowerInvariant();

    /// <summary>
    /// The spelling the index actually stores for a form, recovering «ё».
    /// </summary>
    /// <remarks>
    /// <para><b>Why every generated form has to go through this.</b> The index folds «ё» to
    /// «е» when matching, so <c>Contains("планируёт")</c> is true — it matches the stored
    /// «планирует». A generator that trusts <c>Contains</c> therefore emits whichever of the
    /// two spellings it happened to try first, and «планируют» was being corrected to
    /// «планируёт» on grammatically correct text.</para>
    ///
    /// <para>Which vowel is right is a fact about stress, which the metadata does not record;
    /// but the graph stores the real spelling, and a zero-distance search returns it. One
    /// lookup per correction, only on the correction path.</para>
    /// </remarks>
    public string Canonical(string word)
    {
        if (word.Length == 0) return word;
        if (word.IndexOf('ё') < 0 && word.IndexOf('е') < 0) return word;

        foreach (var match in _index.FindWithin(word, 0, 4))
        {
            if (match.Distance == 0) return match.Word;
        }

        return word;
    }

    private static bool Same(string? a, string? b)
    {
        if (a is null || b is null) return false;
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            var x = a[i] == 'ё' ? 'е' : a[i];
            var y = b[i] == 'ё' ? 'е' : b[i];
            if (x != y) return false;
        }

        return true;
    }

    /// <summary>
    /// Nominative singulars this surface form could belong to, for cross-lemma disambiguation.
    /// </summary>
    /// <remarks>
    /// A generate-and-filter list rather than a reverse index: the caller discards everything
    /// the index does not hold as a noun, so over-proposing costs a DAFSA lookup each and
    /// under-proposing costs a reading. The endings are the ones that distinguish the three
    /// declensions plus the two soft variants; anything more exotic is reached through the
    /// recorded lemma, which is always tried first.
    /// </remarks>
    private static IEnumerable<string> CandidateLemmas(string surface)
    {
        yield return surface;
        if (surface.Length < 3) yield break;

        var one = surface[..^1];
        foreach (var suffix in LemmaSuffixes) yield return one + suffix;

        var two = surface[..^2];
        if (two.Length >= 2)
        {
            foreach (var suffix in LemmaSuffixes) yield return two + suffix;
        }

        var three = surface.Length >= 5 ? surface[..^3] : null;
        if (three is { Length: >= 2 })
        {
            foreach (var suffix in LemmaSuffixes) yield return three + suffix;
        }
    }

    private static readonly string[] LemmaSuffixes = ["", "а", "я", "о", "е", "ь", "й"];

    /// <summary>
    /// True when some form in the index names <paramref name="candidate"/> as its lemma.
    /// </summary>
    /// <remarks>
    /// The index records one analysis per spelling, so a lemma is not recognisable from its own
    /// entry — «логика» is filed as the genitive of «логик» and «политика» as the genitive of
    /// «политик». What identifies a lemma is that other forms point at it, so the test declines
    /// the candidate into three slots and asks whether any of the results does. A word that is
    /// not a lemma produces forms that do not exist («рейсову», «рейсовом») and fails.
    /// </remarks>
    private bool IsAttestedLemma(string candidate, RussianFormIndex.FormInfo info)
    {
        if (string.Equals(info.Lemma, candidate, StringComparison.OrdinalIgnoreCase)) return true;

        var gender = GenderFromTag(Tag(info));
        foreach (var (kase, number) in LemmaProbeSlots)
        {
            foreach (var form in RussianNounParadigm.Generate(candidate, kase, number, gender, false))
            {
                if (!_index.Contains(form)) continue;
                if (string.Equals(_index.GetInfo(form).Lemma, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Gender implied by a lemma's ending, falling back to the recorded value.</summary>
    /// <remarks>
    /// «-а/-я» is feminine and «-о/-е» neuter with very few exceptions, and a consonant ending
    /// is masculine. «-ь» is genuinely both, so there the recorded tag is kept.
    /// </remarks>
    private static RuGender ShapeGender(string lemma, RuGender recorded)
    {
        if (lemma.Length == 0) return recorded;
        return lemma[^1] switch
        {
            'а' or 'я' => RuGender.Fem,
            'о' or 'е' or 'ё' => RuGender.Neut,
            'ь' => recorded,
            _ => RuGender.Masc,
        };
    }

    /// <summary>Slots whose spellings are distinctive enough to identify a paradigm.</summary>
    private static readonly (RuCase Case, RuNumber Number)[] LemmaProbeSlots =
    [
        (RuCase.Dat, RuNumber.Sing), (RuCase.Ins, RuNumber.Sing), (RuCase.Loc, RuNumber.Plur),
    ];

    /// <summary>True when the stem has an adjectival nominative singular in the index.</summary>
    private bool HasAdjectivalNominative(string stem)
    {
        if (stem.Length < 2) return false;
        foreach (var ending in AdjectivalNominatives)
        {
            var candidate = stem + ending;
            if (!_index.Contains(candidate)) continue;
            var info = _index.GetInfo(candidate);
            if (info.PartOfSpeech is RussianPartOfSpeech.AdjectiveFull
                or RussianPartOfSpeech.ParticipleFull
                or RussianPartOfSpeech.Numeral)
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] AdjectivalNominatives = ["ый", "ий", "ой"];

    /// <summary>
    /// True when a form is ambiguous with a synthetic comparative and must not be re-inflected.
    /// </summary>
    /// <remarks>
    /// «позднее», «тише», «выше», «дальше» are comparatives, which do not decline; the index
    /// records «позднее» as the neuter of «поздний», so the agreement rule read «не позднее
    /// пятницы» as a modifier disagreeing with a genitive noun and offered «поздней». Both
    /// readings are real, and a form that is genuinely two things is one the rules stay quiet
    /// about. Only the two endings that create the ambiguity are excluded; «-ей», which is also
    /// the whole oblique feminine singular, is not.
    /// </remarks>
    public static bool IsComparativeShaped(string word)
    {
        var lower = word.ToLowerInvariant();
        return lower.Length > 3
               && (lower.EndsWith("ее", StringComparison.Ordinal)
                   || lower.EndsWith("ше", StringComparison.Ordinal)
                   || lower.EndsWith("же", StringComparison.Ordinal));
    }

    /// <summary>True when a lemma is itself an adjectival form — a substantivised adjective.</summary>
    private static bool IsAdjectivalLemma(string lemma)
        => lemma.EndsWith("ый", StringComparison.Ordinal)
           || lemma.EndsWith("ий", StringComparison.Ordinal)
           || lemma.EndsWith("ой", StringComparison.Ordinal)
           || lemma.EndsWith("ая", StringComparison.Ordinal)
           || lemma.EndsWith("яя", StringComparison.Ordinal)
           || lemma.EndsWith("ое", StringComparison.Ordinal)
           || lemma.EndsWith("ее", StringComparison.Ordinal);

    /// <summary>True when a form carries a past-tense/short-adjective agreement suffix.</summary>
    private static bool EndsPastLike(string word)
    {
        var core = word.EndsWith("ся", StringComparison.Ordinal) || word.EndsWith("сь", StringComparison.Ordinal)
            ? word[..^2]
            : word;
        return core.EndsWith("л", StringComparison.Ordinal)
               || core.EndsWith("ла", StringComparison.Ordinal)
               || core.EndsWith("ло", StringComparison.Ordinal)
               || core.EndsWith("ли", StringComparison.Ordinal);
    }

    /// <summary>The metadata tag, or the empty string when the index carries no metadata.</summary>
    private static string Tag(RussianFormIndex.FormInfo info) => info.Tag ?? string.Empty;

    internal static RuGender GenderFromTag(string tag)
    {
        if (tag.Contains("masc", StringComparison.Ordinal)) return RuGender.Masc;
        if (tag.Contains("femn", StringComparison.Ordinal)) return RuGender.Fem;
        if (tag.Contains("neut", StringComparison.Ordinal)) return RuGender.Neut;
        return RuGender.None;
    }

    /// <summary>The single reading the index recorded, as a one-element set.</summary>
    private static IReadOnlyList<RuReading> FromTag(string tag)
    {
        if (tag.Length == 0) return [];

        RuCase? kase = null;
        if (tag.Contains("nomn", StringComparison.Ordinal)) kase = RuCase.Nom;
        else if (tag.Contains("gent", StringComparison.Ordinal)) kase = RuCase.Gen;
        else if (tag.Contains("datv", StringComparison.Ordinal)) kase = RuCase.Dat;
        else if (tag.Contains("accs", StringComparison.Ordinal)) kase = RuCase.Acc;
        else if (tag.Contains("ablt", StringComparison.Ordinal)) kase = RuCase.Ins;
        else if (tag.Contains("loct", StringComparison.Ordinal)) kase = RuCase.Loc;

        // «gen2», «loc2» and «acc2» are the second genitive/locative/accusative — real slots
        // with the same agreement behaviour as their primaries.
        if (kase is null)
        {
            if (tag.Contains("gen1", StringComparison.Ordinal) || tag.Contains("gen2", StringComparison.Ordinal))
                kase = RuCase.Gen;
            else if (tag.Contains("loc1", StringComparison.Ordinal) || tag.Contains("loc2", StringComparison.Ordinal))
                kase = RuCase.Loc;
            else if (tag.Contains("acc2", StringComparison.Ordinal))
                kase = RuCase.Acc;
        }

        if (kase is null) return [];

        var number = tag.Contains("plur", StringComparison.Ordinal) ? RuNumber.Plur : RuNumber.Sing;
        var gender = number == RuNumber.Plur ? RuGender.None : GenderFromTag(tag);
        return [new RuReading(kase.Value, number, gender)];
    }
}
