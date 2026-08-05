using WriteLite.Models;

namespace WriteLite.Services.Ai;

/// <summary>
/// Maps AI provider JSON into validated WriteLite issues.
/// Prefer correctedText + local diff for punctuation restoration.
/// </summary>
public sealed class AiTextAnalysisService
{
    private readonly IAiTextProvider _provider;
    private readonly ITextCorrectionDiffService _diff;
    private readonly AnalysisResultValidator _validator;

    public AiTextAnalysisService(
        IAiTextProvider provider,
        ITextCorrectionDiffService? diff = null,
        AnalysisResultValidator? validator = null)
    {
        _provider = provider;
        _diff = diff ?? new TextCorrectionDiffService();
        _validator = validator ?? new AnalysisResultValidator();
    }

    public bool IsAvailable => _provider.IsConfigured;

    public async Task<IReadOnlyList<TextIssue>> AnalyzeAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (!_provider.IsConfigured || string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        AiTextAnalysisResponse response;
        try
        {
            response = await _provider.AnalyzeAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }

        // Text may have changed after await — caller must re-validate; we still validate against `text`.
        var issues = new List<TextIssue>();

        // Prefer full correctedText → local diff (best for missing punctuation).
        var corrected = _validator.NormalizeCorrectedText(text, response.CorrectedText);
        if (!string.IsNullOrWhiteSpace(corrected)
            && !string.Equals(text, corrected, StringComparison.Ordinal))
        {
            issues.AddRange(_diff.BuildIssues(text, corrected));
        }

        // Merge explicit issue list when offsets are trustworthy.
        if (response.Issues is { Count: > 0 })
        {
            foreach (var dto in response.Issues)
            {
                var mapped = MapDto(text, dto);
                if (mapped is not null)
                {
                    issues.Add(mapped);
                }
            }
        }

        return _validator.FilterValidIssues(text, issues);
    }

    private TextIssue? MapDto(string text, AiTextIssueDto dto)
    {
        if (dto.Start < 0 || dto.Length <= 0)
        {
            return null;
        }

        if (!_validator.IsRangeValid(text, dto.Start, dto.Length))
        {
            return null;
        }

        var original = dto.Original;
        if (string.IsNullOrEmpty(original))
        {
            original = text.Substring(dto.Start, dto.Length);
        }

        if (!_validator.OriginalMatches(text, dto.Start, dto.Length, original))
        {
            return null;
        }

        if (!_validator.IsReplacementSane(original, dto.Replacement))
        {
            return null;
        }

        if (string.Equals(original, dto.Replacement, StringComparison.Ordinal))
        {
            return null;
        }

        var (cat, ling) = AiIssueTypeMapper.Map(dto.Type);
        var safe = dto.SafeToApply && dto.Confidence >= 0.75;
        var message = string.IsNullOrWhiteSpace(dto.Message)
            ? "Исправление по результатам расширенного анализа."
            : dto.Message.Trim();

        return new TextIssue(
            dto.Start,
            dto.Length,
            original,
            dto.Replacement,
            TitleFor(cat),
            message,
            cat,
            dto.Confidence >= 0.9 ? IssueSeverity.Error : IssueSeverity.Warning,
            CanApplyAutomatically: safe,
            RuleId: "WL-AI-" + (dto.Type ?? "issue").ToUpperInvariant(),
            LinguisticCategory: ling,
            Confidence: Math.Clamp(dto.Confidence, 0, 1));
    }

    private static string TitleFor(IssueCategory cat) => cat switch
    {
        IssueCategory.Punctuation => "Пунктуация",
        IssueCategory.Orthography => "Орфография",
        IssueCategory.Grammar => "Грамматика",
        IssueCategory.Style => "Стиль",
        IssueCategory.Readability => "Оформление",
        _ => "Замечание"
    };
}
