namespace WriteLite.Language.Russian;

/// <summary>
/// Noun declension, generated from the lemma rather than looked up.
/// </summary>
/// <remarks>
/// <para><b>Why generate at all, when the index holds three million real forms.</b> It holds
/// them keyed by spelling, with one grammatical analysis each. Asking it "what case is
/// «инструкции»" gets one answer where the language has four, and a rule built on that answer
/// rewrites correct text — the failure this project has hit before. Generating the paradigm
/// from the lemma and keeping <em>every</em> slot the surface form fills recovers the missing
/// readings, and the index is then used for what it is reliable at: gender, animacy, lemma,
/// and whether a form the generator invented is a real word.</para>
///
/// <para><b>Over-generation is the safe direction, and only in one place.</b> When analysing,
/// an extra reading can only make two words agree that should not have — a missed error.
/// A <em>missing</em> reading makes correct text look wrong, so analysis takes every stem
/// variant and both spelling columns without filtering. When generating a replacement the
/// direction reverses: an invented form must never reach the user, so every candidate is
/// checked against the index and a slot with no surviving candidate produces nothing.</para>
///
/// <para><b>Fleeting vowels are handled by trying both stems.</b> «конец» declines on «конц-»,
/// «день» on «дн-», and which nouns do this is lexical. Both the full stem and the stem with
/// its last «о/е/ё» removed are run through the tables; the wrong one produces forms that
/// match no surface word and are filtered out by the index on the way back.</para>
/// </remarks>
internal static class RussianNounParadigm
{
    private const string Vowels = "аеёиоуыэюя";
    private const string Velar = "кгх";
    private const string Hushing = "жчшщ";

    private readonly record struct Slot(RuCase Case, RuNumber Number);

    private static readonly Slot[] AllSlots =
    [
        new(RuCase.Nom, RuNumber.Sing), new(RuCase.Gen, RuNumber.Sing), new(RuCase.Dat, RuNumber.Sing),
        new(RuCase.Acc, RuNumber.Sing), new(RuCase.Ins, RuNumber.Sing), new(RuCase.Loc, RuNumber.Sing),
        new(RuCase.Nom, RuNumber.Plur), new(RuCase.Gen, RuNumber.Plur), new(RuCase.Dat, RuNumber.Plur),
        new(RuCase.Acc, RuNumber.Plur), new(RuCase.Ins, RuNumber.Plur), new(RuCase.Loc, RuNumber.Plur),
    ];

    /// <summary>Every slot of <paramref name="lemma"/> whose spelling is <paramref name="surface"/>.</summary>
    public static IReadOnlyList<RuReading> Analyse(
        string lemma,
        string surface,
        RuGender gender,
        bool animate)
    {
        if (lemma.Length < 2 || surface.Length == 0) return [];

        var readings = new List<RuReading>(4);
        foreach (var slot in AllSlots)
        {
            foreach (var candidate in Forms(lemma, slot, gender, animate))
            {
                if (!Same(candidate, surface)) continue;
                var reading = new RuReading(
                    slot.Case,
                    slot.Number,
                    slot.Number == RuNumber.Plur ? RuGender.None : gender);
                if (!readings.Contains(reading)) readings.Add(reading);
                break;
            }
        }

        return readings;
    }

    /// <summary>Candidate spellings of one slot, for a caller that will validate them.</summary>
    public static IEnumerable<string> Generate(
        string lemma,
        RuCase target,
        RuNumber number,
        RuGender gender,
        bool animate)
        => Forms(lemma, new Slot(target, number), gender, animate);

    private static bool Same(string a, string b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            var x = a[i] == 'ё' ? 'е' : a[i];
            var y = b[i] == 'ё' ? 'е' : b[i];
            if (x != y) return false;
        }

