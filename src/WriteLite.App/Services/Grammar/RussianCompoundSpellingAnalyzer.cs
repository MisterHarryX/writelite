using WriteLite.Language.Russian;
using WriteLite.Models;

namespace WriteLite.Services.Grammar;

/// <summary>
/// Слитное, раздельное и дефисное написание, решённое по словарю форм.
/// </summary>
/// <remarks>
/// <para>The spell checker sees one token at a time, so a word split or merged in the wrong place
/// reaches it as an unknown token and comes back as the nearest dictionary word — «незнаю» became
/// «нежною», «нехочу» became «нахожу». Every rule here instead asks the form index whether the
/// other spelling of the same letters is a word, which is what a person checking a dictionary
/// would do:</para>
/// <list type="bullet">
///   <item>«незнаю» — not a word; «знаю» is a verb → «не знаю» (не с глаголами и деепричастиями
///   раздельно).</item>
///   <item>«не навижу» — «навижу» is not a word, «ненавижу» is → слитно (слова, которые без «не» не
///   употребляются).</item>
///   <item>«северо западный», «вице президент», «кое что», «из за» — the hyphenated form is a
///   dictionary word → через дефис.</item>
///   <item>«по русски», «поанглийски» — «по-русски» is an adverb in the dictionary → через дефис,
///   unless a noun follows and the pair is a preposition with its object («по русскому полю»).</item>
///   <item>«кто нибудь», «смотри ка» — particles «-нибудь», «-либо», «-ка» → через дефис.</item>
///   <item>«пол часа», «пол лимона» — «пол-» with a genitive noun: слитно, but через дефис before a
///   vowel, «л» or a capital letter.</item>
/// </list>
/// <para>Everything is gated on the dictionary rather than on a list of examples, so it covers
/// «юго-восточный» and «интернет-магазине» as well as the cases in the tests.</para>
/// </remarks>
public sealed class RussianCompoundSpellingAnalyzer : GrammarAnalyzerBase
{
    private readonly RussianFormIndex _index;

    public RussianCompoundSpellingAnalyzer(RussianFormIndex index) => _index = index;

    public static RussianCompoundSpellingAnalyzer? TryCreate(RussianFormIndex? index) =>
        index is null ? null : new RussianCompoundSpellingAnalyzer(index);

    protected override void CollectCore(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        var tokens = RussianTokens.Split(text);
        if (tokens.Count == 0) return;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (ProtectedTextSpans.Overlaps(token.Start, token.Length, protectedSpans)) continue;

            NeWithVerbWrittenSolid(text, token, issues);

            if (i + 1 >= tokens.Count) continue;
            var next = tokens[i + 1];
            if (!SingleSpaceBetween(text, token, next)) continue;
            if (ProtectedTextSpans.Overlaps(token.Start, next.End - token.Start, protectedSpans)) continue;

            if (UnboundWordAfterNe(text, token, next, issues)) continue;
            if (Particle(text, tokens, i, issues)) continue;
            if (PoAdverb(text, tokens, i, issues)) continue;
            if (Pol(text, tokens, i, issues)) continue;
            HyphenatedCompound(text, tokens, i, issues);
        }

