using System.Security.Cryptography;
using System.Text;
using WriteLite.Models;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Deterministic WriteLite rule ids: WL-SPELL-… without user text or third-party branding.
/// </summary>
public static class WriteLiteRuleIdFactory
{
    public static string Create(
        IssueCategory category,
        string? engineRuleId,
        string? issueType,
        string? categoryId)
    {
        var prefix = WriteLiteIssueCategoryMapper.CategoryPrefix(category);
        var material = string.Join('|',
            SanitizeKey(engineRuleId),
            SanitizeKey(issueType),
            SanitizeKey(categoryId));

        if (string.IsNullOrEmpty(material) || material == "||")
        {
            material = "unknown";
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .AsSpan(0, 12)
            .ToString()
            .ToUpperInvariant();

        return $"{prefix}-{hash}";
    }

    private static string SanitizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')
            {
                sb.Append(char.ToUpperInvariant(ch));
            }
        }

        var s = sb.ToString();
        // Strip known third-party product tokens if present in rule ids (internal only hash input).
        s = s.Replace("LANGUAGETOOL", string.Empty, StringComparison.Ordinal);
        return s;
    }
}
