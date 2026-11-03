using WriteLite.Language.Russian;

namespace WriteLite.Services.Lexical;

/// <summary>
/// Word morphology for the dictionary card, read from the Russian form index.
/// </summary>
/// <remarks>
/// <para>The card used to describe words with <see cref="RuleBasedMorphologyService"/>, which
/// guesses from endings and said so with a question mark: «заняться» was shown as «вид:
/// несов.?» — wrong, and presented to the reader as a finding. The form index carries the real
/// answer for every word it knows (OpenCorpora tags «INFN,perf,intr»), so for those words this
/// service reports what the index records and nothing else.</para>
///
/// <para>A word the index does not know gets no grammatical features at all rather than a
/// guess. The card then shows the dictionary article without a grammar block, which is honest;
/// a confident-looking wrong block is not. Lemma, part of speech and inflection of synonyms
/// still fall back to the rule-based service, because those are used to find articles and to
/// shape replacements, where an approximation is better than nothing.</para>
/// </remarks>
public sealed class FormIndexWordMorphologyService : IWordMorphologyService
{
    private readonly Func<RussianMorphology?> _morphology;
    private readonly RuleBasedMorphologyService _fallback;

    public FormIndexWordMorphologyService(
        Func<RussianMorphology?> morphology,
        Func<string, LexicalLanguage, LexicalEntry?>? lookupEntry = null)
    {
        _morphology = morphology ?? throw new ArgumentNullException(nameof(morphology));
        _fallback = new RuleBasedMorphologyService(lookupEntry);
    }

    public string GetLemma(string word, LexicalLanguage language)
    {
        if (Profile(word, language) is { Lemma.Length: > 0 } profile) return profile.Lemma;
        return _fallback.GetLemma(word, language);
    }

    public LexicalPartOfSpeech GetPartOfSpeech(string word, LexicalLanguage language)
    {
        if (Profile(word, language) is { } profile)
        {
            var pos = Map(profile.PartOfSpeech);
            if (pos != LexicalPartOfSpeech.Unknown) return pos;
        }

        return _fallback.GetPartOfSpeech(word, language);
    }

    public MorphologicalFeatures AnalyzeFeatures(string word, LexicalLanguage language, string? sentence = null, int wordStart = -1)
    {
        if (language == LexicalLanguage.English) return _fallback.AnalyzeFeatures(word, language, sentence, wordStart);

        var profile = Profile(word, language);
        return profile is null ? new MorphologicalFeatures() : Describe(profile);
    }

    public string ApplySurfaceCase(string original, string replacement) => _fallback.ApplySurfaceCase(original, replacement);

    public string? InflectLike(string originalSurface, string lemmaForm, LexicalLanguage language)
        => _fallback.InflectLike(originalSurface, lemmaForm, language);

    private RuWordProfile? Profile(string word, LexicalLanguage language)
    {
        if (language == LexicalLanguage.English || string.IsNullOrWhiteSpace(word)) return null;
        return _morphology()?.Describe(word.Trim());
    }

    /// <summary>The profile in the words a reader of the card understands, never abbreviated.</summary>
    internal static MorphologicalFeatures Describe(RuWordProfile p)
    {
        var verbal = p.PartOfSpeech is RussianPartOfSpeech.Verb or RussianPartOfSpeech.Infinitive
            or RussianPartOfSpeech.Gerund or RussianPartOfSpeech.ParticipleFull
            or RussianPartOfSpeech.ParticipleShort;
        var finite = p.PartOfSpeech == RussianPartOfSpeech.Verb;

        return new MorphologicalFeatures(
            Case: CaseSummary(p),
            Number: p.Number switch
            {
                RuNumber.Sing => "единственное",
                RuNumber.Plur => "множественное",
                _ => null,
            },
            Gender: p.Gender switch
            {
                RuGender.Masc => "мужской",
                RuGender.Fem => "женский",
                RuGender.Neut => "средний",
                _ => null,
            },
            Tense: !finite || p.Imperative ? null : p.Tense switch
            {
                RuTense.Past => "прошедшее",
                RuTense.Present => "настоящее",
                RuTense.Future => "будущее",
                _ => null,
            },
            Person: !finite ? null : p.Person switch
            {
                RuPerson.First => "1-е",
                RuPerson.Second => "2-е",
                RuPerson.Third => "3-е",
                _ => null,
            },
            Aspect: p.Aspect switch
            {
                RuAspect.Perfective => "совершенный",
                RuAspect.Imperfective => "несовершенный",
                _ => null,
            },
            Mood: finite && p.Imperative ? "повелительное" : null,
            Animacy: p.Animate switch
            {
                true => "одушевлённое",
                false => "неодушевлённое",
                _ => null,
            },
            Transitivity: !verbal ? null : p.Transitive switch
            {
                true => "переходный",
                false => p.Reflexive ? "непереходный, возвратный" : "непереходный",
                _ => null,
            },
            Register: Register(p.Flags));
    }

