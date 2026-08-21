namespace WriteLite.Models;

public enum IssueCategory
{
    Orthography,
    Grammar,
    Punctuation,
    Style,
    Readability
}

public enum IssueSeverity
{
    Error,
    Warning,
    Suggestion
}

public enum LinguisticIssueCategory
{
    Typo,
    UnknownWord,
    UncheckedVowel,
    CheckableUnstressedVowel,
    PairedConsonant,
    SilentConsonant,
    JoinedOrSeparateSpelling,
    TsyaTisya,
    NeWithPartsOfSpeech,
    EndingError,
    AgreementError,
    ExtraSpace,
    MissingSpace,
    PunctuationRecommendation,
    RepeatedWord,
    LongSentence
}

/// <summary>
/// What kind of claim a finding is making about the text.
/// </summary>
/// <remarks>
/// §14 of the Phase 7 brief: a stylistic recommendation is not a grammatical error, and the
/// distinction has to survive all the way to the UI. <see cref="IssueCategory"/> says which
/// part of the language a finding is about and <see cref="IssueSeverity"/> says how strongly
/// it is held; neither on its own answers "is this wrong, or is this a suggestion". A Style
/// finding at Warning severity and a Grammar finding at Warning severity are not the same
/// thing to a reader, and before this existed they reached the card as the same thing.
/// </remarks>
public enum IssueClass
{
    /// <summary>The text is wrong. «вопреки новых целей», «небыл».</summary>
    Error,

    /// <summary>Very probably wrong, but the reading depends on context.</summary>
    Warning,

    /// <summary>Correct Russian that could be written better. Never an error.</summary>
    Style,

    /// <summary>An observation, offered without a claim that anything is wrong.</summary>
    Information,
}

/// <summary>
/// How confident WriteLite is that a correction should be made — §12, for punctuation in
/// particular, but meaningful for every category.
/// </summary>
public enum CorrectionCertainty
{
    /// <summary>A closed rule with stated exceptions. Safe to apply without reading it.</summary>
    Certain,

    /// <summary>Probably right, and worth reading before applying.</summary>
    Likely,

    /// <summary>An offer. Applying or ignoring it are both reasonable.</summary>
    Suggestion,
}

public sealed record TextIssue(
    int Start,
    int Length,
    string Original,
    string? Replacement,
    string Title,
    string Explanation,
    IssueCategory Category,
    IssueSeverity Severity,
    bool CanApplyAutomatically = true,
    string RuleId = "unknown",
    LinguisticIssueCategory LinguisticCategory = LinguisticIssueCategory.UnknownWord,
    double Confidence = 1.0)
{
    /// <summary>
    /// The ERROR / WARNING / STYLE / INFORMATION classification.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored, so that no analyzer can classify itself inconsistently with
    /// the category and severity it already declared, and so that adding it required no change
    /// to the ~30 places that construct a finding.
    ///
    /// Style and Readability are Style whatever their severity: a long sentence is not an
    /// error at Warning severity, it is a stylistic observation that the writer may disagree
    /// with. Everything else follows severity.
    /// </remarks>
    public IssueClass Class => Category switch
    {
        IssueCategory.Style or IssueCategory.Readability => IssueClass.Style,
        _ => Severity switch
        {
            IssueSeverity.Error => IssueClass.Error,
            IssueSeverity.Warning => IssueClass.Warning,
            _ => IssueClass.Information,
        },
    };

    /// <summary>
    /// How firmly this correction is held, for a UI that wants to say so.
    /// </summary>
    /// <remarks>
    /// <para>A finding with no replacement is never more than a suggestion — there is nothing
    /// to apply. Beyond that the two signals that already exist are the right ones:
    /// <see cref="CanApplyAutomatically"/> is granted by the rule pack to closed rules with
    /// stated exceptions, and <see cref="Confidence"/> is what the layer thought.</para>
    ///
    /// <para>The trained punctuation model never reaches <see cref="CorrectionCertainty.Certain"/>,
    /// because it never carries the auto-apply flag. That is the §12 requirement expressed as
    /// a consequence of the pipeline's shape rather than as a rule about model findings.</para>
    /// </remarks>
    public CorrectionCertainty Certainty => string.IsNullOrEmpty(Replacement)
        ? CorrectionCertainty.Suggestion
        : CanApplyAutomatically && Confidence >= 0.85
            ? CorrectionCertainty.Certain
            : Confidence >= 0.7
                ? CorrectionCertainty.Likely
                : CorrectionCertainty.Suggestion;
}
