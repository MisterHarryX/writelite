namespace WriteLite.Services.Lexical;

public enum LexicalLanguage
{
    Unknown,
    Russian
}

public enum LexicalPartOfSpeech
{
    Unknown,
    Noun,
    Verb,
    Adjective,
    Adverb,
    Pronoun,
    Preposition,
    Conjunction,
    Particle,
    Interjection,
    Numeral
}

public enum SyntacticRole
{
    Unknown,
    Subject,
    Predicate,
    Object,
    Attribute,
    Adverbial,
    Complement,
    ParticleRole,
    ConjunctionRole,
    PrepositionalObject
}

public sealed record LexicalLookupRequest(
    string Word,
    string Sentence,
    string FullText,
    int Start,
    int Length,
    LexicalLanguage SourceLanguage,
    long RequestId = 0,
    int GenerationId = 0,
    long TextVersion = 0,
    string? TargetId = null);

public sealed record LexicalSuggestion(
    string Value,
    string Lemma,
    LexicalPartOfSpeech PartOfSpeech,
    string? Label,
    double Relevance,
    bool CanReplace,
    string? SenseId = null);

public sealed record LexicalDefinition(
    string Definition,
    LexicalPartOfSpeech PartOfSpeech,
    string? UsageLabel,
    string? SenseId = null,
    string? SourceId = null,
    bool IsHistorical = false,
    string? EraLabel = null);

public sealed record LexicalExample(
    string Text,
    string? SenseId,
    string? HighlightWord = null);

public sealed record MorphologicalFeatures(
    string? Case = null,
    string? Number = null,
    string? Gender = null,
    string? Tense = null,
    string? Person = null,
    string? Aspect = null,
    string? Mood = null,
    string? Animacy = null,
    string? Degree = null,
    string? Voice = null)
{
    public IReadOnlyList<string> ToDisplayList()
    {
        var list = new List<string>();
        void Add(string? label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) list.Add($"{label}: {value}");
        }

        Add("падеж", Case);
        Add("число", Number);
        Add("род", Gender);
        Add("время", Tense);
        Add("лицо", Person);
        Add("вид", Aspect);
        Add("наклонение", Mood);
        Add("одуш.", Animacy);
        Add("степень", Degree);
        Add("залог", Voice);
        return list;
    }
}

public sealed record WordRelation(
    string Value,
    string RelationType,
    string? Label = null);

public sealed record SyntacticAnalysis(
    SyntacticRole Role,
    string? HeadWord,
    string? DependencyLabel,
    string Explanation,
    IReadOnlyList<WordRelation> Relations);

public sealed record LexicalLookupResult(
    string Word,
    string Lemma,
    LexicalLanguage Language,
    LexicalPartOfSpeech PartOfSpeech,
    IReadOnlyList<LexicalSuggestion> Synonyms,
    IReadOnlyList<LexicalDefinition> Definitions,
    IReadOnlyList<LexicalExample> Examples,
    long RequestId = 0,
    bool IsEmpty = false,
    string? StatusMessage = null,
    string? PackVersion = null,
    MorphologicalFeatures? Morphology = null,
    SyntacticAnalysis? Syntax = null,
    IReadOnlyList<LexicalSuggestion>? Antonyms = null,
    IReadOnlyList<WordRelation>? Relations = null,
    string? SurfaceFormNote = null,
    string? PackLicense = null,
    string? PackSource = null)
{
    public static LexicalLookupResult Empty(string word, long requestId, string? message = null)
        => new(
            word,
            word,
            LexicalLanguage.Unknown,
            LexicalPartOfSpeech.Unknown,
            [],
            [],
            [],
            requestId,
            IsEmpty: true,
            StatusMessage: message ?? "Словарные данные не найдены.");
}

public sealed record WordRange(
    int Start,
    int Length,
    string Word,
    string Sentence,
    LexicalLanguage Language)
{
    public int End => Start + Length;
}

public sealed record LexicalReplacementRequest(
    string TargetId,
    int GenerationId,
    long TextVersion,
    string CurrentText,
    int Start,
    int Length,
    string OriginalWord,
    string ReplacementLemmaOrForm,
    bool SupportsDirectWrite,
    bool IsPassword,
    bool IsReadOnly);

public sealed record LexicalReplacementResult(
    bool Success,
    string? NewText,
    int CaretIndex,
    string AppliedForm,
    string? FailureReason,
    bool OfferCopyOnly = false);
