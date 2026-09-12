using System.Diagnostics;
using WriteLite.Models;
using WriteLite.Services.Diagnostics;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Settings;

namespace WriteLite.Services.Ai;

/// <summary>What one analysis spent on the model, and where.</summary>
/// <param name="Sentences">Sentences the text was split into.</param>
/// <param name="Considered">Sentences the routing policy was asked about.</param>
/// <param name="Consulted">Sentences that produced an inference.</param>
/// <param name="Offered">Findings the model returned, before acceptance.</param>
/// <param name="Accepted">Findings that survived acceptance.</param>
/// <param name="OutsideTarget">Findings discarded for landing on a context sentence.</param>
/// <param name="CacheHits">Sentences answered from the per-sentence cache.</param>
public sealed record SentenceRoutingReport(
    int Sentences,
    int Considered,
    int Consulted,
    int Offered,
    int Accepted,
    int OutsideTarget,
    int CacheHits)
{
    public static readonly SentenceRoutingReport Empty = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>Inferences per hundred sentences — the number §26 asks to be driven down.</summary>
    public double CallsPerHundredSentences
        => Sentences == 0 ? 0 : 100.0 * Consulted / Sentences;
}

/// <summary>
/// Routes the local model one sentence at a time, with bounded context.
/// </summary>
/// <remarks>
/// <para><b>What this replaces.</b> The hybrid service used to make a single routing decision
/// for whatever text it was handed and then send that whole text to the model. In the field
/// monitor, where the text is one field, that is the same thing as per-sentence routing. In the
/// editor it is not: one confident dative-government fix in the opening line made the entire
/// document "already covered" and the model was never asked about sentence seven — which is
/// exactly the requirement in §25 of the sprint brief, and exactly where the model's measured
/// gains are, since the errors only it can find are the ones no rule fired on.</para>
///
/// <para><b>Bounded context, and only the target sentence is editable.</b> Each consulted
/// sentence is sent inside a three-sentence window so the model can see what the pronouns refer
/// to, and every finding that lands outside the target sentence is discarded before acceptance.
/// That is §24 made structural rather than advisory: a neighbouring sentence can inform a
/// correction and can never receive one. A 300-page document therefore costs the same per
/// consulted sentence as a one-line note, and no sentence is ever rewritten twice.</para>
///
/// <para><b>A budget, spent nearest the caret first.</b> A long document can contain hundreds
/// of sentences the policy would consult, and answering all of them would be neither fast nor
/// useful. The budget caps inferences per analysis and the order is the §50 priority order, so
/// what the reader is looking at is answered and the rest waits for the next pass rather than
/// delaying it.</para>
/// </remarks>
public sealed class SentenceAiRouter
{
    private readonly AiTextAnalysisService _ai;
    private readonly ILocalAiRoutingPolicy? _policy;
    private readonly SentenceAnalysisCache _cache;

    public SentenceAiRouter(
        AiTextAnalysisService ai,
        ILocalAiRoutingPolicy? policy,
        SentenceAnalysisCache? cache = null)
    {
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        _policy = policy;
        _cache = cache ?? new SentenceAnalysisCache();
    }

    /// <summary>Inferences one analysis may spend.</summary>
    /// <remarks>
    /// Eight. A warm inference is tens to hundreds of milliseconds, so this bounds the deep
    /// lane at a couple of seconds for a document of any size — and the lane is asynchronous,
    /// so the bound is on how stale a result can be, not on how long anyone waits. Sentences
    /// beyond it are not dropped; they are simply not this pass's work.
    /// </remarks>
    public int InferenceBudget { get; set; } = 8;

    public SentenceRoutingReport LastReport { get; private set; } = SentenceRoutingReport.Empty;

