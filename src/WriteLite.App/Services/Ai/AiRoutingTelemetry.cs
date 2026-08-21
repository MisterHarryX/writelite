using System.Collections.Concurrent;
using WriteLite.Models;

namespace WriteLite.Services.Ai;

/// <summary>
/// One routing decision, recorded so it can be explained.
/// </summary>
/// <remarks>
/// Carries spans, categories and reasons — never the user's sentence. The point is to be
/// able to answer "why did the model get consulted here, and why was its answer kept or
/// dropped" during development, not to accumulate a record of what anyone wrote.
/// </remarks>
/// <param name="Consulted">Whether an inference was spent.</param>
/// <param name="RoutingReason">Why, in machine-stable form.</param>
/// <param name="TextLength">Length only — never the text.</param>
public sealed record AiRoutingTrace(
    bool Consulted,
    string RoutingReason,
    int TextLength,
    int DeterministicFindings,
    int AiFindingsOffered,
    int AiFindingsAccepted,
    IReadOnlyList<AiFindingTrace> Findings);

/// <param name="Start">Span start, so a decision can be located without quoting text.</param>
public sealed record AiFindingTrace(
    int Start,
    int Length,
    string Category,
    AiSupportClass SupportClass,
    double Confidence,
    double RequiredConfidence,
    bool Accepted,
    string Reason);

/// <summary>
/// Counters for how often the model is consulted and how often it is believed.
/// </summary>
/// <remarks>
/// Phase 3 could say the model cost 8.4× latency and 5.3× the false positives, but not how
/// often it was asked or what fraction of its answers survived — so there was no way to
/// tell an over-consultation problem from a quality problem. These are the numbers that
/// make routing changes measurable rather than plausible.
/// </remarks>
public sealed class AiRoutingMetrics
{
    private long _sentencesSeen;
    private long _sentencesRouted;
    private long _findingsOffered;
    private long _findingsAccepted;
    private readonly ConcurrentDictionary<string, long> _routingReasons = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _rejectionReasons = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<AiSupportClass, long> _offeredByClass = new();
    private readonly ConcurrentDictionary<AiSupportClass, long> _acceptedByClass = new();

    public long SentencesSeen => Interlocked.Read(ref _sentencesSeen);

    public long SentencesRouted => Interlocked.Read(ref _sentencesRouted);

    public long FindingsOffered => Interlocked.Read(ref _findingsOffered);

    public long FindingsAccepted => Interlocked.Read(ref _findingsAccepted);

    /// <summary>Fraction of analysed sentences that cost an inference.</summary>
    public double RoutedFraction => SentencesSeen == 0 ? 0 : (double)SentencesRouted / SentencesSeen;

    /// <summary>Fraction of the model's findings that survived the acceptance policy.</summary>
    public double AcceptedFraction => FindingsOffered == 0 ? 0 : (double)FindingsAccepted / FindingsOffered;

    /// <summary>
    /// Folds one analysis's per-sentence routing counters into the running totals.
    /// </summary>
    /// <remarks>
    /// The counters used to be incremented once per <em>analysis</em>, which made
    /// <see cref="RoutedFraction"/> a fraction of documents rather than of sentences and made
    /// it meaningless for the editor. Routing is decided per sentence now, so the totals are
    /// fed per sentence and the fraction is the number §26 asks to be driven down.
    /// </remarks>
    public void RecordRouting(SentenceRoutingReport report)
    {
        Interlocked.Add(ref _sentencesSeen, report.Considered);
        Interlocked.Add(ref _sentencesRouted, report.Consulted);
        Interlocked.Add(ref _findingsOffered, report.Offered);
        Interlocked.Add(ref _findingsAccepted, report.Accepted);
        _routingReasons.AddOrUpdate("sentence-routing", 1, static (_, value) => value + 1);
    }

    public IReadOnlyDictionary<string, long> RoutingReasons => _routingReasons;

    public IReadOnlyDictionary<string, long> RejectionReasons => _rejectionReasons;

    public IReadOnlyDictionary<AiSupportClass, long> OfferedByClass => _offeredByClass;

    public IReadOnlyDictionary<AiSupportClass, long> AcceptedByClass => _acceptedByClass;

    public void RecordSentence(AiRoutingDecision decision)
    {
        Interlocked.Increment(ref _sentencesSeen);
        if (decision.Consult)
        {
            Interlocked.Increment(ref _sentencesRouted);
        }

        _routingReasons.AddOrUpdate(decision.Reason, 1, static (_, current) => current + 1);
    }

    public void RecordFinding(AiAcceptanceDecision decision)
    {
        Interlocked.Increment(ref _findingsOffered);
        _offeredByClass.AddOrUpdate(decision.Class, 1, static (_, current) => current + 1);

        if (decision.Accept)
        {
            Interlocked.Increment(ref _findingsAccepted);
            _acceptedByClass.AddOrUpdate(decision.Class, 1, static (_, current) => current + 1);
        }
        else
        {
            _rejectionReasons.AddOrUpdate(decision.Reason, 1, static (_, current) => current + 1);
        }
    }

    /// <summary>A single privacy-safe summary line.</summary>
    public override string ToString()
        => $"sentences={SentencesSeen} routed={SentencesRouted} ({RoutedFraction:P1}) "
           + $"offered={FindingsOffered} accepted={FindingsAccepted} ({AcceptedFraction:P1})";
}
