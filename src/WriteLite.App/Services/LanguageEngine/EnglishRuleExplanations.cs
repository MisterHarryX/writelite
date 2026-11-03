namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Hand-written Russian explanations for the English rules a Russian speaker meets most often.
/// </summary>
/// <remarks>
/// The engine's English messages are full of quoted fragments («Use “a” instead of ‘an’…»), which
/// a sentence-level translator mangles. For the rules below the card says what a teacher would
/// say, in Russian, with the actual words from the text; every other rule falls back to the
/// offline translation of the engine's message with the original kept beside it.
/// </remarks>
public static class EnglishRuleExplanations
{
    private static readonly Dictionary<string, (string Title, string Text)> Rules = new(StringComparer.Ordinal)
    {
        ["EN_A_VS_AN"] = ("Артикль a / an",
            "Перед словом, которое начинается с согласного звука, пишется «a», перед гласным звуком — «an»: a test, a university, an hour, an apple. Важен звук, а не буква."),
        ["PERS_PRONOUN_AGREEMENT"] = ("Глагол не согласован с местоимением",
            "Форма глагола зависит от подлежащего: I am / you are / he is, we were, they were. Здесь местоимение требует формы «{1}»."),
        ["HE_VERB_AGR"] = ("Глагол после he / she / it",
            "В настоящем времени после he, she, it к глаголу добавляется -s/-es: he goes, she works, it does. Нужно «{1}»."),
        ["SINGULAR_VERB_AFTER_PLURAL"] = ("Число глагола",
            "Подлежащее во множественном числе требует глагола во множественном числе: they are, the problems were."),
        ["THERE_RE_MANY"] = ("There is / there are",
            "Перед существительным во множественном числе пишется «there are»: there are many problems."),
        ["NON3PRS_VERB"] = ("Форма глагола",
            "После I, you, we, they окончание -s не нужно: I go, they work. Нужно «{1}»."),
        ["MORFOLOGIK_RULE_EN_US"] = ("Опечатка в английском",
            "Слова «{0}» нет в английском словаре. Скорее всего, имелось в виду «{1}»."),
        ["EN_CONTRACTION_SPELLING"] = ("Сокращение пишется с апострофом",
            "В английских сокращениях пропущенная буква заменяется апострофом: don't, doesn't, can't, it's, I'm."),
        ["IT_IS"] = ("It's или its",
            "«It's» — это сокращение «it is» («это есть»); «its» — притяжательное «его/её» (its color). Здесь нужно «{1}»."),
        ["YOUR_YOU_RE"] = ("Your или you're",
            "«You're» — сокращение «you are»; «your» — «твой/ваш»."),
        ["THEIR_IS"] = ("Their / there / they're",
            "«Their» — «их», «there» — «там/есть», «they're» — «they are»."),
        ["UPPERCASE_SENTENCE_START"] = ("Заглавная буква",
            "Английское предложение начинается с заглавной буквы."),
        ["I_LOWERCASE"] = ("Местоимение I",
            "Местоимение «I» (я) в английском всегда пишется с заглавной буквы."),
        ["ENGLISH_WORD_REPEAT_RULE"] = ("Повтор слова",
            "Слово «{0}» повторяется дважды подряд — скорее всего, второе лишнее."),
        ["MORE_COMPARATIVE"] = ("Лишнее more",
            "Сравнительная степень образуется либо окончанием -er (smarter), либо словом more (more intelligent), но не обоими сразу."),
        ["MOST_SUPERLATIVE"] = ("Лишнее most",
            "Превосходная степень — либо -est (smartest), либо most (most intelligent), но не обоими сразу."),
        ["BE_AGREE"] = ("I agree без am",
            "«Agree» — глагол, а не прилагательное: по-английски «I agree», а не «I am agree» (калька с «я согласен»)."),
        ["EN_COMPOUNDS"] = ("Слитно или через дефис",
            "Это составное слово пишется иначе: «{1}»."),
        ["COMMA_PARENTHESIS_WHITESPACE"] = ("Пробел у знака препинания",
            "Перед запятой и точкой пробел не ставится, после — ставится."),
        ["WHITESPACE_RULE"] = ("Лишний пробел",
            "Между словами нужен один пробел."),
        ["DOUBLE_PUNCTUATION"] = ("Двойной знак",
            "Знак препинания повторяется дважды."),
        ["EN_QUOTES"] = ("Кавычки", "В английском тексте используются кавычки “…” или \"…\"."),
        ["SENT_START_CONJUNCTIVE_LINKING_ADVERB_COMMA"] = ("Запятая после вводного слова",
            "После вводных слов в начале предложения (However, Therefore, Moreover) ставится запятая."),
        ["DID_BASEFORM"] = ("Глагол после did / does",
            "После did, does, didn't, doesn't глагол стоит в начальной форме: did go, doesn't know."),
        ["MANY_NN_U"] = ("Many или much",
            "«Many» — с исчисляемыми (many books), «much» — с неисчисляемыми (much time)."),
        ["FEWER_LESS"] = ("Fewer или less",
            "«Fewer» — с исчисляемыми (fewer errors), «less» — с неисчисляемыми (less time)."),
        ["AFFECT_EFFECT"] = ("Affect или effect",
            "«Affect» — глагол «влиять», «effect» — существительное «эффект, результат»."),
        ["THEN_THAN"] = ("Then или than",
            "«Than» — в сравнениях (bigger than), «then» — «тогда, потом»."),
        ["LOOSE_LOSE"] = ("Lose или loose",
            "«Lose» — «терять», «loose» — «свободный, незатянутый»."),
        ["ADVISE_VBZ"] = ("Advise или advice",
            "«Advice» — существительное «совет», «advise» — глагол «советовать»."),
    };

    /// <summary>The curated explanation for an engine rule, or null when there is none.</summary>
    public static (string Title, string Text)? Describe(string? ruleId, string original, string? replacement)
    {
        if (ruleId is null) return null;
        if (!Rules.TryGetValue(ruleId, out var entry))
        {
            // Rule ids carry numbered variants: HE_VERB_AGR, HE_VERB_AGR_2 …
            var trimmed = ruleId.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').TrimEnd('_');
            if (!Rules.TryGetValue(trimmed, out entry)) return null;
        }

        return (entry.Title, entry.Text.Replace("{0}", original).Replace("{1}", replacement ?? original));
    }
}
