using System.Text.RegularExpressions;
using WriteLite.Language.Russian;
using WriteLite.Models;
using WriteLite.Services.Lexical;

namespace WriteLite.Services.Stylistics;

/// <summary>
/// Stylistic observations: redundancy, intensifier stacking, filler, bureaucratic phrasing,
/// and repetition across neighbouring sentences.
/// </summary>
/// <remarks>
/// <para><b>Nothing here is an error.</b> Every finding is <see cref="IssueCategory.Style"/>,
/// which makes it <see cref="IssueClass.Style"/> at the card, and none of it auto-applies.
/// §15 is explicit that not every style preference is objectively wrong, and the failure mode
/// this layer has to avoid is not missing a wordy sentence — it is turning a correct sentence
/// into five warnings. §47 asks for that to be measured separately, and
/// <see cref="MaxFindingsPerHundredWords"/> is the mechanism.</para>
///
/// <para><b>Closed lists, not heuristics.</b> Every phrase here is redundant or bureaucratic
/// in every context it appears in, which is why each one can carry a specific reason. The
/// judgement calls that need to know what the sentence means — whether a passive is the right
/// choice, whether a pronoun is ambiguous — are deliberately absent: they would be guesses
/// dressed as advice, and §15 says not to pretend otherwise.</para>
///
/// <para><b>Profiles change what is offered, never what is an error.</b> A colloquial word is
/// mentioned in an academic text and left alone in a chat message. §19: the suggestion is
/// always optional and the text is never treated as wrong.</para>
/// </remarks>
public sealed partial class RussianStyleAnalyzer
{
    private readonly StyleProfile _profile;
    private readonly ILexicalSignalSource? _signals;

    public RussianStyleAnalyzer(
        StyleProfile profile = StyleProfile.General,
        ILexicalSignalSource? lexicalSignals = null)
    {
        _profile = profile;
        _signals = lexicalSignals;
    }

    /// <summary>
    /// The density ceiling: style findings per hundred words.
    /// </summary>
    /// <remarks>
    /// §47. Three per hundred words is roughly one per longish sentence, which reads as help;
    /// the same rules unbounded on a heavily padded paragraph produce eight or nine, which
    /// reads as an argument. When the ceiling binds, the findings kept are the ones with the
    /// highest confidence, so what survives is the layer's best evidence rather than whatever
    /// happened to be earliest in the text.
    /// </remarks>
    private const double MaxFindingsPerHundredWords = 3.0;

    /// <summary>A redundant or padded phrase, and what it can be reduced to.</summary>
    private sealed record StylePhrase(
        string Pattern,
        string Replacement,
        string Title,
        string Reason,
        string RuleId,
        double Confidence,
        bool NeutralRegisterOnly = false);

