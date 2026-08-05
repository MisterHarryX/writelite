using System.Diagnostics;
using WriteLite.Models;
using WriteLite.Services.Settings;
using WriteLite.Services.Spelling;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Composes built-in WriteLite analyzers with the extended engine, merge, dictionary and ignore filters.
/// </summary>
public sealed class WriteLiteOrchestratingAnalyzer : ITextAnalyzer, ITextAnalysisProgressSource
{
    private readonly ITextAnalyzer _rules;
    private readonly ITextAnalyzer _spell;
    private readonly WriteLiteLanguageEngine _engine;
    private readonly WriteLiteIssueMerger _merger = new();
    private readonly IssueRenderingPipeline _issuePipeline = new();
    private readonly UserDictionaryService _dictionary;
    private readonly WriteLiteIgnoreService _ignore;
    private readonly object _progressGate = new();
    private WriteLiteAppSettings _settings = new();
    private TextAnalysisProgress _lastProgress = TextAnalysisProgress.Complete(string.Empty);

    public WriteLiteOrchestratingAnalyzer(
        ITextAnalyzer rules,
        ITextAnalyzer spell,
        WriteLiteLanguageEngine engine,
        UserDictionaryService dictionary,
        WriteLiteIgnoreService ignore)
    {
        _rules = rules;
        _spell = spell;
        _engine = engine;
        _dictionary = dictionary;
        _ignore = ignore;
        _engine.AnalysisProgressChanged += (_, progress) => ReportProgress(progress);
    }

    public WriteLiteLanguageEngine Engine => _engine;

    public UserDictionaryService Dictionary => _dictionary;

    public WriteLiteIgnoreService Ignore => _ignore;

    public event EventHandler<TextAnalysisProgress>? AnalysisProgressChanged;

    public TextAnalysisProgress GetProgress(string text)
    {
        lock (_progressGate)
        {
            return string.Equals(_lastProgress.Text, text ?? string.Empty, StringComparison.Ordinal)
                ? _lastProgress
                : TextAnalysisProgress.Pending(text ?? string.Empty);
        }
    }

    public void ApplySettings(WriteLiteAppSettings settings)
    {
        _settings = settings ?? new WriteLiteAppSettings();
    }

    public IReadOnlyList<TextIssue> Analyze(string text)
        => AnalyzeAsync(text).GetAwaiter().GetResult();

    public async Task<IReadOnlyList<TextIssue>> AnalyzeAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !_settings.CheckingEnabled)
        {
            return [];
        }

        ReportProgress(TextAnalysisProgress.Pending(text));
        _ignore.NotifyText(text);
        var sw = Stopwatch.StartNew();

        // Local analyzers always run (baseline WriteLite).
        var rulesTask = Task.Run(() => _rules.AnalyzeAsync(text, cancellationToken), cancellationToken);
        var spellTask = Task.Run(() => _spell.AnalyzeAsync(text, cancellationToken), cancellationToken);
        var engineTask = _settings.ExtendedChecking
            ? _engine.AnalyzeAsync(text, cancellationToken)
            : Task.FromResult<IReadOnlyList<TextIssue>>([]);

        await Task.WhenAll(rulesTask, spellTask, engineTask).ConfigureAwait(false);

        var rules = await rulesTask.ConfigureAwait(false);
        var spell = await spellTask.ConfigureAwait(false);
        var engine = await engineTask.ConfigureAwait(false);

        // Filter engine orthography hits that are in user dictionary.
        var engineFiltered = engine
            .Where(i => !(i.Category == IssueCategory.Orthography
                          && _dictionary.Contains(i.Original.Trim())))
            .ToList();

        var spellFiltered = spell
            .Where(i => !_dictionary.Contains(i.Original.Trim()))
            .ToList();

        var merged = _merger.Merge(rules, spellFiltered, engineFiltered);
        var filtered = _ignore.Filter(merged.Issues);

        // Also drop dictionary words from final list.
        filtered = filtered
            .Where(i => i.Category != IssueCategory.Orthography
                        || !_dictionary.Contains(i.Original.Trim()))
            .Where(IsCategoryEnabled)
            .Select(SanitizeLocalIssue)
            .Where(i => i is not null)
            .Select(i => i!)
            .ToList();

        filtered = _issuePipeline.Filter(text, filtered).ToList();

        // When "safe apply all" is on, leave CanApplyAutomatically as mapped.
        // When off, allow auto-apply for any issue that has a replacement (still sanitized).
        if (!_settings.SafeApplyAll)
        {
            filtered = filtered
                .Select(i => string.IsNullOrEmpty(i.Replacement)
                    ? i
                    : i with { CanApplyAutomatically = true })
                .ToList();
        }

        sw.Stop();
        CompatibilityLogger.Technical(
            "language-engine-request-completed",
            $"textLength={text.Length} issueCount={filtered.Count} safeCount={filtered.Count(i => i.CanApplyAutomatically)} durationMs={sw.ElapsedMilliseconds} status={_engine.EngineState}");

        var engineProgress = _settings.ExtendedChecking
            ? _engine.GetProgress(text)
            : TextAnalysisProgress.Complete(text);
        ReportProgress(engineProgress.IsComplete
            ? TextAnalysisProgress.Complete(text)
            : new TextAnalysisProgress(
                text,
                engineProgress.CheckedCharacters,
                text.Length,
                IsComplete: false,
                IncompleteReason: engineProgress.IncompleteReason ?? "extended-analysis-incomplete"));

        return filtered;
    }

    private void ReportProgress(TextAnalysisProgress progress)
    {
        lock (_progressGate)
        {
            _lastProgress = progress;
        }

        AnalysisProgressChanged?.Invoke(this, progress);
    }

    private static TextIssue? SanitizeLocalIssue(TextIssue issue)
    {
        var replacement = issue.Replacement;
        if (replacement is not null)
        {
            replacement = TextOutputSanitizer.SanitizeReplacement(issue.Original, replacement);
            if (replacement is null)
            {
                return null;
            }
        }

        var safe = issue.CanApplyAutomatically;
        if (safe && TextOutputSanitizer.MessageContradictsSafeApply(issue.Explanation, safe))
        {
            safe = false;
        }

        if (string.Equals(issue.Original, replacement, StringComparison.Ordinal))
        {
            return null;
        }

        return issue with { Replacement = replacement, CanApplyAutomatically = safe };
    }

    private bool IsCategoryEnabled(TextIssue issue)
    {
        // Repeats
        if (issue.LinguisticCategory == LinguisticIssueCategory.RepeatedWord)
        {
            return _settings.EnableRepeats;
        }

        // Typography (spaces / readability formatting)
        if (issue.Category == IssueCategory.Readability
            || issue.LinguisticCategory is LinguisticIssueCategory.ExtraSpace
                or LinguisticIssueCategory.MissingSpace)
        {
            return _settings.EnableTypography || _settings.EnableStyle;
        }

        return issue.Category switch
        {
            IssueCategory.Orthography => _settings.EnableOrthography,
            IssueCategory.Punctuation => _settings.EnablePunctuation,
            IssueCategory.Grammar => _settings.EnableGrammar,
            IssueCategory.Style => _settings.EnableStyle,
            _ => true
        };
    }
}
