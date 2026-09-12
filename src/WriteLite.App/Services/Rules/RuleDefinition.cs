using WriteLite.Language.Russian;
using WriteLite.Models;

namespace WriteLite.Services.Rules;

public static class RulePackSchema
{
    public const int CurrentVersion = 1;
    public const string RussianLanguage = RussianLanguageProfile.IsoCode;
}

public sealed record RulePackDocument(
    int SchemaVersion,
    string PackVersion,
    string Language,
    IReadOnlyList<RuleDefinition> Rules);

public sealed record RuleDefinition(
    string RuleId,
    string Language,
    LinguisticIssueCategory Category,
    IssueCategory IssueCategory,
    string Title,
    string ShortMessage,
    string DetailedExplanation,
    IReadOnlyList<string> BadExamples,
    IReadOnlyList<string> GoodExamples,
    IssueSeverity Severity,
    IReadOnlyList<string> Suggestions,
    bool SafeToApply,
    string Source,
    RuleImplementation Implementation,
    IReadOnlyList<RuleTestCase> Tests);

public sealed record RuleImplementation(
    string Engine,
    string? Pattern = null,
    string? Replacement = null,
    bool IgnoreCase = false);

public sealed record RuleTestCase(
    string Name,
    string Input,
    bool ShouldMatch,
    string? ExpectedSuggestion = null);