    /// <summary>
    /// Phrases that say the same thing twice, or say in five words what one word says.
    /// </summary>
    /// <remarks>
    /// Ordered longest-pattern-first at match time so «на данный момент времени» is reported
    /// once as a whole rather than as «на данный момент» plus a stray «данный».
    /// </remarks>
    private static readonly StylePhrase[] Phrases =
    [
        // — redundancy: the phrase contains its own meaning twice —
        new(@"на\s+данный\s+момент\s+времени", "сейчас",
            "Избыточное сочетание",
            "«Момент» уже обозначает время, поэтому «времени» здесь лишнее.",
            "ru.style.redundant-moment", 0.9),
        new(@"в\s+период\s+времени", "в период",
            "Избыточное сочетание",
            "«Период» уже обозначает отрезок времени.",
            "ru.style.redundant-period", 0.88),
        new(@"очень\s+сильно", "",
            "Избыточное усиление",
            "«Очень» и «сильно» усиливают одно и то же; вместе они утяжеляют фразу, не добавляя смысла.",
            "ru.style.double-intensifier", 0.85),
        // Remove only the redundant analytical marker. Replacing the whole inflected
        // adjective with the adverb «лучше» produced «лучше решение» and broke agreement.
        new(@"более\s+(лучш(?:ий|ая|ее|ие|его|ему|им|ем|ей|ую|их|ими))", "$1",
            "Двойная степень сравнения",
            "«Лучший» уже сравнительная форма, «более» с ней не сочетается.",
            "ru.style.double-comparative", 0.95),
        new(@"самый\s+наилучш(ий|ая|ее|ие|его|ему|им|их)", "наилучший",
            "Двойная превосходная степень",
            "«Наилучший» уже превосходная форма.",
            "ru.style.double-superlative", 0.95),
        new(@"свободная\s+вакансия", "вакансия",
            "Избыточное сочетание",
            "Вакансия — это и есть свободное место.",
            "ru.style.redundant-vacancy", 0.9),
        new(@"памятный\s+сувенир", "сувенир",
            "Избыточное сочетание",
            "Сувенир — это и есть памятный предмет.",
            "ru.style.redundant-souvenir", 0.9),
        new(@"первый\s+дебют", "дебют",
            "Избыточное сочетание",
            "Дебют бывает только первым.",
            "ru.style.redundant-debut", 0.9),
        new(@"в\s+конечном\s+итоге\s+в\s+итоге", "в итоге",
            "Повтор оборота",
            "Оборот повторяется дважды подряд.",
            "ru.style.repeated-phrase", 0.9),

        // — filler: removable without changing the meaning —
        new(@"\bкак\s+бы\s+(?=[а-яё])", "",
            "Слово-паразит",
            "«Как бы» здесь не несёт смысла и ослабляет утверждение.",
            "ru.style.filler-kakby", 0.72, NeutralRegisterOnly: true),
        new(@"\bтак\s+сказать\b", "",
            "Слово-паразит",
            "«Так сказать» не добавляет смысла.",
            "ru.style.filler-taksskazat", 0.75, NeutralRegisterOnly: true),
        new(@"\bсобственно\s+говоря\b", "",
            "Лишний оборот",
            "«Собственно говоря» можно убрать без потери смысла.",
            "ru.style.filler-sobstvenno", 0.72, NeutralRegisterOnly: true),

        // — bureaucratic: a neutral verb replaced by a noun and a helper —
        new(@"осуществля(ть|ет|ем|ют)\s+деятельность", "работать",
            "Канцелярит",
            "«Осуществлять деятельность» — канцелярский оборот; глагол короче и понятнее.",
            "ru.style.bureaucratic-activity", 0.8, NeutralRegisterOnly: false),
        new(@"производ(ить|ит|им|ят)\s+оплату", "оплачивать",
            "Канцелярит",
            "Отглагольное существительное заменяется глаголом: «оплачивать».",
            "ru.style.bureaucratic-payment", 0.8),
        new(@"в\s+целях\s+(?=[а-яё]+ния\b)", "для ",
            "Канцелярит",
            "«В целях» с отглагольным существительным утяжеляет фразу; проще «для».",
            "ru.style.bureaucratic-purpose", 0.72),
        new(@"в\s+связи\s+с\s+тем,?\s+что", "потому что",
            "Канцелярит",
            "«В связи с тем что» — тяжёлый союз; «потому что» читается легче.",
            "ru.style.bureaucratic-because", 0.75),
        new(@"по\s+причине\s+того,?\s+что", "потому что",
            "Канцелярит",
            "«По причине того что» — тяжёлый союз; «потому что» читается легче.",
            "ru.style.bureaucratic-reason", 0.75),
        new(@"имеет\s+место\s+быть", "происходит",
            "Канцелярит",
            "«Имеет место быть» — смешение двух оборотов: «имеет место» и «есть».",
            "ru.style.bureaucratic-takesplace", 0.9),
        new(@"\bданн(ый|ая|ое|ые)\s+(?=[а-яё])", "этот ",
            "Канцелярит",
            "«Данный» в значении «этот» — канцелярская замена указательного местоимения.",
            "ru.style.bureaucratic-dannyj", 0.65, NeutralRegisterOnly: false),
    ];

    /// <summary>
    /// Words whose repetition in neighbouring sentences is worth mentioning.
    /// </summary>
    /// <remarks>
    /// Content words only, and long ones: repeating «который» or «это» is how Russian works,
    /// and reporting it would be reporting the language rather than the writing.
    /// </remarks>
    private const int MinRepeatedWordLength = 5;

    /// <summary>How many times a word must appear across the window before it is mentioned.</summary>
    private const int RepetitionThreshold = 3;

    /// <summary>How many neighbouring sentences the repetition window spans.</summary>
    private const int RepetitionWindowSentences = 3;

