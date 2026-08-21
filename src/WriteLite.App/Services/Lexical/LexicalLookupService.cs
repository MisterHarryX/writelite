namespace WriteLite.Services.Lexical;

/// <summary>One related word the editor can offer, and whether it opens an article of its own.</summary>
public sealed record LexicalWord(string Value, LexicalLanguage Language, bool HasArticle);

/// <summary>Everything the editor's word panel shows for one word.</summary>
public sealed record LexicalCard(
    string Word,
    string Lemma,
    LexicalLanguage Language,
    string PartOfSpeech,
    IReadOnlyList<LexicalDefinition> Definitions,
    IReadOnlyList<LexicalWord> Synonyms,
    IReadOnlyList<LexicalWord> Antonyms,
    IReadOnlyList<LexicalWord> Translations,
    IReadOnlyList<string> Forms,
    string? StatusMessage,
    string Provenance)
{
    public bool IsEmpty =>
        Definitions.Count == 0 && Synonyms.Count == 0 && Antonyms.Count == 0 && Translations.Count == 0;

    public static LexicalCard Unavailable(string word, string message) => new(
        word, word, LexicalLanguage.Unknown, string.Empty, [], [], [], [], [], message,
        "локальные данные, без интернета");
}

/// <summary>
/// The editor's view of the installed dictionaries.
/// </summary>
/// <remarks>
/// A thin composition over the offline lexical service and the translation index —
/// the same data the dictionary page and the double-click card already read. It
/// exists so the editor asks one question ("tell me about this word") instead of
/// reassembling the answer from three services, and so the "does this chip lead
/// anywhere" check lives in one place rather than being duplicated per surface.
///
/// Strictly offline: every answer comes from packs on disk, and a word the packs do
/// not cover is reported as not covered rather than guessed at.
/// </remarks>
public sealed class LexicalLookupService(
    ILexicalKnowledgeService knowledge,
    TranslationIndex? translations = null)
{
    private readonly LexicalLanguageDetector _languages = new();

    public bool IsReady => knowledge.IsPackLoaded;

    public async Task<LexicalCard> LookupAsync(
        string word,
        string sentence,
        LexicalLanguage language = LexicalLanguage.Unknown,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return LexicalCard.Unavailable(string.Empty, "Выделите слово, чтобы посмотреть его в словаре.");
        }

        var detected = language == LexicalLanguage.Unknown ? _languages.DetectWord(word) : language;
        var context = string.IsNullOrWhiteSpace(sentence) ? word : sentence;

        var request = new LexicalLookupRequest(
            Word: word,
            Sentence: context,
            FullText: context,
            Start: Math.Max(0, context.IndexOf(word, StringComparison.CurrentCultureIgnoreCase)),
            Length: word.Length,
            SourceLanguage: detected);

        var result = await knowledge.LookupAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // Translation lookups are indexed reads, but they still happen off the
        // dispatcher: this service is shared with the card that appears over other
        // applications while someone is typing in them.
        var translated = await Task.Run(
            () => ResolveTranslations(result, detected),
            cancellationToken).ConfigureAwait(false);

        return new LexicalCard(
            result.Word,
            result.Lemma,
            detected,
            PartOfSpeechName(result.PartOfSpeech),
            result.Definitions,
            ToWords(result.Synonyms, detected),
            ToWords(result.Antonyms, detected),
            translated,
            result.Morphology?.ToDisplayList() ?? [],
            result.IsEmpty ? result.StatusMessage ?? "Слова нет в установленных словарях." : null,
            BuildProvenance(result));
    }

    private IReadOnlyList<LexicalWord> ResolveTranslations(LexicalLookupResult result, LexicalLanguage language)
    {
        if (translations is null)
        {
            return [];
        }

        var target = language == LexicalLanguage.English ? LexicalLanguage.Russian : LexicalLanguage.English;

        // The index is keyed by lemma, so a surface form has to resolve through the
        // lemma the lookup already established before falling back to the word itself.
        var values = translations.Translate(result.Lemma, language);
        if (values.Count == 0 && !string.Equals(result.Lemma, result.Word, StringComparison.OrdinalIgnoreCase))
        {
            values = translations.Translate(result.Word, language);
        }

        return values
            .Select(value => new LexicalWord(value, target, HasArticle(value, target)))
            .ToArray();
    }

    private IReadOnlyList<LexicalWord> ToWords(IReadOnlyList<LexicalSuggestion>? suggestions, LexicalLanguage language)
    {
        if (suggestions is null || suggestions.Count == 0)
        {
            return [];
        }

        return suggestions
            .Select(suggestion => suggestion.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Select(value => new LexicalWord(value, language, HasArticle(value, language)))
            .ToArray();
    }

    /// <summary>True when clicking this word would actually open something.</summary>
    private bool HasArticle(string word, LexicalLanguage language)
    {
        if (knowledge is not OfflineLexicalKnowledgeService offline)
        {
            // A service that cannot be probed: treat every word as navigable rather
            // than greying out ones that would in fact have opened.
            return true;
        }

        var probe = language == LexicalLanguage.English
            ? TranslationIndex.Normalize(word, language)
            : word;

        return offline.LookupEntry(probe, language) is not null;
    }

    private string BuildProvenance(LexicalLookupResult result)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(result.PackSource))
        {
            parts.Add(result.PackSource!);
        }

        if (!string.IsNullOrWhiteSpace(result.PackVersion))
        {
            parts.Add($"версия {result.PackVersion}");
        }

        parts.Add("локальные данные, без интернета");
        return string.Join(" · ", parts);
    }

    public static string PartOfSpeechName(LexicalPartOfSpeech partOfSpeech) => partOfSpeech switch
    {
        LexicalPartOfSpeech.Noun => "существительное",
        LexicalPartOfSpeech.Verb => "глагол",
        LexicalPartOfSpeech.Adjective => "прилагательное",
        LexicalPartOfSpeech.Adverb => "наречие",
        LexicalPartOfSpeech.Pronoun => "местоимение",
        LexicalPartOfSpeech.Preposition => "предлог",
        LexicalPartOfSpeech.Conjunction => "союз",
        LexicalPartOfSpeech.Particle => "частица",
        LexicalPartOfSpeech.Interjection => "междометие",
        LexicalPartOfSpeech.Numeral => "числительное",
        _ => string.Empty
    };
}
