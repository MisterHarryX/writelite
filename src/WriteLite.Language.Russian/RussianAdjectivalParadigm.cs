namespace WriteLite.Language.Russian;

/// <summary>
/// The declension of everything in Russian that agrees with a noun by ending: full
/// adjectives, full participles, ordinal numerals and the adjectival pronouns.
/// </summary>
/// <remarks>
/// <para>One table, used in both directions. Read forwards it answers "which slots can this
/// ending fill", which is how a modifier is analysed; read backwards it answers "what is this
/// word in the dative plural", which is how a correction is produced. Keeping both directions
/// in one table is what stops the analyser and the generator from disagreeing — a rule that
/// detects a mismatch it cannot repair reports errors and offers nothing.</para>
///
/// <para><b>Hard and soft are separated by the ending actually written, not by the stem.</b>
/// Deciding softness from the stem means deciding whether «хороший» is soft — it is not, it is
/// a velar stem spelt with «и» by the к/г/х rule — and whether «синий» is, which it is. Both
/// take the same endings, so the distinction the generator needs is the one the surface form
/// already shows. Velar and hushing stems land in the soft column and come out correct,
/// because the spelling rule that put them there governs the rest of their paradigm too.</para>
///
/// <para><b>Everything generated is checked against the form index before it is offered.</b>
/// The table cannot know that «задачей» is spelt with «е» and «свечой» with «о» — that is
/// stress, which is not recorded. Generating both and keeping the one the index contains gets
/// the answer without modelling stress at all, and a slot where neither candidate exists
/// produces nothing.</para>
/// </remarks>
internal static class RussianAdjectivalParadigm
{
    /// <summary>Endings longest first, so «ого» is never matched as «о».</summary>
    private static readonly (string Ending, bool Soft, RuReading[] Readings)[] Endings = BuildEndings();

    private static (string, bool, RuReading[])[] BuildEndings()
    {
        var table = new List<(string, bool, RuReading[])>
        {
            // ---- hard ----------------------------------------------------
            ("ыми", false, [new(RuCase.Ins, RuNumber.Plur, RuGender.None)]),
            ("ого", false,
            [
                new(RuCase.Gen, RuNumber.Sing, RuGender.Masc),
                new(RuCase.Gen, RuNumber.Sing, RuGender.Neut),
                new(RuCase.Acc, RuNumber.Sing, RuGender.Masc),
            ]),
            ("ому", false,
            [
                new(RuCase.Dat, RuNumber.Sing, RuGender.Masc),
                new(RuCase.Dat, RuNumber.Sing, RuGender.Neut),
            ]),
            ("ый", false,
            [
                new(RuCase.Nom, RuNumber.Sing, RuGender.Masc),
                new(RuCase.Acc, RuNumber.Sing, RuGender.Masc),
            ]),
            ("ой", false,
            [
                // «большой» — a stressed masculine nominative — and the whole oblique
                // feminine singular share this ending. Both readings are listed: leaving
                // either out would make a correct phrase look like an error.
                new(RuCase.Nom, RuNumber.Sing, RuGender.Masc),
                new(RuCase.Acc, RuNumber.Sing, RuGender.Masc),
                new(RuCase.Gen, RuNumber.Sing, RuGender.Fem),
                new(RuCase.Dat, RuNumber.Sing, RuGender.Fem),
                new(RuCase.Ins, RuNumber.Sing, RuGender.Fem),
                new(RuCase.Loc, RuNumber.Sing, RuGender.Fem),
            ]),
            ("ая", false, [new(RuCase.Nom, RuNumber.Sing, RuGender.Fem)]),
            ("ую", false, [new(RuCase.Acc, RuNumber.Sing, RuGender.Fem)]),
            ("ою", false, [new(RuCase.Ins, RuNumber.Sing, RuGender.Fem)]),
            ("ое", false,
            [
                new(RuCase.Nom, RuNumber.Sing, RuGender.Neut),
                new(RuCase.Acc, RuNumber.Sing, RuGender.Neut),
            ]),
            ("ые", false,
            [
                new(RuCase.Nom, RuNumber.Plur, RuGender.None),
                new(RuCase.Acc, RuNumber.Plur, RuGender.None),
            ]),
            ("ых", false,
            [
                new(RuCase.Gen, RuNumber.Plur, RuGender.None),
                new(RuCase.Loc, RuNumber.Plur, RuGender.None),
                new(RuCase.Acc, RuNumber.Plur, RuGender.None),
            ]),
            ("ым", false,
            [
                new(RuCase.Ins, RuNumber.Sing, RuGender.Masc),
                new(RuCase.Ins, RuNumber.Sing, RuGender.Neut),
                new(RuCase.Dat, RuNumber.Plur, RuGender.None),
            ]),
            ("ом", false,
            [
                new(RuCase.Loc, RuNumber.Sing, RuGender.Masc),
                new(RuCase.Loc, RuNumber.Sing, RuGender.Neut),
            ]),

            // ---- soft, velar and hushing ---------------------------------
            ("ими", true, [new(RuCase.Ins, RuNumber.Plur, RuGender.None)]),
            ("его", true,
            [
                new(RuCase.Gen, RuNumber.Sing, RuGender.Masc),
                new(RuCase.Gen, RuNumber.Sing, RuGender.Neut),
                new(RuCase.Acc, RuNumber.Sing, RuGender.Masc),
            ]),
            ("ему", true,
            [
                new(RuCase.Dat, RuNumber.Sing, RuGender.Masc),
                new(RuCase.Dat, RuNumber.Sing, RuGender.Neut),
            ]),
            ("ий", true,
            [
                new(RuCase.Nom, RuNumber.Sing, RuGender.Masc),
                new(RuCase.Acc, RuNumber.Sing, RuGender.Masc),
            ]),
            ("яя", true, [new(RuCase.Nom, RuNumber.Sing, RuGender.Fem)]),
            ("юю", true, [new(RuCase.Acc, RuNumber.Sing, RuGender.Fem)]),
            ("ею", true, [new(RuCase.Ins, RuNumber.Sing, RuGender.Fem)]),
            ("ей", true,
            [
                new(RuCase.Gen, RuNumber.Sing, RuGender.Fem),
                new(RuCase.Dat, RuNumber.Sing, RuGender.Fem),
                new(RuCase.Ins, RuNumber.Sing, RuGender.Fem),
                new(RuCase.Loc, RuNumber.Sing, RuGender.Fem),
            ]),
            ("ее", true,
            [
                new(RuCase.Nom, RuNumber.Sing, RuGender.Neut),
                new(RuCase.Acc, RuNumber.Sing, RuGender.Neut),
            ]),
            ("ие", true,
            [
                new(RuCase.Nom, RuNumber.Plur, RuGender.None),
                new(RuCase.Acc, RuNumber.Plur, RuGender.None),
            ]),
            ("их", true,
            [
                new(RuCase.Gen, RuNumber.Plur, RuGender.None),
                new(RuCase.Loc, RuNumber.Plur, RuGender.None),
                new(RuCase.Acc, RuNumber.Plur, RuGender.None),
            ]),
            ("им", true,
            [
                new(RuCase.Ins, RuNumber.Sing, RuGender.Masc),
                new(RuCase.Ins, RuNumber.Sing, RuGender.Neut),
                new(RuCase.Dat, RuNumber.Plur, RuGender.None),
            ]),
            ("ем", true,
            [
                new(RuCase.Loc, RuNumber.Sing, RuGender.Masc),
                new(RuCase.Loc, RuNumber.Sing, RuGender.Neut),
            ]),
        };

        return [.. table.OrderByDescending(e => e.Item1.Length)];
    }

