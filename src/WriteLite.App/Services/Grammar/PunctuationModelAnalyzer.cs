using WriteLite.AI.Local;
using WriteLite.Models;

namespace WriteLite.Services.Grammar;

/// <summary>
/// Turns the punctuation classifier's per-boundary probabilities into findings, and refuses
/// to offer any that a deterministic layer has already covered.
/// </summary>
/// <remarks>
/// <para>Phase 6 integration, behind <see cref="Enabled"/>. The question this exists to answer
/// is whether the trained model improves the real pipeline, and that cannot be answered from
/// a standalone accuracy number — a classifier can score well on held-out boundaries and
/// still make WriteLite worse by re-proposing what the rules already found, or by inserting
/// commas into clean text. Both are measured through this class rather than around it.</para>
///
/// <para><b>Three refusals, in order of how much they matter.</b></para>
///
/// <para><b>It never removes a comma.</b> Only boundaries with no comma are scored. Removing
/// punctuation the writer chose is a different decision with a much higher cost of being
/// wrong, and no evidence in this phase speaks to it.</para>
///
/// <para><b>It yields to the rules layer.</b> A boundary already carrying a rule finding is
/// dropped, not merged. The rules were measured at zero false positives across 841 items and
/// carry a specific linguistic explanation; the model carries a probability. Where they
/// overlap, the explanation is worth more to the user than the second opinion.</para>
///
/// <para><b>It applies nothing automatically.</b> <c>CanApplyAutomatically</c> is false for
/// every finding here. The deterministic comma rules earn that flag by being closed lists
/// with stated exceptions; a probability above a threshold is a different kind of claim.</para>
/// </remarks>
public sealed class PunctuationModelAnalyzer : IDisposable
{
    private readonly PunctuationDecisionModel _model;
    private bool _disposed;

    private PunctuationModelAnalyzer(PunctuationDecisionModel model, double threshold)
    {
        _model = model;
        Threshold = threshold;
    }

    /// <summary>Loads the analyzer, or returns null when no punctuation model is deployed.</summary>
    public static PunctuationModelAnalyzer? TryLoad(string? modelDirectory = null, double? threshold = null)
    {
        var model = PunctuationDecisionModel.TryLoad(modelDirectory);
        return model is null ? null : new PunctuationModelAnalyzer(model, threshold ?? model.AcceptanceThreshold);
    }

    /// <summary>The COMMA probability at or above which a boundary becomes a finding.</summary>
    public double Threshold { get; }

    public string ModelVersion => _model.ModelVersion;
    public TimeSpan LoadTime => _model.LoadTime;

    /// <summary>
    /// The longest sentence the model is asked about.
    /// </summary>
    /// <remarks>
    /// Past this the boundaries fall outside the encoder's 64-token window and the model is
    /// scoring a truncated sentence — the same defect the reranker diagnosis found, and there
    /// is no reason to reproduce it knowingly. Long sentences keep their rule findings.
    /// </remarks>
    private const int MaxSentenceLength = 300;

    /// <summary>
    /// Extra punctuation findings for <paramref name="text"/>, given what other layers found.
    /// </summary>
    /// <remarks>
    /// <para>Text longer than one sentence is split and scored a sentence at a time, with the
    /// findings mapped back to document offsets. Phase 6 shipped this class with a flat
    /// <c>text.Length &gt; MaxSentenceLength</c> return, which was correct for a benchmark
    /// whose every item is one sentence and silently correct for nothing else: on any real
    /// document the layer produced nothing at all, and produced it without any signal that it
    /// had stood down.</para>
    ///
    /// <para>The per-sentence cap itself stays, and applies to each sentence rather than to
    /// the document. A sentence longer than <see cref="MaxSentenceLength"/> has boundaries
    /// outside the encoder's 64-token window, so the model would be scoring a truncated
    /// string — the defect the Phase 6 reranker diagnosis found, and there is no reason to
    /// reproduce it knowingly. Those sentences keep their rule findings.</para>
    /// </remarks>
    public IReadOnlyList<TextIssue> Analyze(
        string text,
        IReadOnlyList<TextIssue> existingIssues,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text)) return [];

        List<TextIssue>? findings = null;
        foreach (var (start, length) in Sentences(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (length == 0 || length > MaxSentenceLength) continue;

            var sentence = text.Substring(start, length);
            var boundaries = PunctuationDecisionModel.CandidateBoundaries(sentence)
                .Where(p => !ProtectedTextSpans.Overlaps(start + p, 1, protectedSpans))
                .Where(p => !AlreadyCovered(start + p, existingIssues))
                .ToList();
            if (boundaries.Count == 0) continue;

            foreach (var decision in _model.Score(sentence, boundaries, cancellationToken))
            {
                if (!decision.Accepted(Threshold)) continue;

                // Reported as an insertion at the end of the preceding word — the same shape
                // the word-aware diff gives a missing comma, and the shape that keeps
                // subword_fragment_rate at zero. A span that began mid-word would be exactly
                // the defect Phase 5 removed from the AI path.
                var wordEnd = decision.Position;
                var wordStart = wordEnd;
                while (wordStart > 0 && char.IsLetter(sentence[wordStart - 1])) wordStart--;
                if (wordStart == wordEnd) continue;

                var word = sentence[wordStart..wordEnd];
                (findings ??= []).Add(new TextIssue(
                    start + wordStart,
                    word.Length,
                    word,
                    word + ",",
                    "Здесь может требоваться запятая",
                    "Модель пунктуации оценивает эту границу как место запятой. "
                    + "Проверьте, разделяет ли она части предложения.",
                    IssueCategory.Punctuation,
                    IssueSeverity.Suggestion,
                    CanApplyAutomatically: false,
                    RuleId: "ru.punctuation.model-comma",
                    LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation,
                    Confidence: Math.Round(decision.Probability, 3)));
            }
        }

        return (IReadOnlyList<TextIssue>?)findings ?? [];
    }

    /// <summary>Sentence spans, as (start, length) over the original text.</summary>
    /// <remarks>
    /// Split on terminators and on line breaks. A line break ends a sentence for this purpose
    /// even without a terminator, because a list item or a chat line is a unit the model
    /// should see whole rather than glued to the next one.
    /// </remarks>
    private static IEnumerable<(int Start, int Length)> Sentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is not ('.' or '!' or '?' or '…' or '\n' or '\r')) continue;

            // Consume a run of terminators so «Правда?!» is one boundary, not two.
            var end = i;
            while (end < text.Length && text[end] is '.' or '!' or '?' or '…' or '\n' or '\r') end++;

            yield return (start, Trim(text, start, end - start));
            start = end;
            i = end - 1;
        }

        if (start < text.Length) yield return (start, Trim(text, start, text.Length - start));
    }

    /// <summary>Length of the span with leading and trailing non-letter noise removed.</summary>
    private static int Trim(string text, int start, int length)
    {
        var end = start + length;
        while (end > start && !char.IsLetterOrDigit(text[end - 1])) end--;
        return Math.Max(0, end - start);
    }

    /// <summary>True when some other layer already has an opinion about this boundary.</summary>
    private static bool AlreadyCovered(int position, IReadOnlyList<TextIssue> existingIssues)
    {
        foreach (var issue in existingIssues)
        {
            // A comma insertion is reported on the word before the boundary, so an issue
            // ending at the boundary is about this boundary.
            var end = issue.Start + issue.Length;
            if (position >= issue.Start - 1 && position <= end + 1) return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model.Dispose();
    }
}
