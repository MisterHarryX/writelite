using WriteLite.Models;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Maps engine match signals to WriteLite <see cref="IssueCategory"/>.
/// Priority: issueType → category.id → rule.id family → category.name.
/// </summary>
public static class WriteLiteIssueCategoryMapper
{
    public static IssueCategory Map(WriteLiteLanguageMatchDto match)
    {
        var issueType = match.Rule?.IssueType;
        var categoryId = match.Rule?.Category?.Id;
        var ruleId = match.Rule?.Id;
        var categoryName = match.Rule?.Category?.Name;

        if (TryMapToken(issueType, out var fromType))
        {
            return fromType;
        }

        if (TryMapToken(categoryId, out var fromCatId))
        {
            return fromCatId;
        }

        if (TryMapRuleId(ruleId, out var fromRule))
        {
            return fromRule;
        }

        if (TryMapToken(categoryName, out var fromName))
        {
            return fromName;
        }

        return IssueCategory.Readability; // UI: «ОФОРМЛЕНИЕ» / other
    }

    public static LinguisticIssueCategory MapLinguistic(IssueCategory category, WriteLiteLanguageMatchDto match)
    {
        var issueType = (match.Rule?.IssueType ?? string.Empty).ToLowerInvariant();
        var categoryId = (match.Rule?.Category?.Id ?? string.Empty).ToUpperInvariant();
        var ruleId = (match.Rule?.Id ?? string.Empty).ToUpperInvariant();

        if (IsRepetition(issueType, categoryId, ruleId))
        {
            return LinguisticIssueCategory.RepeatedWord;
        }

        return category switch
        {
            IssueCategory.Orthography => LinguisticIssueCategory.Typo,
            IssueCategory.Punctuation => LinguisticIssueCategory.PunctuationRecommendation,
            IssueCategory.Grammar => LinguisticIssueCategory.AgreementError,
            IssueCategory.Style => LinguisticIssueCategory.LongSentence,
            IssueCategory.Readability => LinguisticIssueCategory.ExtraSpace,
            _ => LinguisticIssueCategory.UnknownWord
        };
    }

    public static string CategoryPrefix(IssueCategory category) => category switch
    {
        IssueCategory.Orthography => "WL-SPELL",
        IssueCategory.Punctuation => "WL-PUNCT",
        IssueCategory.Grammar => "WL-GRAMMAR",
        IssueCategory.Style => "WL-STYLE",
        IssueCategory.Readability => "WL-OTHER",
        _ => "WL-OTHER"
    };

    /// <summary>
    /// User-facing bucket label used for tests and diagnostics (not a model change).
    /// </summary>
    public static string UserFacingBucket(IssueCategory category, LinguisticIssueCategory linguistic)
    {
        if (linguistic == LinguisticIssueCategory.RepeatedWord)
        {
            return "Повторы";
        }

        return category switch
        {
            IssueCategory.Orthography => "Орфография",
            IssueCategory.Punctuation => "Пунктуация",
            IssueCategory.Grammar => "Грамматика",
            IssueCategory.Style => "Стиль",
            IssueCategory.Readability when linguistic is LinguisticIssueCategory.ExtraSpace
                or LinguisticIssueCategory.MissingSpace => "Типографика",
            IssueCategory.Readability => "Другое",
            _ => "Другое"
        };
    }

    private static bool TryMapToken(string? token, out IssueCategory category)
    {
        category = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var t = token.Trim().ToLowerInvariant();

        // Order matters: "typography" contains "typo".
        if (t is "typography" or "typesetting"
            || t.Contains("typograph", StringComparison.Ordinal)
            || t.Contains("whitespace", StringComparison.Ordinal)
            || t is "whitespace" or "spacing")
        {
            category = IssueCategory.Readability;
            return true;
        }

        if (t is "misspelling" or "typo" or "typos" or "spelling" or "orthography"
            || t.Contains("spell", StringComparison.Ordinal)
            || (t.Contains("typo", StringComparison.Ordinal) && !t.Contains("typograph", StringComparison.Ordinal)))
        {
            category = IssueCategory.Orthography;
            return true;
        }

        if (t is "punctuation" or "punct" || t.Contains("punct", StringComparison.Ordinal))
        {
            category = IssueCategory.Punctuation;
            return true;
        }

        if (t is "grammar" or "grammatical" || t.Contains("grammar", StringComparison.Ordinal))
        {
            category = IssueCategory.Grammar;
            return true;
        }

        if (t is "style" or "register" or "stylistic"
            || t.Contains("style", StringComparison.Ordinal)
            || t.Contains("register", StringComparison.Ordinal))
        {
            category = IssueCategory.Style;
            return true;
        }

        if (t is "repetition" or "redundant" or "redundancy"
            || t.Contains("repeat", StringComparison.Ordinal)
            || t.Contains("duplicat", StringComparison.Ordinal)
            || t.Contains("redundan", StringComparison.Ordinal))
        {
            category = IssueCategory.Style;
            return true;
        }

        return false;
    }

    private static bool TryMapRuleId(string? ruleId, out IssueCategory category)
    {
        category = default;
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            return false;
        }

        var r = ruleId.ToUpperInvariant();
        if (r.Contains("MORFOLOGIK") || r.Contains("HUNSPELL") || r.Contains("SPELL")
            || r.Contains("TYPO"))
        {
            category = IssueCategory.Orthography;
            return true;
        }

        if (r.Contains("PUNCT") || r.Contains("COMMA") || r.Contains("QUOTE")
            || r.Contains("DASH") || r.Contains("POZHALUJSTA"))
        {
            category = IssueCategory.Punctuation;
            return true;
        }

        if (r.Contains("GRAMMAR") || r.Contains("AGREEMENT") || r.Contains("CONJUG"))
        {
            category = IssueCategory.Grammar;
            return true;
        }

        if (r.Contains("STYLE") || r.Contains("REGISTER") || r.Contains("FORMAL"))
        {
            category = IssueCategory.Style;
            return true;
        }

        if (r.Contains("WHITESPACE") || r.Contains("DOUBLE_SPACE") || r.Contains("TYPOGRAPH"))
        {
            category = IssueCategory.Readability;
            return true;
        }

        if (r.Contains("REPEAT") || r.Contains("WORD_REPEAT") || r.Contains("REDUNDAN"))
        {
            category = IssueCategory.Style;
            return true;
        }

        return false;
    }

    private static bool IsRepetition(string issueType, string categoryId, string ruleId)
        => issueType.Contains("repet") || issueType.Contains("redundan")
           || categoryId.Contains("REPET") || categoryId.Contains("REDUND")
           || ruleId.Contains("REPEAT") || ruleId.Contains("WORD_REPEAT");
}
