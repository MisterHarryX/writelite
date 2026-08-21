using WriteLite.Language.Russian;

namespace WriteLite.Harness;

/// <summary>
/// Sorts a <c>target → replacement</c> pair into the linguistic classes §3 asks about.
/// </summary>
/// <remarks>
/// <para>The corpus labels are not enough on their own. The golden set's <c>category</c> is a
/// benchmark-design label ("real_word", "morphology") assigned per <em>item</em>, and the
/// error corpus's <c>errors[].type</c> is a generator label ("confusion_pair",
/// "morph_inflect") describing how a corruption was <em>made</em>. Neither answers the
/// question a candidate generator has to act on, which is what kind of relationship holds
/// between the two words. These classes are derived from the words themselves and from the
/// form index, so the same classifier applies to both corpora and to any future one.</para>
///
/// <para>Order matters: the tests run from most specific to least, and the first that fires
/// wins. тся/ться is checked before the general morphology test because it is a subset of it
/// with a completely different fix.</para>
/// </remarks>
internal static class Classify
{
    public const string Tsya = "тся/ться";
    public const string Layout = "layout";
    public const string CaseForm = "case_form";
    public const string VerbForm = "verb_form";
    public const string Agreement = "agreement";
    public const string Morphology = "morphology";
    public const string RealWord = "real_word";
    public const string Orthography = "orthography";
    public const string Other = "other";

    public static string Of(RussianFormIndex index, string target, string replacement)
    {
        var a = target.ToLowerInvariant().Replace('ё', 'е');
        var b = replacement.ToLowerInvariant().Replace('ё', 'е');
        if (a.Length == 0 || b.Length == 0) return Other;

        if (IsTsyaPair(a, b)) return Tsya;
        if (IsLayoutPair(target, replacement)) return Layout;

        var targetKnown = index.Contains(a);
        var replacementInfo = index.GetInfo(b);
        var targetInfo = index.GetInfo(a);

        // Same lemma on both sides is the defining mark of a form-choice error: the writer
        // picked the right word and the wrong ending. Split by what actually differs so a
        // weak sub-class cannot hide inside an aggregate (§16).
        var sameLemma = targetKnown
                        && targetInfo.Lemma.Length > 0
                        && string.Equals(targetInfo.Lemma, replacementInfo.Lemma, StringComparison.OrdinalIgnoreCase);

        if (sameLemma)
        {
            return SubClassOf(targetInfo, replacementInfo);
        }

        // Both sides are real words but unrelated — компания/кампания, одеть/надеть. Nothing
        // in the spelling layer can see this, which is precisely why it is its own class.
        if (targetKnown) return RealWord;

        return Orthography;
    }

    private static string SubClassOf(
        RussianFormIndex.FormInfo target,
        RussianFormIndex.FormInfo replacement)
    {
        var pos = replacement.PartOfSpeech;
        var isVerbal = pos is RussianPartOfSpeech.Verb
            or RussianPartOfSpeech.Infinitive
            or RussianPartOfSpeech.ParticipleFull
            or RussianPartOfSpeech.ParticipleShort
            or RussianPartOfSpeech.Gerund;

        var targetGrammemes = Grammemes(target.Tag);
        var replacementGrammemes = Grammemes(replacement.Tag);

        if (isVerbal) return VerbForm;

        var caseChanged = Differs(targetGrammemes, replacementGrammemes, Cases);
        var agreementChanged = Differs(targetGrammemes, replacementGrammemes, Genders)
                               || Differs(targetGrammemes, replacementGrammemes, Numbers);

        // An adjective or participle that changed gender or number is agreeing with something;
        // a noun that changed case is being governed by something. When both moved, the
        // governing relationship is the stronger signal and case wins.
        if (caseChanged) return CaseForm;
        if (agreementChanged) return Agreement;
        return Morphology;
    }

    private static readonly string[] Cases =
        ["nomn", "gent", "datv", "accs", "ablt", "loct", "voct", "gen1", "gen2", "acc2", "loc1", "loc2"];

    private static readonly string[] Genders = ["masc", "femn", "neut"];
    private static readonly string[] Numbers = ["sing", "plur"];

    private static HashSet<string> Grammemes(string tag)
        => new(
            tag.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    private static bool Differs(HashSet<string> a, HashSet<string> b, string[] axis)
    {
        var left = axis.FirstOrDefault(a.Contains);
        var right = axis.FirstOrDefault(b.Contains);
        return left is not null && right is not null && !string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>«учится» ↔ «учиться» — the same stem with and without the soft sign.</summary>
    public static bool IsTsyaPair(string a, string b)
    {
        if (a.EndsWith("тся", StringComparison.Ordinal) && b.EndsWith("ться", StringComparison.Ordinal))
        {
            return string.Equals(a[..^3], b[..^4], StringComparison.Ordinal);
        }

        if (a.EndsWith("ться", StringComparison.Ordinal) && b.EndsWith("тся", StringComparison.Ordinal))
        {
            return string.Equals(a[..^4], b[..^3], StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsLayoutPair(string target, string replacement)
    {
        if (RussianKeyboard.IsCyrillicText(target))
        {
            var latin = RussianKeyboard.RussianToLatinLayout(target.ToLowerInvariant());
            if (latin is not null && string.Equals(latin, replacement, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (RussianKeyboard.IsLatinLayoutText(target))
        {
            var cyrillic = RussianKeyboard.LatinToRussianLayout(target.ToLowerInvariant());
            if (cyrillic is not null && string.Equals(cyrillic, replacement, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
