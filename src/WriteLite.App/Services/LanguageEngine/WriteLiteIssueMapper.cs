using System.Diagnostics;
using System.Globalization;
using System.Text;
using WriteLite.Models;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Converts internal engine match DTOs into WriteLite <see cref="TextIssue"/> models.
/// Does not log user text.
/// </summary>
public sealed class WriteLiteIssueMapper
{
    public const int MaxReplacementLength = 80;
    public const double MaxReplacementGrowth = 3.0;

    public WriteLiteIssueMapResult MapAll(string sourceText, WriteLiteLanguageResponseDto response)
    {
        ArgumentNullException.ThrowIfNull(response);
        sourceText ??= string.Empty;

        var sw = Stopwatch.StartNew();
        var issues = new List<TextIssue>();
        var rejected = 0;
        var input = response.Matches?.Count ?? 0;

        if (response.Matches is not null)
        {
            foreach (var match in response.Matches)
            {
                var mapped = MapOne(sourceText, match);
                if (mapped is null)
                {
                    rejected++;
                }
                else
                {
                    issues.Add(mapped);
                }
            }
        }

        sw.Stop();
        CompatibilityLogger.Technical(
            "language-engine-map-completed",
            $"inputMatchCount={input} mappedCount={issues.Count} rejectedCount={rejected} durationMs={sw.ElapsedMilliseconds}");

        return new WriteLiteIssueMapResult(issues, input, rejected, sw.Elapsed);
    }

    public TextIssue? MapOne(string sourceText, WriteLiteLanguageMatchDto? match)
    {
        if (match is null)
        {
            return null;
        }

        sourceText ??= string.Empty;
        var offset = match.Offset;
        var length = match.Length;

        if (!IsSafeRange(sourceText, offset, length, out var original))
        {
            return null;
        }

        var replacements = match.Replacements ?? [];
        var nonEmpty = replacements
            .Select(r => r.Value)
            .Where(v => v is not null)
            .Select(v => v!)
            .Where(v => !string.Equals(v, original, StringComparison.Ordinal))
            .ToList();

        string? replacement = null;
        var canApplyAutomatically = false;

        if (nonEmpty.Count == 0)
        {
            // Empty or identical replacements — informational only.
            replacement = null;
            canApplyAutomatically = false;
        }
        else
        {
            replacement = nonEmpty[0];
            if (!IsAcceptableReplacement(original, replacement, match, out var safeForAuto))
            {
                if (string.IsNullOrEmpty(replacement))
                {
                    return null;
                }

                canApplyAutomatically = false;
            }
            else
            {
                // Single unambiguous → can auto; multiple → manual only (CanApplyAutomatically=false).
                canApplyAutomatically = nonEmpty.Count == 1 && safeForAuto;
            }
        }

        // Empty deletion only for known safe whitespace/punctuation families.
        if (replacement is not null && replacement.Length == 0)
        {
            if (!IsSafeDeletion(match, original))
            {
                canApplyAutomatically = false;
                // Still allow manual if short whitespace deletion?
                if (original.Length > 3 || !IsWhitespaceOnly(original))
                {
                    replacement = null;
                    canApplyAutomatically = false;
                }
            }
            else
            {
                canApplyAutomatically = true;
            }
        }

        var category = WriteLiteIssueCategoryMapper.Map(match);
        var linguistic = WriteLiteIssueCategoryMapper.MapLinguistic(category, match);
        var ruleId = WriteLiteRuleIdFactory.Create(
            category,
            match.Rule?.Id,
            match.Rule?.IssueType,
            match.Rule?.Category?.Id);

        var fallbackTitle = DefaultTitle(category);
        var title = WriteLiteIssueNormalization.NormalizeTitle(
            match.ShortMessage,
            match.Message,
            fallbackTitle);
        var explanation = WriteLiteIssueNormalization.NormalizeExplanation(
            match.Message,
            match.ShortMessage,
            DefaultExplanation(category));

        if (string.IsNullOrWhiteSpace(title))
        {
            title = fallbackTitle;
        }

        if (string.IsNullOrWhiteSpace(explanation))
        {
            explanation = DefaultExplanation(category);
        }

        // Never surface third-party brand after normalization.
        if (WriteLiteIssueNormalization.ContainsThirdPartyBrand(title)
            || WriteLiteIssueNormalization.ContainsThirdPartyBrand(explanation))
        {
            title = fallbackTitle;
            explanation = DefaultExplanation(category);
        }

        var severity = canApplyAutomatically ? IssueSeverity.Error : IssueSeverity.Suggestion;

        return new TextIssue(
            Start: offset,
            Length: length,
            Original: original,
            Replacement: replacement,
            Title: title,
            Explanation: explanation,
            Category: category,
            Severity: severity,
            CanApplyAutomatically: canApplyAutomatically && replacement is not null,
            RuleId: ruleId,
            LinguisticCategory: linguistic);
    }