    /// <summary>
    /// Model findings for <paramref name="text"/>, in document offsets, already accepted.
    /// </summary>
    /// <param name="priorityOffset">
    /// Where the reader is. Sentences are consulted outward from here, so the caret's own
    /// sentence is answered first and a distant page last.
    /// </param>
    public async Task<IReadOnlyList<TextIssue>> RouteAsync(
        string text,
        IReadOnlyList<TextIssue> deterministic,
        WriteLiteAppSettings settings,
        int priorityOffset,
        CancellationToken cancellationToken)
    {
        var trace = CheckPipelineTracing.Current;
        var spans = SentenceWindowBuilder.Split(text);
        if (spans.Count == 0)
        {
            LastReport = SentenceRoutingReport.Empty;
            return [];
        }

        var order = ByDistanceFrom(spans, priorityOffset);
        var accepted = new List<TextIssue>();
        int considered = 0, consulted = 0, offered = 0, outside = 0, cacheHits = 0;

        foreach (var index in order)
        {
            if (consulted >= InferenceBudget) break;
            cancellationToken.ThrowIfCancellationRequested();

            var (start, length) = spans[index];
            if (length < Math.Max(8, settings.AiMinTextLength)) continue;
            if (length > settings.AiMaxTextLength) continue;

            var sentence = text.Substring(start, length);
            var localHere = Rebase(deterministic, start, length);

            considered++;
            var decision = _policy is null
                ? AiRoutingDecision.Yes("routing-disabled")
                : _policy.ShouldConsult(sentence, localHere);
            if (!decision.Consult) continue;

            var window = SentenceWindowBuilder.Around(text, start);
            var prompt = window.IsEmpty ? sentence : window.Joined;
            var targetInPrompt = prompt.IndexOf(window.IsEmpty ? sentence : window.Current, StringComparison.Ordinal);
            if (targetInPrompt < 0)
            {
                // The window could not be reassembled around this sentence; fall back to the
                // sentence alone rather than risk mapping offsets against the wrong string.
                prompt = sentence;
                targetInPrompt = 0;
            }

            var targetLength = window.IsEmpty ? sentence.Length : window.Current.Length;

            IReadOnlyList<TextIssue> findings;
            if (_cache.TryGet(prompt, out var cached))
            {
                findings = cached;
                cacheHits++;
            }
            else
            {
                consulted++;
                var sw = Stopwatch.StartNew();
                try
                {
                    findings = await _ai.AnalyzeAsync(prompt, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // §63: a model failure removes model findings and nothing else.
                    CompatibilityLogger.Technical("sentence-ai-failed", ex);
                    continue;
                }

                sw.Stop();
                trace?.Note($"sentence-ai: index={index} ms={sw.ElapsedMilliseconds} findings={findings.Count}");
                _cache.Set(prompt, findings);
            }

            offered += findings.Count;

            foreach (var finding in findings)
            {
                // Everything outside the target sentence is context and is not editable.
                if (finding.Start < targetInPrompt
                    || finding.Start + finding.Length > targetInPrompt + targetLength)
                {
                    outside++;
                    continue;
                }

                var documentStart = start + (finding.Start - targetInPrompt);
                if (documentStart < 0 || documentStart + finding.Length > text.Length) continue;

                // The span must still say what the model thought it said.
                var actual = text.Substring(documentStart, finding.Length);
                if (!string.Equals(actual, finding.Original, StringComparison.Ordinal)) continue;

                var rebased = finding with { Start = documentStart };
                if (_policy is null)
                {
                    accepted.Add(rebased);
                    continue;
                }

                var verdict = _policy.ShouldAccept(text, rebased, deterministic);
                if (verdict.Accept) accepted.Add(rebased);
            }
        }

        LastReport = new SentenceRoutingReport(
            spans.Count, considered, consulted, offered, accepted.Count, outside, cacheHits);

        CompatibilityLogger.Technical(
            "sentence-ai-routing",
            $"sentences={spans.Count} considered={considered} consulted={consulted} "
            + $"offered={offered} accepted={accepted.Count} outsideTarget={outside} cacheHits={cacheHits}");

        return accepted;
    }

    /// <summary>Deterministic findings that fall inside a sentence, with sentence-local offsets.</summary>
    private static List<TextIssue> Rebase(IReadOnlyList<TextIssue> issues, int start, int length)
    {
        var result = new List<TextIssue>();
        var end = start + length;
        foreach (var issue in issues)
        {
            if (issue.Start < start || issue.Start + issue.Length > end) continue;
            result.Add(issue with { Start = issue.Start - start });
        }

        return result;
    }

    /// <summary>Sentence indices ordered by distance from the reader's position.</summary>
    private static int[] ByDistanceFrom(IReadOnlyList<(int Start, int Length)> spans, int offset)
    {
        var order = new int[spans.Count];
        for (var i = 0; i < spans.Count; i++) order[i] = i;

        Array.Sort(order, (a, b) => Distance(spans[a], offset).CompareTo(Distance(spans[b], offset)));
        return order;
    }

    private static int Distance((int Start, int Length) span, int offset)
    {
        if (offset >= span.Start && offset < span.Start + span.Length) return 0;
        return offset < span.Start ? span.Start - offset : offset - (span.Start + span.Length);
    }
}

/// <summary>
/// Model answers for sentences, keyed by the exact prompt text.
/// </summary>
/// <remarks>
/// <para>The key is the whole prompt — target sentence plus its context — so a sentence whose
/// neighbours changed is re-asked rather than answered from a window that no longer exists.
/// That is the §48 requirement that a cache be context-aware, expressed as the cache key rather
/// than as a rule someone has to remember.</para>
///
/// <para>Bounded and self-dropping for the same reason as the morphology cache: the contents
/// are a pure function of the prompt, so there is nothing to invalidate and no age to track,
/// and dropping the map wholesale keeps the lookup free of bookkeeping.</para>
/// </remarks>
public sealed class SentenceAnalysisCache
{
    private readonly Dictionary<string, IReadOnlyList<TextIssue>> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly int _limit;

    public SentenceAnalysisCache(int limit = 512) => _limit = Math.Max(16, limit);

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public bool TryGet(string prompt, out IReadOnlyList<TextIssue> findings)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(prompt, out findings!);
        }
    }

    public void Set(string prompt, IReadOnlyList<TextIssue> findings)
    {
        lock (_gate)
        {
            if (_entries.Count >= _limit) _entries.Clear();
            _entries[prompt] = findings;
        }
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }
}