        PoAdverbWrittenSolid(text, tokens, protectedSpans, issues);
    }

    // ── «незнаю» → «не знаю» ────────────────────────────────────────────────────────────────

    private void NeWithVerbWrittenSolid(string text, RussianToken token, ICollection<TextIssue> issues)
    {
        var lower = token.Value.ToLowerInvariant();
        if (lower.Length < 5 || !lower.StartsWith("не", StringComparison.Ordinal)) return;
        if (lower.Contains('-')) return;
        if (_index.Contains(lower)) return;            // «ненавижу», «недоумевает», «нездоровится»

        var rest = lower[2..];
        if (rest.StartsWith("до", StringComparison.Ordinal) && _index.Contains("недо" + rest[2..])) return;
        var pos = _index.GetInfo(rest).PartOfSpeech;
        if (pos is not (RussianPartOfSpeech.Verb or RussianPartOfSpeech.Infinitive or RussianPartOfSpeech.Gerund))
        {
            return;
        }

        var replacement = RussianTokens.MatchLeadingCase(token.Value, "не " + rest);
        var what = pos == RussianPartOfSpeech.Gerund ? "деепричастиями" : "глаголами";
        issues.Add(new TextIssue(
            token.Start,
            token.Length,
            token.Value,
            replacement,
            "«Не» с глаголом пишется раздельно",
            $"С {what} «не» пишется раздельно: «{replacement.ToLowerInvariant()}». Слитно пишутся только "
            + "глаголы, которые без «не» не употребляются («ненавидеть», «недоумевать»), и глаголы "
            + "с приставкой «недо-» («недооценить»).",
            IssueCategory.Orthography,
            IssueSeverity.Error,
            CanApplyAutomatically: true,
            RuleId: "ru.spelling.ne-verb",
            LinguisticCategory: LinguisticIssueCategory.NeWithPartsOfSpeech,
            Confidence: 0.96));
    }

    // ── «не навижу» → «ненавижу» ───────────────────────────────────────────────────────────

    private bool UnboundWordAfterNe(string text, RussianToken ne, RussianToken word, ICollection<TextIssue> issues)
    {
        if (!ne.Value.Equals("не", StringComparison.OrdinalIgnoreCase)) return false;
        var lower = word.Value.ToLowerInvariant();
        if (lower.Length < 3 || lower.Contains('-')) return false;
        if (_index.Contains(lower)) return false;
        var merged = "не" + lower;
        if (!_index.Contains(merged)) return false;

        var original = text[ne.Start..word.End];
        var replacement = RussianTokens.MatchLeadingCase(ne.Value, merged);
        issues.Add(new TextIssue(
            ne.Start,
            original.Length,
            original,
            replacement,
            "Слово без «не» не употребляется",
            $"«{merged}» без «не» не существует («{lower}» — не слово), поэтому пишется слитно.",
            IssueCategory.Orthography,
            IssueSeverity.Error,
            CanApplyAutomatically: true,
            RuleId: "ru.spelling.ne-unbound",
            LinguisticCategory: LinguisticIssueCategory.NeWithPartsOfSpeech,
            Confidence: 0.94));
        return true;
    }

    // ── «кто нибудь», «смотри ка», «опять таки» ─────────────────────────────────────────────

    private static readonly HashSet<string> IndefiniteBases = new(StringComparer.OrdinalIgnoreCase)
    {
        "кто", "что", "где", "куда", "откуда", "когда", "почему", "зачем", "как", "чей", "чья", "чьё", "чьи",
        "какой", "какая", "какое", "какие", "какого", "какую", "каким", "каких", "какому",
        "кого", "кому", "кем", "чего", "чему", "чем", "ком", "чём", "сколько",
    };

    private bool Particle(string text, List<RussianToken> tokens, int i, ICollection<TextIssue> issues)
    {
        var word = tokens[i];
        var particle = tokens[i + 1].Value.ToLowerInvariant();
        string? joined = null;
        string? reason = null;

        if (particle is "нибудь" or "либо" && IndefiniteBases.Contains(word.Value))
        {
            joined = word.Value + "-" + particle;
            reason = $"Частица «-{particle}» в неопределённых местоимениях и наречиях пишется через дефис.";
        }
        else if (particle == "ка" && IsImperativeOrNu(word.Value))
        {
            joined = word.Value + "-ка";
            reason = "Частица «-ка» после повелительного наклонения пишется через дефис.";
        }
        else if (particle == "таки" && _index.Contains(word.Value.ToLowerInvariant() + "-таки"))
        {
            joined = word.Value + "-таки";
            reason = "Частица «-таки» после наречий и частиц пишется через дефис.";
        }

        if (joined is null) return false;
        var original = text[word.Start..tokens[i + 1].End];
        issues.Add(new TextIssue(
            word.Start,
            original.Length,
            original,
            joined,
            "Частица пишется через дефис",
            reason!,
            IssueCategory.Orthography,
            IssueSeverity.Error,
            CanApplyAutomatically: true,
            RuleId: "ru.spelling.particle-hyphen",
            LinguisticCategory: LinguisticIssueCategory.JoinedOrSeparateSpelling,
            Confidence: 0.94));
        return true;
    }

    private bool IsImperativeOrNu(string word)
    {
        var lower = word.ToLowerInvariant();
        if (lower is "ну" or "давай" or "давайте" or "дай" or "дайте") return true;
        var info = _index.GetInfo(lower);
        return info.Tag?.Contains("impr", StringComparison.Ordinal) == true;
    }

    // ── «по русски», «по новому» ───────────────────────────────────────────────────────────

    private bool PoAdverb(string text, List<RussianToken> tokens, int i, ICollection<TextIssue> issues)
    {
        if (!tokens[i].Value.Equals("по", StringComparison.OrdinalIgnoreCase)) return false;
        var word = tokens[i + 1];
        var lower = word.Value.ToLowerInvariant();
        var candidate = "по-" + lower;
        if (!_index.Contains(candidate) || _index.GetInfo(candidate).PartOfSpeech != RussianPartOfSpeech.Adverb) return false;

        // «по новому плану», «по русскому полю»: a preposition with its noun phrase.
        if (_index.Contains(lower) && i + 2 < tokens.Count && SingleSpaceBetween(text, word, tokens[i + 2]))
        {
            var after = _index.GetInfo(tokens[i + 2].Value.ToLowerInvariant());
            if (after.PartOfSpeech is RussianPartOfSpeech.Noun or RussianPartOfSpeech.AdjectiveFull
                    or RussianPartOfSpeech.Pronoun or RussianPartOfSpeech.Numeral
                && after.Tag?.Contains("datv", StringComparison.Ordinal) != false)
            {
                return false;
            }
        }

        var original = text[tokens[i].Start..word.End];
        var replacement = RussianTokens.MatchLeadingCase(tokens[i].Value, candidate);
        issues.Add(Hyphen(tokens[i].Start, original, replacement,
            $"«{candidate}» — наречие; наречия с приставкой «по-» на «-ому», «-ему», «-ски», «-ьи» пишутся через дефис.",
            "ru.spelling.po-adverb"));
        return true;
    }

    private void PoAdverbWrittenSolid(
        string text,
        List<RussianToken> tokens,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        foreach (var token in tokens)
        {
            var lower = token.Value.ToLowerInvariant();
            if (lower.Length < 6 || !lower.StartsWith("по", StringComparison.Ordinal) || lower.Contains('-')) continue;
            if (_index.Contains(lower)) continue;
            var candidate = "по-" + lower[2..];
            if (!_index.Contains(candidate) || _index.GetInfo(candidate).PartOfSpeech != RussianPartOfSpeech.Adverb) continue;
            if (ProtectedTextSpans.Overlaps(token.Start, token.Length, protectedSpans)) continue;
            issues.Add(Hyphen(token.Start, token.Value, RussianTokens.MatchLeadingCase(token.Value, candidate),
                $"«{candidate}» пишется через дефис.", "ru.spelling.po-adverb"));
        }
    }

    // ── «пол часа» → «полчаса», «пол лимона» → «пол-лимона» ─────────────────────────────────

    private static readonly HashSet<string> PolNouns = new(StringComparer.OrdinalIgnoreCase)
    {
        "часа", "дня", "года", "века", "месяца", "недели", "минуты", "секунды", "суток", "ночи", "утра", "лета", "зимы",
        "литра", "килограмма", "кило", "метра", "километра", "сантиметра", "стакана", "ложки", "чашки", "бутылки",
        "банки", "пачки", "страницы", "книги", "мира", "города", "страны", "жизни", "пути", "дороги", "класса",
        "лимона", "яблока", "апельсина", "арбуза", "дыни", "торта", "пиццы", "батона", "буханки", "огурца",
        "второго", "третьего", "четвёртого", "четвертого", "пятого", "шестого", "седьмого", "восьмого",
        "девятого", "десятого", "одиннадцатого", "двенадцатого", "первого",
    };

    private static readonly HashSet<string> FloorPrepositions = new(StringComparer.OrdinalIgnoreCase)
    {
        "на", "под", "по", "через", "о", "об", "с", "со", "за", "в", "во", "у", "до", "от",
    };

    private bool Pol(string text, List<RussianToken> tokens, int i, ICollection<TextIssue> issues)
    {
        if (!tokens[i].Value.Equals("пол", StringComparison.OrdinalIgnoreCase)) return false;
        var noun = tokens[i + 1];
        if (!PolNouns.Contains(noun.Value) && !IsCapitalisedGenitive(noun.Value)) return false;
        if (i > 0 && FloorPrepositions.Contains(tokens[i - 1].Value)) return false;

        var first = noun.Value[0];
        var hyphen = char.IsUpper(first) || "аеёиоуыэюяАЕЁИОУЫЭЮЯлЛ".Contains(first);
        var joined = tokens[i].Value + (hyphen ? "-" : string.Empty) + noun.Value;
        var original = text[tokens[i].Start..noun.End];
        issues.Add(new TextIssue(
            tokens[i].Start,
            original.Length,
            original,
            joined,
            hyphen ? "«Пол-» пишется через дефис" : "«Пол-» пишется слитно",
            hyphen
                ? "«Пол» (половина) пишется через дефис перед гласной, «л» и заглавной буквой: «пол-лимона», «пол-яблока», «пол-Москвы»."
                : "«Пол» (половина) с существительным в родительном падеже пишется слитно: «полчаса», «полдня», «полкниги».",
            IssueCategory.Orthography,
            IssueSeverity.Error,
            CanApplyAutomatically: true,
            RuleId: "ru.spelling.pol",
            LinguisticCategory: LinguisticIssueCategory.JoinedOrSeparateSpelling,
            Confidence: 0.9));
        return true;
    }

    private bool IsCapitalisedGenitive(string word) =>
        char.IsUpper(word[0]) && _index.GetInfo(word.ToLowerInvariant()).Tag?.Contains("gent", StringComparison.Ordinal) == true;

    // ── «северо западный», «вице президент», «кое что», «из за» ─────────────────────────────

    /// <summary>First halves that are function words: their pairs are handled above or are legitimately separate.</summary>
    private static readonly HashSet<string> NeverJoinFirst = new(StringComparer.OrdinalIgnoreCase)
    {
        "что", "кто", "как", "где", "когда", "куда", "откуда", "почему", "зачем", "чей", "какой", "сколько",
        "так", "вот", "там", "тут", "это", "то", "все", "всё", "по", "пол", "не", "ни", "и", "а", "но", "да",
        "в", "во", "на", "с", "со", "к", "ко", "у", "о", "об", "от", "до", "за", "под", "над", "при", "про", "для",
        "он", "она", "оно", "они", "мы", "вы", "я", "ты",
    };

    /// <summary>Prefixes that only ever attach with a hyphen: «вице-президент», «экс-чемпион».</summary>
    private static readonly HashSet<string> ForeignPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "вице", "экс", "лейб", "обер", "унтер", "штаб", "контр", "пресс", "блиц", "бизнес", "интернет", "веб",
    };

    private static readonly HashSet<string> NeverJoinSecond = new(StringComparer.OrdinalIgnoreCase)
    {
        "то", "либо", "нибудь", "ка", "таки", "де", "с", "тка",
    };

    private void HyphenatedCompound(string text, List<RussianToken> tokens, int i, ICollection<TextIssue> issues)
    {
        var a = tokens[i];
        var b = tokens[i + 1];
        var la = a.Value.ToLowerInvariant();
        var lb = b.Value.ToLowerInvariant();
        if (la.Contains('-') || lb.Contains('-') || NeverJoinSecond.Contains(lb)) return;
        if (NeverJoinFirst.Contains(la) && !(la == "из" && lb is "за" or "под") && !(la == "кое")) return;

        var joined = la + "-" + lb;
        if (!_index.Contains(joined)) return;

        // Two ordinary words that happen to form a compound are only joined when the first one
        // cannot stand alone in that position: «северо», «вице», «давным», «кое» are not free
        // words, while «интернет магазин» is joined because the dictionary records the compound
        // and the pair has no reading as two separate words that a noun-noun sequence would need.
        var firstIsWord = _index.Contains(la) && !ForeignPrefixes.Contains(la);
        if (firstIsWord)
        {
            var infoA = _index.GetInfo(la);
            var infoB = _index.GetInfo(lb);
            var nounNoun = infoA.PartOfSpeech == RussianPartOfSpeech.Noun && infoB.PartOfSpeech == RussianPartOfSpeech.Noun
                           && infoA.Tag?.Contains("nomn", StringComparison.Ordinal) == true;
            var colourOrDirection = infoA.PartOfSpeech is RussianPartOfSpeech.AdjectiveShort or RussianPartOfSpeech.Adverb
                                    && infoB.PartOfSpeech is RussianPartOfSpeech.AdjectiveFull;
            var reduplication = la == lb || (la.Length > 3 && lb.StartsWith(la[..3], StringComparison.Ordinal));
            var prepositions = la == "из" || la == "кое";
            if (!nounNoun && !colourOrDirection && !reduplication && !prepositions) return;
        }

        var original = text[a.Start..b.End];
        var replacement = RussianTokens.MatchLeadingCase(a.Value, joined);
        issues.Add(Hyphen(a.Start, original, replacement,
            $"«{joined}» — одно слово, оно пишется через дефис.", "ru.spelling.hyphen-compound"));
    }

    private static TextIssue Hyphen(int start, string original, string replacement, string explanation, string ruleId) =>
        new(start,
            original.Length,
            original,
            replacement,
            "Пишется через дефис",
            explanation,
            IssueCategory.Orthography,
            IssueSeverity.Error,
            CanApplyAutomatically: true,
            RuleId: ruleId,
            LinguisticCategory: LinguisticIssueCategory.JoinedOrSeparateSpelling,
            Confidence: 0.92);

    private static bool SingleSpaceBetween(string text, RussianToken left, RussianToken right)
    {
        var gap = right.Start - left.End;
        if (gap < 1 || gap > 2) return false;
        for (var k = left.End; k < right.Start; k++)
        {
            if (text[k] != ' ') return false;
        }

        return true;
    }
}