    /// <summary>
    /// The slots a nominal form can fill, grouped by number: «именительный или винительный», or
    /// «родительный ед.; именительный или винительный мн.» when the spelling spans both.
    /// </summary>
    private static string? CaseSummary(RuWordProfile p)
    {
        if (p.Readings.Count == 0) return null;

        var groups = p.Readings
            .GroupBy(r => r.Number)
            .Select(g => (Number: g.Key, Cases: string.Join(" или ", g.Select(r => CaseName(r.Case)))))
            .ToArray();

        if (groups.Length == 1) return groups[0].Cases;

        return string.Join("; ", groups.Select(g => $"{g.Cases} {(g.Number == RuNumber.Sing ? "ед." : "мн.")}"));
    }

    private static string CaseName(RuCase value) => value switch
    {
        RuCase.Nom => "именительный",
        RuCase.Gen => "родительный",
        RuCase.Dat => "дательный",
        RuCase.Acc => "винительный",
        RuCase.Ins => "творительный",
        _ => "предложный",
    };

    private static string? Register(RussianFormFlags flags)
    {
        var labels = new List<string>(3);
        if ((flags & RussianFormFlags.Slang) != 0) labels.Add("сленг");
        if ((flags & RussianFormFlags.Informal) != 0) labels.Add("разговорное");
        if ((flags & RussianFormFlags.Obscene) != 0) labels.Add("грубое");
        if ((flags & RussianFormFlags.Archaic) != 0) labels.Add("устаревшее");
        if ((flags & RussianFormFlags.Borrowing) != 0) labels.Add("заимствование");
        if ((flags & RussianFormFlags.Abbreviation) != 0) labels.Add("аббревиатура");
        if ((flags & RussianFormFlags.ProperName) != 0) labels.Add("имя собственное");
        return labels.Count == 0 ? null : string.Join(", ", labels);
    }

    private static LexicalPartOfSpeech Map(RussianPartOfSpeech pos) => pos switch
    {
        RussianPartOfSpeech.Noun => LexicalPartOfSpeech.Noun,
        RussianPartOfSpeech.AdjectiveFull or RussianPartOfSpeech.AdjectiveShort
            or RussianPartOfSpeech.Comparative => LexicalPartOfSpeech.Adjective,
        RussianPartOfSpeech.Verb or RussianPartOfSpeech.Infinitive => LexicalPartOfSpeech.Verb,
        RussianPartOfSpeech.Adverb => LexicalPartOfSpeech.Adverb,
        RussianPartOfSpeech.Pronoun => LexicalPartOfSpeech.Pronoun,
        RussianPartOfSpeech.Preposition => LexicalPartOfSpeech.Preposition,
        RussianPartOfSpeech.Conjunction => LexicalPartOfSpeech.Conjunction,
        RussianPartOfSpeech.Particle => LexicalPartOfSpeech.Particle,
        RussianPartOfSpeech.Interjection => LexicalPartOfSpeech.Interjection,
        RussianPartOfSpeech.Numeral => LexicalPartOfSpeech.Numeral,
        _ => LexicalPartOfSpeech.Unknown,
    };
}
