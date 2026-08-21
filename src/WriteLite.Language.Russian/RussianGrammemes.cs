namespace WriteLite.Language.Russian;

/// <summary>Grammatical case. «Loc» is the предложный падеж.</summary>
public enum RuCase : byte { Nom = 0, Gen = 1, Dat = 2, Acc = 3, Ins = 4, Loc = 5 }

public enum RuNumber : byte { Sing = 0, Plur = 1 }

/// <summary>Gender, with <see cref="None"/> for plural forms that do not mark it.</summary>
public enum RuGender : byte { None = 0, Masc = 1, Fem = 2, Neut = 3 }

public enum RuPerson : byte { None = 0, First = 1, Second = 2, Third = 3 }

/// <summary>
/// One concrete grammatical reading of a surface form.
/// </summary>
/// <remarks>
/// <para><b>Why readings are enumerated rather than folded into per-dimension flag sets.</b>
/// «новым» is masculine/neuter singular instrumental <em>or</em> plural dative — two readings
/// that share no dimension value. Folded into flags the word would come back as
/// case ∈ {Ins, Dat}, number ∈ {Sing, Plur}, gender ∈ {Masc, Neut}, which also describes a
/// masculine singular dative the word cannot be. Flags therefore agree with more contexts than
/// the word does, and every one of those extra agreements is a missed error.</para>
///
/// <para>The cost is bounded: no Russian adjectival ending carries more than four readings and
/// no noun ending more than three, so a set is a short array and agreement is a nested loop
/// over at most sixteen pairs.</para>
/// </remarks>
public readonly record struct RuReading(RuCase Case, RuNumber Number, RuGender Gender)
{
    public override string ToString() => $"{Case}/{Number}/{Gender}";
}

/// <summary>
/// The readings a surface form can carry, together with what is known about it lexically.
/// </summary>
/// <remarks>
/// <para><b>An empty <see cref="Readings"/> set means "not analysed", never "no reading is
/// possible".</b> Every consumer in this assembly treats it as a reason to stay silent. That
/// is the single invariant which keeps a morphology-driven rule from inventing errors in text
/// it simply does not understand: irregular paradigms, fleeting vowels the generator missed,
/// borrowings and indeclinables all arrive here as an empty set, and an empty set produces no
/// finding.</para>
///
/// <para>The converse invariant is what makes the analysis usable: a non-empty set must be a
/// <em>superset</em> of the form's true readings. Under-generating a reading turns correct
/// text into a reported error, so every table below errs toward listing more.</para>
/// </remarks>
public readonly record struct RuAnalysis(
    IReadOnlyList<RuReading> Readings,
    RuGender LexicalGender,
    bool Animate,
    bool Known)
{
    public static readonly RuAnalysis Unknown = new([], RuGender.None, false, false);

    public bool IsEmpty => Readings.Count == 0;

    /// <summary>True when some reading of each form can describe the same slot.</summary>
    /// <remarks>
    /// Gender is compared only in the singular: Russian plural adjectives and nouns do not
    /// mark it, and <see cref="RuGender.None"/> on either side is treated as "does not
    /// constrain" rather than as a fourth value that fails to match.
    /// </remarks>
    public bool AgreesWith(RuAnalysis other)
    {
        if (IsEmpty || other.IsEmpty) return true;

        foreach (var a in Readings)
        {
            foreach (var b in other.Readings)
            {
                if (a.Case != b.Case || a.Number != b.Number) continue;
                if (a.Number == RuNumber.Plur) return true;
                if (a.Gender == RuGender.None || b.Gender == RuGender.None) return true;
                if (a.Gender == b.Gender) return true;
            }
        }

        return false;
    }

    /// <summary>True when some reading is in <paramref name="required"/>.</summary>
    public bool CanBe(RuCase required)
    {
        foreach (var r in Readings)
        {
            if (r.Case == required) return true;
        }

        return false;
    }

    /// <summary>True when some reading is in any of <paramref name="required"/>.</summary>
    public bool CanBeAny(IReadOnlyList<RuCase> required)
    {
        foreach (var r in Readings)
        {
            foreach (var c in required)
            {
                if (r.Case == c) return true;
            }
        }

        return false;
    }
}

/// <summary>Verb features that matter for subject agreement.</summary>
/// <remarks>
/// Past-tense Russian marks gender and number; present and future mark person and number.
/// A form is described by whichever of those it actually carries, and the unmarked dimensions
/// stay at <see cref="RuGender.None"/> / <see cref="RuPerson.None"/> so an agreement check
/// silently skips them rather than failing on them.
/// </remarks>
public readonly record struct RuVerbForm(
    RuNumber Number,
    RuGender Gender,
    RuPerson Person,
    bool IsPast,
    bool IsInfinitive)
{
    public static readonly RuVerbForm None = new(RuNumber.Sing, RuGender.None, RuPerson.None, false, false);
}