        return true;
    }

    private static IEnumerable<string> Forms(string lemma, Slot slot, RuGender gender, bool animate)
    {
        foreach (var stem in Stems(lemma))
        {
            foreach (var form in FormsForStem(lemma, stem, slot, gender, animate))
            {
                if (form.Length > 0) yield return form;
            }
        }
    }

    /// <summary>The declension stem, plus the fleeting-vowel variant when one is possible.</summary>
    private static IEnumerable<string> Stems(string lemma)
    {
        var last = lemma[^1];
        var stem = last is 'а' or 'я' or 'о' or 'е' or 'ё' or 'й' or 'ь' ? lemma[..^1] : lemma;
        yield return stem;

        // «конец» → «конц-», «день» → «дн-», «отец» → «отц-». Only when the vowel sits between
        // two consonants, which is the shape the alternation actually takes; anything else
        // produces a stem no slot will match and is discarded downstream.
        if (stem.Length >= 3)
        {
            var i = stem.Length - 2;
            if (Vowels.IndexOf(stem[i]) >= 0
                && stem[i] is 'о' or 'е' or 'ё'
                && Vowels.IndexOf(stem[i - 1]) < 0
                && Vowels.IndexOf(stem[^1]) < 0)
            {
                yield return stem[..i] + stem[(i + 1)..];
            }
        }
    }

    private static IEnumerable<string> FormsForStem(
        string lemma,
        string stem,
        Slot slot,
        RuGender gender,
        bool animate)
    {
        var last = lemma[^1];
        var soft = last is 'я' or 'е' or 'ё' or 'й' or 'ь';
        var iya = lemma.EndsWith("ия", StringComparison.Ordinal)
                  || lemma.EndsWith("ие", StringComparison.Ordinal)
                  || lemma.EndsWith("ий", StringComparison.Ordinal);

        // «время», «имя», «знамя» — the -ен- stem. Small closed class, produced alongside the
        // regular attempt rather than instead of it, because the caller filters.
        if (lemma.EndsWith("мя", StringComparison.Ordinal) && gender == RuGender.Neut)
        {
            foreach (var f in Heteroclitic(lemma[..^1] + "ен", slot)) yield return f;
        }

        var declension = last switch
        {
            'а' or 'я' => 1,
            'о' or 'е' or 'ё' => 2,
            'ь' when gender == RuGender.Fem => 3,
            _ => 2,
        };

        switch (declension)
        {
            case 1:
                foreach (var f in FirstDeclension(lemma, stem, soft, iya, slot, animate)) yield return f;
                break;
            case 3:
                foreach (var f in ThirdDeclension(lemma, stem, slot)) yield return f;
                break;
            default:
                foreach (var f in SecondDeclension(lemma, stem, soft, iya, slot, gender, animate)) yield return f;
                break;
        }
    }

    // ---- -а / -я -------------------------------------------------------------

    private static IEnumerable<string> FirstDeclension(
        string lemma, string stem, bool soft, bool iya, Slot slot, bool animate)
    {
        switch (slot.Case, slot.Number)
        {
            case (RuCase.Nom, RuNumber.Sing):
                yield return lemma;
                break;
            case (RuCase.Gen, RuNumber.Sing):
                if (iya) yield return stem + "и";
                yield return stem + (soft ? "и" : Hard(stem, "ы", "и"));
                break;
            case (RuCase.Dat, RuNumber.Sing):
            case (RuCase.Loc, RuNumber.Sing):
                if (iya) yield return stem + "и";
                yield return stem + "е";
                break;
            case (RuCase.Acc, RuNumber.Sing):
                yield return stem + (soft ? "ю" : "у");
                break;
            case (RuCase.Ins, RuNumber.Sing):
                yield return stem + (soft ? "ей" : "ой");
                yield return stem + (soft ? "ею" : "ою");
                yield return stem + "ей";
                yield return stem + "ой";
                break;
            case (RuCase.Nom, RuNumber.Plur):
                yield return stem + (soft ? "и" : Hard(stem, "ы", "и"));
                break;
            case (RuCase.Gen, RuNumber.Plur):
                // Zero ending, with the inserted vowel Russian uses to break the cluster.
                yield return stem;
                yield return stem + "ей";
                yield return stem + "й";
                foreach (var f in WithInsertedVowel(stem)) yield return f;
                break;
            case (RuCase.Dat, RuNumber.Plur):
                yield return stem + (soft ? "ям" : "ам");
                break;
            case (RuCase.Acc, RuNumber.Plur):
                if (animate)
                {
                    yield return stem;
                    foreach (var f in WithInsertedVowel(stem)) yield return f;
                }
                else
                {
                    yield return stem + (soft ? "и" : Hard(stem, "ы", "и"));
                }

                break;
            case (RuCase.Ins, RuNumber.Plur):
                yield return stem + (soft ? "ями" : "ами");
                break;
            case (RuCase.Loc, RuNumber.Plur):
                yield return stem + (soft ? "ях" : "ах");
                break;
        }
    }

    // ---- consonant / -й / -ь masculine, and -о / -е neuter -------------------

    private static IEnumerable<string> SecondDeclension(
        string lemma, string stem, bool soft, bool iya, Slot slot, RuGender gender, bool animate)
    {
        var neuter = gender == RuGender.Neut;

        switch (slot.Case, slot.Number)
        {
            case (RuCase.Nom, RuNumber.Sing):
                yield return lemma;
                break;
            case (RuCase.Gen, RuNumber.Sing):
                yield return stem + (soft ? "я" : "а");
                break;
            case (RuCase.Dat, RuNumber.Sing):
                yield return stem + (soft ? "ю" : "у");
                break;
            case (RuCase.Acc, RuNumber.Sing):
                if (neuter) { yield return lemma; break; }
                if (animate) yield return stem + (soft ? "я" : "а");
                else yield return lemma;
                break;
            case (RuCase.Ins, RuNumber.Sing):
                yield return stem + (soft ? "ем" : "ом");
                yield return stem + "ём";
                yield return stem + (soft ? "ом" : "ем");
                break;
            case (RuCase.Loc, RuNumber.Sing):
                if (iya) yield return stem + "и";
                yield return stem + "е";

                // The second locative: «в аэропорту», «на берегу», «в году», «на ветру».
                // A stressed «-у/-ю» used only after «в» and «на», belonging to a couple of
                // hundred masculine nouns. Listing which nouns have it is not needed here,
                // because a form that is not the word in front of us simply matches nothing —
                // but omitting the slot made every one of them look like a dative, and
                // «в аэропорту» became a government error on correct text.
                if (!neuter)
                {
                    yield return stem + (soft ? "ю" : "у");
                }

                break;
            case (RuCase.Nom, RuNumber.Plur):
                if (neuter)
                {
                    yield return stem + (soft ? "я" : "а");
                }
                else
                {
                    yield return stem + (soft ? "и" : Hard(stem, "ы", "и"));
                    yield return stem + (soft ? "я" : "а");
                    yield return stem + "ья";
                }

                break;
            case (RuCase.Gen, RuNumber.Plur):
                if (neuter)
                {
                    yield return stem;
                    yield return stem + "й";
                    yield return stem + "ей";
                    foreach (var f in WithInsertedVowel(stem)) yield return f;
                }
                else
                {
                    yield return stem + (soft ? "ей" : Hushing.IndexOf(Tail(stem)) >= 0 ? "ей" : "ов");
                    yield return stem + "ов";
                    yield return stem + "ев";
                    yield return stem + "ёв";
                    yield return stem + "ей";
                }

                break;
            case (RuCase.Dat, RuNumber.Plur):
                yield return stem + (soft ? "ям" : "ам");
                yield return stem + "ьям";
                break;
            case (RuCase.Acc, RuNumber.Plur):
                if (neuter)
                {
                    yield return stem + (soft ? "я" : "а");
                }
                else if (animate)
                {
                    yield return stem + (soft ? "ей" : "ов");
                    yield return stem + "ов";
                    yield return stem + "ев";
                    yield return stem + "ей";
                }
                else
                {
                    yield return stem + (soft ? "и" : Hard(stem, "ы", "и"));
                    yield return stem + "ья";
                }

                break;
            case (RuCase.Ins, RuNumber.Plur):
                yield return stem + (soft ? "ями" : "ами");
                yield return stem + "ьями";
                break;
            case (RuCase.Loc, RuNumber.Plur):
                yield return stem + (soft ? "ях" : "ах");
                yield return stem + "ьях";
                break;
        }
    }

    // ---- feminine -ь ---------------------------------------------------------

    private static IEnumerable<string> ThirdDeclension(string lemma, string stem, Slot slot)
    {
        switch (slot.Case, slot.Number)
        {
            case (RuCase.Nom, RuNumber.Sing):
            case (RuCase.Acc, RuNumber.Sing):
                yield return lemma;
                break;
            case (RuCase.Gen, RuNumber.Sing):
            case (RuCase.Dat, RuNumber.Sing):
            case (RuCase.Loc, RuNumber.Sing):
                yield return stem + "и";
                break;
            case (RuCase.Ins, RuNumber.Sing):
                yield return stem + "ью";
                break;
            case (RuCase.Nom, RuNumber.Plur):
            case (RuCase.Acc, RuNumber.Plur):
                yield return stem + "и";
                break;
            case (RuCase.Gen, RuNumber.Plur):
                yield return stem + "ей";
                break;
            case (RuCase.Dat, RuNumber.Plur):
                yield return stem + "ям";
                yield return stem + "ам";
                break;
            case (RuCase.Ins, RuNumber.Plur):
                yield return stem + "ями";
                yield return stem + "ами";
                break;
            case (RuCase.Loc, RuNumber.Plur):
                yield return stem + "ях";
                yield return stem + "ах";
                break;
        }
    }

    // ---- «время», «имя» ------------------------------------------------------

    private static IEnumerable<string> Heteroclitic(string stem, Slot slot)
    {
        switch (slot.Case, slot.Number)
        {
            // «время» is its own nominative and accusative singular; the regular attempt
            // treats the «-я» as a first-declension ending and produces «времю» for the
            // accusative, so «в дополнительное время» lost the reading it actually uses.
            case (RuCase.Nom, RuNumber.Sing):
            case (RuCase.Acc, RuNumber.Sing):
                yield return stem[..^2] + "мя";
                break;
            case (RuCase.Gen, RuNumber.Sing):
            case (RuCase.Dat, RuNumber.Sing):
            case (RuCase.Loc, RuNumber.Sing):
                yield return stem + "и";
                break;
            case (RuCase.Ins, RuNumber.Sing):
                yield return stem + "ем";
                break;
            case (RuCase.Nom, RuNumber.Plur):
            case (RuCase.Acc, RuNumber.Plur):
                yield return stem + "а";
                break;
            case (RuCase.Gen, RuNumber.Plur):
                yield return stem;
                break;
            case (RuCase.Dat, RuNumber.Plur):
                yield return stem + "ам";
                break;
            case (RuCase.Ins, RuNumber.Plur):
                yield return stem + "ами";
                break;
            case (RuCase.Loc, RuNumber.Plur):
                yield return stem + "ах";
                break;
        }
    }

    // ---- spelling helpers ----------------------------------------------------

    private static char Tail(string stem) => stem.Length == 0 ? ' ' : stem[^1];

    /// <summary>«ы» becomes «и» after a velar or hushing consonant.</summary>
    private static string Hard(string stem, string plain, string afterVelar)
    {
        var tail = Tail(stem);
        return Velar.IndexOf(tail) >= 0 || Hushing.IndexOf(tail) >= 0 ? afterVelar : plain;
    }

    /// <summary>
    /// The genitive plural of a zero-ending noun, with the vowel Russian inserts to break the
    /// final consonant cluster: «поездка» → «поездок», «сестра» → «сестёр», «песня» → «песен».
    /// </summary>
    private static IEnumerable<string> WithInsertedVowel(string stem)
    {
        if (stem.Length < 2) yield break;
        if (Vowels.IndexOf(stem[^1]) >= 0) yield break;
        if (Vowels.IndexOf(stem[^2]) >= 0) yield break;

        var head = stem[..^1];
        var tail = stem[^1];
        yield return head + "о" + tail;
        yield return head + "е" + tail;
        yield return head + "ё" + tail;
    }
}