    /// <summary>Splits an adjectival form into stem and slots, or returns false.</summary>
    public static bool TrySplit(
        string word,
        out string stem,
        out bool soft,
        out IReadOnlyList<RuReading> readings)
    {
        foreach (var (ending, isSoft, slots) in Endings)
        {
            if (word.Length <= ending.Length + 1) continue;
            if (!word.EndsWith(ending, StringComparison.Ordinal)) continue;

            stem = word[..^ending.Length];
            soft = isSoft;
            readings = slots;
            return true;
        }

        stem = string.Empty;
        soft = false;
        readings = [];
        return false;
    }

    /// <summary>
    /// The candidate spellings of one slot for a stem, best first.
    /// </summary>
    /// <remarks>
    /// More than one candidate is produced wherever Russian orthography chooses between
    /// letters on grounds this table does not model — «и» after velars and hushing consonants,
    /// «е» for unstressed «о» after hushing. The caller keeps whichever the form index knows,
    /// which is how «хорошим» rather than «хорошым» comes out of a hard-column stem.
    /// </remarks>
    public static IEnumerable<string> Generate(string stem, bool soft, RuReading target)
    {
        foreach (var candidate in Column(stem, soft, target)) yield return candidate;

        // The opposite column as a fallback: velar, hushing and «ц» stems take hard endings
        // in some slots and soft-looking ones in others, and which is which is a spelling rule
        // about the stem's last consonant rather than a property of the paradigm.
        foreach (var candidate in Column(stem, !soft, target)) yield return candidate;
    }

    private static IEnumerable<string> Column(string stem, bool soft, RuReading target)
    {
        foreach (var (ending, isSoft, slots) in Endings)
        {
            if (isSoft != soft) continue;

            var matches = false;
            foreach (var slot in slots)
            {
                if (slot == target) { matches = true; break; }
            }

            if (matches) yield return stem + ending;
        }
    }
}
