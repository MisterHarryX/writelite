namespace WriteLite.Language.Russian;

/// <summary>
/// The determiners that agree with a noun but do not decline like an adjective.
/// </summary>
/// <remarks>
/// <para>«этот», «тот», «весь», «мой», «наш», «один» and their relatives carry the same
/// agreement obligations as an adjective and are involved in a large share of real agreement
/// errors — «Эта фильм», «на моём кровати», «к своей другу». Their nominatives and several
/// oblique slots are irregular enough that pushing them through the adjectival table produces
/// «моя» from a feminine-nominative rule that wants «мояя», so they are written out.</para>
///
/// <para>The list is closed and short by construction: every determiner that <em>does</em>
/// decline adjectivally — «каждый», «любой», «который», «какой», «самый», «другой», «такой» —
/// is deliberately absent, because the adjectival table already handles it and a second
/// definition is a second thing to keep correct.</para>
///
/// <para>The tables are exhaustive per lemma, so a form found here needs no index check and a
/// form absent from here is genuinely not that lemma. That is what lets the analyser trust a
/// pronoun reading as strongly as it trusts a closed rule.</para>
/// </remarks>
internal static class RussianPronounParadigm
{
    private static readonly Dictionary<string, (string Form, RuReading Reading)[]> Tables = Build();

    private static readonly Dictionary<string, string> FormToLemma = BuildReverse();

    /// <summary>The lemma this surface form belongs to, or null.</summary>
    public static string? LemmaOf(string word)
        => FormToLemma.TryGetValue(word, out var lemma) ? lemma : null;

    /// <summary>Every slot this surface form fills, across all tabled lemmas.</summary>
    public static IReadOnlyList<RuReading> Analyse(string word)
    {
        if (!FormToLemma.TryGetValue(word, out var lemma)) return [];

        var readings = new List<RuReading>(4);
        foreach (var (form, reading) in Tables[lemma])
        {
            if (!string.Equals(form, word, StringComparison.Ordinal)) continue;
            if (!readings.Contains(reading)) readings.Add(reading);
        }

        return readings;
    }

    /// <summary>The form of <paramref name="lemma"/> in one slot, or null.</summary>
    public static string? Inflect(string lemma, RuReading target)
    {
        if (!Tables.TryGetValue(lemma, out var table)) return null;

        foreach (var (form, reading) in table)
        {
            if (reading == target) return form;
        }

        // Plural slots do not mark gender; a caller carrying one through from a singular
        // context must still find the plural form.
        if (target.Number == RuNumber.Plur)
        {
            foreach (var (form, reading) in table)
            {
                if (reading.Number == RuNumber.Plur && reading.Case == target.Case) return form;
            }
        }

        return null;
    }

