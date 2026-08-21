namespace WriteLite.Language.Core;

/// <summary>
/// Answers whether a string is a word in the <em>other</em> language, for keyboard-layout
/// recovery.
/// </summary>
/// <remarks>
/// A word typed with the wrong layout active is not a spelling error and edit distance will
/// never find it: "пшерги" is one keystroke-for-keystroke mapping away from "github" and
/// nowhere near it in Cyrillic. Recovering it needs a lexicon of the target language, which
/// the Russian correction layer has no business owning — hence this seam. The host supplies
/// an implementation; when none is supplied, layout recovery keeps working in the direction
/// it always worked (Latin keystrokes, Russian word) and simply cannot reach the other.
///
/// Membership alone never corrects anything. It makes a candidate available to ranking,
/// which still has to prefer it over every Russian candidate — see the layout branch in
/// <c>RussianCandidateGenerator</c>. That ordering is what keeps a genuinely Russian word
/// from being rewritten into an English lookalike.
/// </remarks>
public interface ILayoutTargetLexicon
{
    /// <summary>True when the word exists in the target language.</summary>
    /// <remarks>Called with a lowercase string; implementations need not fold again.</remarks>
    bool Contains(string word);

    /// <summary>
    /// True for vocabulary a user is especially likely to type inside prose in another
    /// language — tool names, product names, programming terms.
    /// </summary>
    /// <remarks>
    /// Ranked above ordinary dictionary membership because the prior differs sharply: a
    /// Russian speaker writing about "github" is common, and one who meant an obscure
    /// English noun that happens to be the layout image of their typo is not.
    /// </remarks>
    bool IsTechnicalTerm(string word);
}
