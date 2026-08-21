using WriteLite.Language.Russian;
using WriteLite.Models;

namespace WriteLite.Services.Grammar;

/// <summary>
/// Обращение after a greeting: «Привет дорогой друг» → «Привет, дорогой друг».
/// </summary>
/// <remarks>
/// <para>Russian separates a form of address from the rest of the sentence with a comma, and
/// the position where it is most often left out — and most safely detectable — is directly
/// after a greeting or an interjection at the start of a sentence.</para>
///
/// <para><b>The gate is the whole rule.</b> A comma is proposed only when a closed-list
/// opener is followed by a noun phrase whose head is an animate nominative noun, a personal
/// name, or one of a short list of collective addresses. Everything else is left alone,
/// because the alternative readings are ordinary sentences: «Привет из Москвы», «Спасибо
/// большое», «Добрый вечер начался хорошо».</para>
///
/// <para><b>Nominative is required even for names, and that is not redundant.</b> The form
/// index resolves «из» to a personal name — presumably a folded «Иза» — with the proper-name
/// flag set. Accepting a proper name without also requiring the nominative would turn
/// «Привет из Москвы» into «Привет, из Москвы». The genitive tag on that reading is what
/// stops it.</para>
///
/// <para><b>The collective list exists because the index collapses homonymy.</b> «коллеги» is
/// recorded as the genitive singular of «коллега» rather than the nominative plural, so the
/// tag gate rejects «Добрый вечер коллеги» — a correct finding lost to a data artefact. A
/// short closed list of forms that are addresses whenever they follow a greeting restores
/// them without loosening the gate for anything else.</para>
/// </remarks>
public sealed class RussianVocativeAnalyzer
{
    private readonly RussianFormIndex _index;

    public RussianVocativeAnalyzer(RussianFormIndex index) => _index = index;

    public static RussianVocativeAnalyzer? TryCreate(RussianFormIndex? index)
        => index is null ? null : new RussianVocativeAnalyzer(index);

    /// <summary>
    /// Openers, longest first so «добрый вечер» is preferred over a bare «добрый».
    /// </summary>
    /// <remarks>
    /// Each entry is an interjection or a greeting formula that is not a sentence member, so
    /// what follows it can only be an address. Deliberately absent: «уважаемый», which is an
    /// adjective modifying the address rather than an opener, and «дорогой», for the same
    /// reason — both are handled as modifiers inside the phrase instead.
    /// </remarks>
    private static readonly string[] Openers =
    [
        "добрый день", "добрый вечер", "доброе утро", "доброй ночи",
        "здравствуйте", "здравствуй", "приветствую",
        "послушайте", "послушай", "слушайте", "слушай",
        "извините", "извини", "простите", "прости",
        "спасибо", "привет", "здорово",
    ];

    /// <summary>
    /// Collective addresses the index cannot be asked about, because their recorded analysis
    /// is a different form of the same lemma. See the class remarks.
    /// </summary>
    private static readonly HashSet<string> CollectiveAddresses = new(StringComparer.OrdinalIgnoreCase)
    {
        "коллеги", "друзья", "ребята", "господа", "товарищи", "дамы", "парни", "девушки",
    };

    /// <summary>Adjective and participle endings that can modify an address.</summary>
    private static readonly string[] ModifierEndings =
    [
        "ый", "ий", "ой", "ая", "яя", "ое", "ее", "ые", "ие", "ый", "ыe",
    ];

    /// <summary>The most modifiers accepted before the head of the address.</summary>
    private const int MaxModifiers = 2;

