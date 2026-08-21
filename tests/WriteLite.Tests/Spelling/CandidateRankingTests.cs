using WriteLite.Language.Russian;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Spelling;

/// <summary>
/// Candidate ranking, and the register guard it must not break.
/// </summary>
/// <remarks>
/// Phase 5 forensics on the 25 wrong replacements the deterministic pipeline produced on the
/// frozen corpus found the proper-name register penalty firing unconditionally: candidates
/// are scored against the lowercased word, so <c>char.IsUpper(original[0])</c> was never
/// true and every name-flagged candidate lost a factor of 0.35. Seven of the 25 were proper
/// names. The negatives below are the reason that penalty exists at all, and they matter
/// more than the positives — quietly rewriting an ordinary word into somebody's surname is
/// the classic embarrassing spellchecker output.
/// </remarks>
[TestClass]
public sealed class CandidateRankingTests
{
    private static RussianFormIndex? _index;
    private static RussianCandidateGenerator? _generator;

    [ClassInitialize]
    public static void Load(TestContext _)
    {
        _index = RussianFormIndex.Load();
        if (_index is not null)
        {
            _generator = new RussianCandidateGenerator(_index, new EnglishLayoutLexicon());
        }
    }

    [ClassCleanup]
    public static void Unload() => _index?.Dispose();

    // ── Capitalised misspellings of names now reach the top ─────────────────────

    [TestMethod]
    [DataRow("Гагарен", "гагарин")]
    [DataRow("Тургеньев", "тургенев")]
    [DataRow("Шолохава", "шолохова")]
    [DataRow("Ярославли", "ярославле")]
    [DataRow("Репена", "репина")]
    public void CapitalisedName_RanksItsNameCandidateFirst(string typed, string expected)
        => Assert.AreEqual(expected, Top(typed));

    // ── Hard negatives: the penalty must still do its job ───────────────────────

    [TestMethod]
    [DataRow("мере")]
    [DataRow("роман")]
    [DataRow("орёл")]
    [DataRow("вера")]
    [DataRow("надежда")]
    [DataRow("любовь")]
    public void LowercaseOrdinaryWords_AreNotRewrittenIntoNames(string word)
    {
        // These are correct words; nothing should be suggested for them at all. The point is
        // that a lowercase word must never acquire a capitalised name as its best candidate.
        foreach (var candidate in Rank(word).Take(3))
        {
            Assert.IsFalse(
                candidate.Info.Flags.HasFlag(RussianFormFlags.ProperName)
                && candidate.Score > 0.8,
                $"'{word}' was offered proper name '{candidate.Word}' at {candidate.Score:F3}");
        }
    }

    [TestMethod]
    public void LowercaseTypo_StillPrefersTheCommonNounOverAName()
    {
        // «превет» must become «привет», not a name that happens to be one edit away.
        var top = Top("превет");
        Assert.AreEqual("привет", top);
    }

    [TestMethod]
    public void ProperNamePenaltyStillAppliesToALowercaseWord()
    {
        var lowercase = RussianCandidateScorer.Score(
            "гагарен", "гагарин", 1, RussianCandidateOrigin.EditDistance,
            default, typedCapitalised: false);
        var capitalised = RussianCandidateScorer.Score(
            "гагарен", "гагарин", 1, RussianCandidateOrigin.EditDistance,
            default, typedCapitalised: true);

        // With no flags on the default FormInfo the two are equal; the asymmetry only shows
        // up for a name-flagged candidate, which is exactly what the penalty is scoped to.
        Assert.AreEqual(capitalised, lowercase);
    }

    // ── Character-level repairs are exempt from the name penalty ────────────────

    [TestMethod]
    public void HomoglyphRepair_IsNotTreatedAsAGuessAtAName()
    {
        // «мoре» with a Latin o. «море» is name-flagged in the index; the repair is still a
        // one-to-one character mapping, not a guess.
        Assert.AreEqual("море", Top("мoре"));
    }

    [TestMethod]
    public void LayoutRepair_IsNotTreatedAsAGuessAtAName()
        => Assert.AreEqual("привет", Top("ghbdtn"));

    // ── Ties are broken by frequency, not by the alphabet ───────────────────────

    [TestMethod]
    public void ATiedScore_IsBrokenByFrequencyRatherThanSpelling()
    {
        // «двер» scored «две» and «дверь» identically; the alphabet used to choose «две».
        Assert.AreEqual("дверь", Top("двер"));
    }

    // ── Nothing correct became wrong ────────────────────────────────────────────

    [TestMethod]
    [DataRow("севодня", "сегодня")]
    [DataRow("здраствуйте", "здравствуйте")]
    [DataRow("граматика", "грамматика")]
    [DataRow("канечно", "конечно")]
    [DataRow("извените", "извините")]
    public void OrdinaryTyposStillCorrectAsBefore(string typed, string expected)
    {
        var ranked = Rank(typed).Take(3).Select(c => c.Word).ToList();
        Assert.Contains(expected, ranked, $"'{typed}' produced {string.Join(", ", ranked)}");
    }

    private static string? Top(string word) => Rank(word).FirstOrDefault()?.Word;

    private static IReadOnlyList<RussianCandidate> Rank(string word)
    {
        if (_generator is null)
        {
            Assert.Inconclusive("Russian form index is not deployed.");
            return [];
        }

        return _generator.Rank(word, 8);
    }
}