    public static bool IsSafeRange(string text, int offset, int length, out string original)
    {
        original = string.Empty;
        if (offset < 0 || length <= 0)
        {
            return false;
        }

        if (offset > text.Length || length > text.Length - offset)
        {
            return false;
        }

        // Do not split surrogate pairs.
        if (char.IsLowSurrogate(text[offset]))
        {
            return false;
        }

        var end = offset + length;
        if (end < text.Length && char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1]))
        {
            // range ends in the middle of a pair
            return false;
        }

        if (end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
        {
            return false;
        }

        // Do not split CRLF.
        if (offset > 0 && text[offset] == '\n' && text[offset - 1] == '\r')
        {
            return false;
        }

        if (end < text.Length && end > 0 && text[end - 1] == '\r' && text[end] == '\n')
        {
            return false;
        }

        original = text.Substring(offset, length);

        // Reject control characters other than tab/CR/LF inside the span.
        foreach (var ch in original)
        {
            if (char.IsControl(ch) && ch is not '\t' and not '\r' and not '\n')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAcceptableReplacement(
        string original,
        string replacement,
        WriteLiteLanguageMatchDto match,
        out bool safeForAuto)
    {
        safeForAuto = false;

        if (string.Equals(original, replacement, StringComparison.Ordinal))
        {
            return false;
        }

        if (replacement.Length > MaxReplacementLength)
        {
            return false;
        }

        if (original.Length > 0 && replacement.Length > original.Length * MaxReplacementGrowth + 8)
        {
            return false;
        }

        // Newlines in replacement → not auto-safe.
        if (replacement.Contains('\n') || replacement.Contains('\r'))
        {
            safeForAuto = false;
            return true; // still show for manual if UI allows
        }

        foreach (var ch in replacement)
        {
            if (char.IsControl(ch) && ch is not '\t')
            {
                return false;
            }
        }

        var category = WriteLiteIssueCategoryMapper.Map(match);
        // Grammar rewrites are never auto-safe.
        if (category == IssueCategory.Grammar && replacement.Length > original.Length + 10)
        {
            safeForAuto = false;
            return true;
        }

        if (category is IssueCategory.Orthography or IssueCategory.Punctuation
            or IssueCategory.Readability)
        {
            safeForAuto = true;
            return true;
        }

        if (category == IssueCategory.Style)
        {
            safeForAuto = original.Length <= 20 && replacement.Length <= 20;
            return true;
        }

        safeForAuto = false;
        return true;
    }

    private static bool IsSafeDeletion(WriteLiteLanguageMatchDto match, string original)
    {
        if (!IsWhitespaceOnly(original) && original is not ("," or "." or ";" or ":" or "…" or "-"))
        {
            return false;
        }

        var category = WriteLiteIssueCategoryMapper.Map(match);
        return category is IssueCategory.Punctuation or IssueCategory.Readability
            || IsWhitespaceOnly(original);
    }

    private static bool IsWhitespaceOnly(string value)
        => value.Length > 0 && value.All(char.IsWhiteSpace);

    private static string DefaultTitle(IssueCategory category) => category switch
    {
        IssueCategory.Orthography => "Орфография",
        IssueCategory.Punctuation => "Пунктуация",
        IssueCategory.Grammar => "Грамматика",
        IssueCategory.Style => "Стиль",
        IssueCategory.Readability => "Оформление",
        _ => "Замечание"
    };

    private static string DefaultExplanation(IssueCategory category) => category switch
    {
        IssueCategory.Orthography => "Возможная орфографическая ошибка.",
        IssueCategory.Punctuation => "Проверьте знаки препинания.",
        IssueCategory.Grammar => "Возможная грамматическая ошибка.",
        IssueCategory.Style => "Стилистическая рекомендация.",
        IssueCategory.Readability => "Рекомендация по оформлению текста.",
        _ => "Локальная проверка обнаружила замечание."
    };
}

public sealed record WriteLiteIssueMapResult(
    IReadOnlyList<TextIssue> Issues,
    int InputMatchCount,
    int RejectedCount,
    TimeSpan Duration);