    public void Collect(
        string text,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var tokens = RussianTokens.Split(text);
        if (tokens.Count < 2) return;

        for (var i = 0; i < tokens.Count; i++)
        {
            if (!RussianTokens.IsSentenceStart(text, tokens[i].Start)) continue;

            var opener = MatchOpener(text, tokens, i);
            if (opener is null) continue;

            var (openerEnd, lastOpenerToken) = opener.Value;

            // A comma, a dash or anything else already separating the opener means the writer
            // has already made the decision this rule is about.
            if (openerEnd < text.Length && text[openerEnd] != ' ') continue;

            var address = ResolveAddress(text, tokens, lastOpenerToken, lastOpenerToken + 1);
            if (address is null) continue;

            if (ProtectedTextSpans.Overlaps(tokens[i].Start, address.Value.End - tokens[i].Start, protectedSpans))
            {
                continue;
            }

            var openerText = text[tokens[i].Start..openerEnd];
            issues.Add(new TextIssue(
                tokens[i].Start,
                openerEnd - tokens[i].Start,
                openerText,
                openerText + ",",
                "Обращение выделяется запятой",
                $"«{address.Value.Text}» — обращение; оно не является членом предложения "
                + "и отделяется запятой.",
                IssueCategory.Punctuation,
                IssueSeverity.Warning,
                CanApplyAutomatically: true,
                RuleId: "ru.punctuation.vocative-comma",
                LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation,
                Confidence: 0.9));

            CollectSentenceBoundary(text, address.Value.End, protectedSpans, issues);
        }
    }

    /// <summary>
    /// Greeting questions running on from the address: «…дорогой друг как твои дела» →
    /// «…дорогой друг! Как твои дела?».
    /// </summary>
    /// <remarks>
    /// <para>§11 of the brief asks WriteLite to notice when a punctuation error is larger than
    /// a missing comma, and to be conservative about it. Both halves matter: proposing a
    /// second comma here would be wrong, and splitting every long sentence would be worse than
    /// proposing nothing.</para>
    ///
    /// <para>The conservatism is a closed list of greeting questions rather than a general
    /// rule about interrogative words. «как» after an address is not reliably a question —
    /// «Спасибо, Алексей, как всегда» is an ordinary comparison — and separating the two
    /// readings needs to know what follows «как», which is exactly what the list encodes. It
    /// covers the greeting formulas people actually run together and nothing else.</para>
    ///
    /// <para>Never auto-applied. Inserting a sentence boundary changes more of the writer's
    /// text than any other finding this pipeline produces, and §12 puts an uncertain
    /// interpretation on the suggestion side of the line.</para>
    /// </remarks>
    private static void CollectSentenceBoundary(
        string text,
        int addressEnd,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        ICollection<TextIssue> issues)
    {
        var cursor = addressEnd;
        while (cursor < text.Length && text[cursor] == ' ') cursor++;
        if (cursor >= text.Length || cursor == addressEnd) return;

        var remainder = text[cursor..];
        var question = GreetingQuestions.FirstOrDefault(
            q => remainder.StartsWith(q, StringComparison.OrdinalIgnoreCase));
        if (question is null) return;

        var end = cursor + question.Length;

        // The question must *end* the clause. Checking only that the next character is not a
        // letter was not enough: «как дела компании идут» matched «как дела» and left the rest
        // of the clause behind a question mark. What follows must be the end of the text, a
        // comma, or a terminator — anything else means the match was a prefix of a longer
        // clause this rule knows nothing about.
        var after = end;
        while (after < text.Length && text[after] == ' ') after++;
        if (after < text.Length && text[after] != ',' && !RussianTokens.IsTerminator(text[after])) return;

        // A comma directly after would survive into «…дела?, я был…», so it is consumed.
        if (end < text.Length && text[end] == ',') end++;

        if (ProtectedTextSpans.Overlaps(addressEnd, end - addressEnd, protectedSpans)) return;

        var original = text[addressEnd..end];
        var matched = text[cursor..(cursor + question.Length)];
        var replacement = "! " + RussianTokens.Capitalize(matched) + "?";

        issues.Add(new TextIssue(
            addressEnd,
            end - addressEnd,
            original,
            replacement,
            "Здесь заканчивается предложение",
            "После приветствия с обращением начинается новый вопрос: приветствие закрывается "
            + "восклицательным знаком, а вопрос — вопросительным.",
            IssueCategory.Punctuation,
            IssueSeverity.Suggestion,
            CanApplyAutomatically: false,
            RuleId: "ru.punctuation.greeting-sentence-boundary",
            LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation,
            Confidence: 0.75));
    }

