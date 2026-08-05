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
    double Confidence = 1.0);