    private static readonly HashSet<string> RepetitionStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "который", "которая", "которое", "которые", "потому", "поэтому", "также", "чтобы",
        "когда", "нужно", "может", "можно", "будет", "было", "были", "очень", "более",
        "самый", "такой", "такая", "такие", "этого", "этому", "своей", "своих", "своего",
    };

    public IReadOnlyList<TextIssue> Analyze(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var findings = new List<TextIssue>();
        CollectPhrases(text, protectedSpans, findings);
        CollectAnalyticalComparatives(text, protectedSpans, findings);
        CollectRepetition(text, protectedSpans, findings);
        findings = WithoutSelfDefeatingEdits(text, findings);

        return ApplyDensityLimit(text, findings);
    }

    /// <summary>
    /// Drops a shortening whose replacement word is already in the sentence.
    /// </summary>
    /// <remarks>
    /// <para><b>Measured, not hypothetical.</b> A 480-word report pasted into the editor
    /// contained «На данный момент времени мы сейчас завершили основной этап». The redundancy
    /// rule fired correctly — «момент времени» does say time twice — and its replacement was
    /// «Сейчас». Applying it produced «Сейчас мы сейчас завершили»: shorter, still redundant,
    /// and now redundant in a way the writer did not put there.</para>
    ///
    /// <para>The general shape is that a style edit is only worth offering if the sentence it
    /// leaves behind is better than the one it replaces, and a rule that only looks at its own
    /// span cannot know that. Checking the rest of the sentence for the word about to be
    /// introduced is the cheapest test that catches the whole class, and it fails safe: an
    /// uncertain case produces no suggestion, which §43 asks for and which costs nothing here,
    /// because a stylistic note is the least important thing on the card.</para>
    ///
    /// <para>Deletions are exempt — a rule whose replacement is empty introduces nothing — and
    /// so are short function words, where a coincidental match says nothing about redundancy.</para>
    /// </remarks>
    private static List<TextIssue> WithoutSelfDefeatingEdits(string text, List<TextIssue> findings)
    {
        var kept = new List<TextIssue>(findings.Count);

        foreach (var issue in findings)
        {
            if (string.IsNullOrWhiteSpace(issue.Replacement) || IntroducesNoRepetition(text, issue))
            {
                kept.Add(issue);
            }
        }

        return kept;
    }

    private static bool IntroducesNoRepetition(string text, TextIssue issue)
    {
        var (sentenceStart, sentenceEnd) = SentenceAround(text, issue.Start);
        var before = text[sentenceStart..issue.Start];
        var after = text[Math.Min(issue.Start + issue.Length, sentenceEnd)..sentenceEnd];
        var remainder = before + " " + after;

        foreach (var word in issue.Replacement!.Split(
                     (char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var bare = word.Trim('.', ',', '!', '?', ';', ':', '(', ')');

            // Short words are skipped: a coincidental match on a four-letter function word
            // says nothing about whether the sentence now repeats itself, and suppressing on
            // one would cost real suggestions.
            if (bare.Length < 5) continue;
            if (ContainsWord(remainder, bare)) return false;
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="haystack"/> holds <paramref name="word"/> as a whole word.
    /// </summary>
    /// <remarks>
    /// Written out rather than expressed as a regex with word boundaries, because the
    /// characters either side are the entire question and a hand-rolled test says so plainly.
    /// The comparison is ordinal-ignore-case: a sentence-initial replacement and the same word
    /// lower-cased further along are the same word here, which is the case this guard exists
    /// for.
    /// </remarks>
    private static bool ContainsWord(string haystack, string word)
    {
        var from = 0;
        while (from <= haystack.Length - word.Length)
        {
            var at = haystack.IndexOf(word, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;

            var beforeOk = at == 0 || !char.IsLetter(haystack[at - 1]);
            var after = at + word.Length;
            var afterOk = after >= haystack.Length || !char.IsLetter(haystack[after]);
            if (beforeOk && afterOk) return true;

            from = at + 1;
        }

        return false;
    }

    private static (int Start, int End) SentenceAround(string text, int offset)
    {
        var start = Math.Clamp(offset, 0, text.Length);
        while (start > 0 && !IsSentenceBoundary(text[start - 1])) start--;

        var end = Math.Clamp(offset, 0, text.Length);
        while (end < text.Length && !IsSentenceBoundary(text[end])) end++;

        return (start, end);
    }

    private static bool IsSentenceBoundary(char value)
        => value is '.' or '!' or '?' or '\n' or '\r';

    private void CollectAnalyticalComparatives(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        List<TextIssue> findings)
    {
        if (_signals is null) return;

        foreach (Match match in AnalyticalComparativeRegex().Matches(text))
        {
            if (ProtectedTextSpans.Overlaps(match.Index, match.Length, protectedSpans)) continue;
            if (findings.Any(issue => match.Index < issue.Start + issue.Length
                                      && issue.Start < match.Index + match.Length)) continue;

            var form = match.Groups["form"].Value;
            if (_signals.For(form).PartOfSpeech != RussianPartOfSpeech.Comparative) continue;

            findings.Add(new TextIssue(
                match.Index,
                match.Length,
                match.Value,
                RussianCase.MatchLeading(match.Value, form),
                "Двойная степень сравнения",
                "Сравнительная форма уже передаёт значение «более»; оставьте только один способ сравнения.",
                IssueCategory.Style,
                IssueSeverity.Suggestion,
                CanApplyAutomatically: false,
                RuleId: "ru.style.double-comparative",
                LinguisticCategory: LinguisticIssueCategory.RepeatedWord,
                Confidence: 0.95));
        }
    }

    [GeneratedRegex(@"\bболее\s+(?<form>[\p{L}ёЁ]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex AnalyticalComparativeRegex();

    private void CollectPhrases(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        List<TextIssue> findings)
    {
        // Every candidate first, then longest *match* wins. Ordering by pattern length instead
        // was wrong and quietly so: the regex for «данный» is a longer string than the regex
        // for «на данный момент времени», so the short phrase claimed the span and the whole
        // redundancy went unreported. What matters is how much text a match covers.
        var candidates = new List<(Match Match, StylePhrase Phrase)>();
        foreach (var phrase in Phrases)
        {
            if (phrase.NeutralRegisterOnly && !_profile.ExpectsNeutralRegister()) continue;

            foreach (Match match in Regex.Matches(
                text, phrase.Pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200)))
            {
                if (ProtectedTextSpans.Overlaps(match.Index, match.Length, protectedSpans)) continue;
                candidates.Add((match, phrase));
            }
        }

        var claimed = new List<(int Start, int End)>();
        foreach (var (match, phrase) in candidates
            .OrderByDescending(c => c.Match.Length)
            .ThenBy(c => c.Match.Index))
        {
            var edit = BuildEdit(text, match, phrase);
            if (edit is null) continue;

            var (start, length, replacement) = edit.Value;
            if (claimed.Any(c => start < c.End && c.Start < start + length)) continue;

            claimed.Add((start, start + length));
            findings.Add(new TextIssue(
                start,
                length,
                text.Substring(start, length),
                replacement,
                phrase.Title,
                phrase.Reason,
                IssueCategory.Style,
                IssueSeverity.Suggestion,
                CanApplyAutomatically: false,
                RuleId: phrase.RuleId,
                LinguisticCategory: LinguisticIssueCategory.RepeatedWord,
                Confidence: phrase.Confidence));
        }
    }

    /// <summary>
    /// The span to replace and what to put there, or null when the edit would damage the text.
    /// </summary>
    /// <remarks>
    /// A phrase whose replacement is empty is being deleted, and the span has to grow to take
    /// one of the surrounding spaces with it. Reporting the bare phrase and an empty
    /// replacement turned «Я был очень сильно наслышан» into «Я был  наслышан» — a double
    /// space, on a suggestion whose entire purpose was to tidy the sentence.
    /// </remarks>
    private static (int Start, int Length, string Replacement)? BuildEdit(
        string text,
        Match match,
        StylePhrase phrase)
    {
        if (phrase.Replacement.Length > 0)
        {
            var replaced = match.Result(phrase.Replacement);
            if (string.Equals(replaced, match.Value, StringComparison.Ordinal)) return null;
            return (match.Index, match.Length, RussianCase.MatchLeading(match.Value, replaced));
        }

        var end = match.Index + match.Length;
        if (end < text.Length && text[end] == ' ') return (match.Index, match.Length + 1, string.Empty);
        if (match.Index > 0 && text[match.Index - 1] == ' ') return (match.Index - 1, match.Length + 1, string.Empty);
        return null;
    }

    /// <summary>
    /// A content word repeated across neighbouring sentences.
    /// </summary>
    /// <remarks>
    /// §20 asks for repetition across more than one sentence, and §19 asks for creative and
    /// casual writing to keep its voice — repetition is a device in prose and a defect in a
    /// report, so this stands down entirely on those profiles rather than being tuned for
    /// them. No replacement is offered: §20 says to suggest alternatives only when they
    /// preserve meaning, and nothing here knows whether a synonym would.
    /// </remarks>
    private void CollectRepetition(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        List<TextIssue> findings)
    {
        if (_profile.ProtectsAuthorVoice()) return;

        var sentences = SplitSentences(text).ToList();
        if (sentences.Count < 2) return;

        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i + 1 < sentences.Count; i++)
        {
            var window = sentences.Skip(i).Take(RepetitionWindowSentences).ToList();
            if (window.Count < 2) break;

            var counts = new Dictionary<string, List<(int Start, int Length)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (start, length) in window)
            {
                foreach (Match word in WordRegex().Matches(text.Substring(start, length)))
                {
                    if (word.Length < MinRepeatedWordLength) continue;
                    var value = word.Value.ToLowerInvariant();
                    if (RepetitionStopWords.Contains(value)) continue;

                    if (!counts.TryGetValue(value, out var list)) counts[value] = list = [];
                    list.Add((start + word.Index, word.Length));
                }
            }

            foreach (var (word, occurrences) in counts)
            {
                if (occurrences.Count < RepetitionThreshold) continue;
                if (!reported.Add(word)) continue;

                var last = occurrences[^1];
                if (ProtectedTextSpans.Overlaps(last.Start, last.Length, protectedSpans)) continue;

                findings.Add(new TextIssue(
                    last.Start,
                    last.Length,
                    text.Substring(last.Start, last.Length),
                    null,
                    "Повтор слова в соседних предложениях",
                    $"Слово «{word}» встречается {occurrences.Count} раза в соседних предложениях. "
                    + "Если это не термин, попробуйте синоним или перестройте фразу.",
                    IssueCategory.Style,
                    IssueSeverity.Suggestion,
                    CanApplyAutomatically: false,
                    RuleId: "ru.style.repetition-across-sentences",
                    LinguisticCategory: LinguisticIssueCategory.RepeatedWord,
                    Confidence: 0.7));
            }
        }
    }

    /// <summary>
    /// Caps how much this layer may say about a given amount of text.
    /// </summary>
    /// <remarks>
    /// §47: style should feel helpful, not punitive. The cap is on the whole layer rather than
    /// per rule, because the annoying case is not one rule firing repeatedly — it is six
    /// different rules each firing once on the same paragraph.
    /// </remarks>
    private static IReadOnlyList<TextIssue> ApplyDensityLimit(string text, List<TextIssue> findings)
    {
        if (findings.Count == 0) return [];

        var words = WordRegex().Matches(text).Count;
        var allowed = Math.Max(1, (int)Math.Floor(words * MaxFindingsPerHundredWords / 100.0));
        if (findings.Count <= allowed) return findings.OrderBy(f => f.Start).ToList();

        return findings
            .OrderByDescending(f => f.Confidence)
            .ThenBy(f => f.Start)
            .Take(allowed)
            .OrderBy(f => f.Start)
            .ToList();
    }

    private static IEnumerable<(int Start, int Length)> SplitSentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?' or '…' or '\n')) continue;
            if (i + 1 > start) yield return (start, i + 1 - start);
            start = i + 1;
        }

        if (start < text.Length) yield return (start, text.Length - start);
    }

    [GeneratedRegex(@"[\p{L}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}

internal static class RussianCase
{
    public static string MatchLeading(string source, string replacement)
    {
        if (source.Length == 0 || replacement.Length == 0) return replacement;
        if (!char.IsUpper(source[0]) || !char.IsLower(replacement[0])) return replacement;
        var culture = new System.Globalization.CultureInfo("ru-RU");
        return char.ToUpper(replacement[0], culture) + replacement[1..];
    }
}