    /// <summary>
    /// Greeting questions, longest first so the fuller form wins the prefix match.
    /// </summary>
    private static readonly string[] GreetingQuestions =
    [
        "как твои дела", "как ваши дела", "как у тебя дела", "как у вас дела",
        "как поживаешь", "как поживаете", "как настроение", "что нового",
        "как дела", "как жизнь", "как ты", "как вы",
    ];

    /// <summary>The opener at <paramref name="index"/>, as (end offset, last token index).</summary>
    private static (int End, int LastToken)? MatchOpener(string text, List<RussianToken> tokens, int index)
    {
        foreach (var opener in Openers)
        {
            var words = opener.Split(' ');
            if (index + words.Length > tokens.Count) continue;

            var matched = true;
            for (var w = 0; w < words.Length; w++)
            {
                if (!tokens[index + w].Value.Equals(words[w], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }

                if (w > 0 && !RussianTokens.OnlySpacesBetween(text, tokens[index + w - 1], tokens[index + w]))
                {
                    matched = false;
                    break;
                }
            }

            if (matched) return (tokens[index + words.Length - 1].End, index + words.Length - 1);
        }

        return null;
    }

    /// <summary>The address phrase starting at <paramref name="first"/>, or null.</summary>
    /// <remarks>
    /// <paramref name="openerToken"/> is passed in so the gap between the opener and the first
    /// word of the address is checked like every other gap. Without it «Привет — дорогой друг»
    /// was read as an unseparated address and offered a second separator.
    /// </remarks>
    private (string Text, int End)? ResolveAddress(
        string text,
        List<RussianToken> tokens,
        int openerToken,
        int first)
    {
        if (first >= tokens.Count) return null;
        if (!RussianTokens.OnlySpacesBetween(text, tokens[openerToken], tokens[first])) return null;

        for (var modifiers = 0; modifiers <= MaxModifiers; modifiers++)
        {
            var headIndex = first + modifiers;
            if (headIndex >= tokens.Count) break;

            // Contiguity across the whole phrase, including from the opener.
            var contiguous = true;
            for (var k = first; k < headIndex; k++)
            {
                if (!RussianTokens.OnlySpacesBetween(text, tokens[k], tokens[k + 1])) { contiguous = false; break; }
            }

            if (!contiguous) break;

            var modifiersOk = true;
            for (var k = first; k < headIndex; k++)
            {
                if (!IsModifier(tokens[k].Value)) { modifiersOk = false; break; }
            }

            if (!modifiersOk) break;

            if (!IsAddressHead(tokens[headIndex].Value)) continue;

            return (text[tokens[first].Start..tokens[headIndex].End], tokens[headIndex].End);
        }

        return null;
    }

    /// <summary>A word that can stand between the greeting and the address.</summary>
    private bool IsModifier(string word)
    {
        var lower = word.ToLowerInvariant();
        if (lower.Length < 4) return false;
        if (!ModifierEndings.Any(e => lower.EndsWith(e, StringComparison.Ordinal))) return false;

        // Must be a word at all — an unknown token is a typo, not a modifier, and the
        // spelling layer owns it.
        if (!_index.Contains(lower)) return false;

        // An index-recognised verb ending in -ий/-ой is not a modifier of an address.
        var info = _index.GetInfo(lower);
        return info.PartOfSpeech is RussianPartOfSpeech.AdjectiveFull
            or RussianPartOfSpeech.ParticipleFull
            or RussianPartOfSpeech.Adverb
            or RussianPartOfSpeech.Noun
            or RussianPartOfSpeech.Unknown;
    }

    /// <summary>An animate nominative noun, a personal name, or a collective address.</summary>
    private bool IsAddressHead(string word)
    {
        if (CollectiveAddresses.Contains(word)) return true;

        var info = _index.GetInfo(word.ToLowerInvariant());
        if (info.PartOfSpeech != RussianPartOfSpeech.Noun) return false;
        if (!info.Tag.Contains("nomn", StringComparison.Ordinal)) return false;

        return info.Tag.Contains("anim", StringComparison.Ordinal)
            && !info.Tag.Contains("inan", StringComparison.Ordinal);
    }
}