    private static Dictionary<string, string> BuildReverse()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (lemma, table) in Tables)
        {
            foreach (var (form, _) in table)
            {
                // «то» belongs to «тот» and «это» to «этот»; first lemma wins and the other
                // readings are still reachable through the winner's own table when they
                // coincide. Nothing here depends on the losing lemma.
                map.TryAdd(form, lemma);
            }
        }

        return map;
    }

    private static Dictionary<string, (string, RuReading)[]> Build()
    {
        var result = new Dictionary<string, (string, RuReading)[]>(StringComparer.Ordinal);

        Add(result, "этот", "этот", "этого", "этому", "этим", "этом",
            "эта", "этой", "эту", "этою", "это", "эти", "этих", "этими");
        Add(result, "тот", "тот", "того", "тому", "тем", "том",
            "та", "той", "ту", "тою", "то", "те", "тех", "теми");
        Add(result, "весь", "весь", "всего", "всему", "всем", "всём",
            "вся", "всей", "всю", "всею", "всё", "все", "всех", "всеми");
        Add(result, "мой", "мой", "моего", "моему", "моим", "моём",
            "моя", "моей", "мою", "моею", "моё", "мои", "моих", "моими");
        Add(result, "твой", "твой", "твоего", "твоему", "твоим", "твоём",
            "твоя", "твоей", "твою", "твоею", "твоё", "твои", "твоих", "твоими");
        Add(result, "свой", "свой", "своего", "своему", "своим", "своём",
            "своя", "своей", "свою", "своею", "своё", "свои", "своих", "своими");
        Add(result, "наш", "наш", "нашего", "нашему", "нашим", "нашем",
            "наша", "нашей", "нашу", "нашею", "наше", "наши", "наших", "нашими");
        Add(result, "ваш", "ваш", "вашего", "вашему", "вашим", "вашем",
            "ваша", "вашей", "вашу", "вашею", "ваше", "ваши", "ваших", "вашими");
        Add(result, "чей", "чей", "чьего", "чьему", "чьим", "чьём",
            "чья", "чьей", "чью", "чьею", "чьё", "чьи", "чьих", "чьими");
        Add(result, "сам", "сам", "самого", "самому", "самим", "самом",
            "сама", "самой", "саму", "самою", "само", "сами", "самих", "самими");
        Add(result, "один", "один", "одного", "одному", "одним", "одном",
            "одна", "одной", "одну", "одною", "одно", "одни", "одних", "одними");

        return result;
    }

    /// <summary>
    /// One determiner, given in the fixed slot order the tables share.
    /// </summary>
    /// <remarks>
    /// Positional rather than named because every one of these paradigms has exactly these
    /// fourteen distinct spellings in exactly this order, and a named form would repeat the
    /// slot labels eleven times for no additional safety. The accusative is derived: masculine
    /// and plural accusatives are ambiguous between the nominative and genitive spellings by
    /// animacy, and both readings are recorded so that neither an animate nor an inanimate
    /// head can produce a false mismatch.
    /// </remarks>
    private static void Add(
        Dictionary<string, (string, RuReading)[]> into,
        string lemma,
        string mNom, string mGen, string mDat, string mIns, string mLoc,
        string fNom, string fObl, string fAcc, string fIns,
        string nNom,
        string pNom, string pGen, string pIns)
    {
        var forms = new List<(string, RuReading)>
        {
            (mNom, new RuReading(RuCase.Nom, RuNumber.Sing, RuGender.Masc)),
            (mNom, new RuReading(RuCase.Acc, RuNumber.Sing, RuGender.Masc)),
            (mGen, new RuReading(RuCase.Gen, RuNumber.Sing, RuGender.Masc)),
            (mGen, new RuReading(RuCase.Gen, RuNumber.Sing, RuGender.Neut)),
            (mGen, new RuReading(RuCase.Acc, RuNumber.Sing, RuGender.Masc)),
            (mDat, new RuReading(RuCase.Dat, RuNumber.Sing, RuGender.Masc)),
            (mDat, new RuReading(RuCase.Dat, RuNumber.Sing, RuGender.Neut)),
            (mIns, new RuReading(RuCase.Ins, RuNumber.Sing, RuGender.Masc)),
            (mIns, new RuReading(RuCase.Ins, RuNumber.Sing, RuGender.Neut)),
            (mIns, new RuReading(RuCase.Dat, RuNumber.Plur, RuGender.None)),
            (mLoc, new RuReading(RuCase.Loc, RuNumber.Sing, RuGender.Masc)),
            (mLoc, new RuReading(RuCase.Loc, RuNumber.Sing, RuGender.Neut)),

            (fNom, new RuReading(RuCase.Nom, RuNumber.Sing, RuGender.Fem)),
            (fObl, new RuReading(RuCase.Gen, RuNumber.Sing, RuGender.Fem)),
            (fObl, new RuReading(RuCase.Dat, RuNumber.Sing, RuGender.Fem)),
            (fObl, new RuReading(RuCase.Ins, RuNumber.Sing, RuGender.Fem)),
            (fObl, new RuReading(RuCase.Loc, RuNumber.Sing, RuGender.Fem)),
            (fAcc, new RuReading(RuCase.Acc, RuNumber.Sing, RuGender.Fem)),
            (fIns, new RuReading(RuCase.Ins, RuNumber.Sing, RuGender.Fem)),

            (nNom, new RuReading(RuCase.Nom, RuNumber.Sing, RuGender.Neut)),
            (nNom, new RuReading(RuCase.Acc, RuNumber.Sing, RuGender.Neut)),

            (pNom, new RuReading(RuCase.Nom, RuNumber.Plur, RuGender.None)),
            (pNom, new RuReading(RuCase.Acc, RuNumber.Plur, RuGender.None)),
            (pGen, new RuReading(RuCase.Gen, RuNumber.Plur, RuGender.None)),
            (pGen, new RuReading(RuCase.Loc, RuNumber.Plur, RuGender.None)),
            (pGen, new RuReading(RuCase.Acc, RuNumber.Plur, RuGender.None)),
            (pIns, new RuReading(RuCase.Ins, RuNumber.Plur, RuGender.None)),
        };

        into[lemma] = [.. forms];
    }
}
