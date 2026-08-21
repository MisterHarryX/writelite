using System.Text;
using WriteLite.Services.Ai;

namespace WriteLite.Services.Diagnostics;

/// <summary>
/// TEMPORARY instrumentation: one record of what a single Check actually did.
/// </summary>
/// <remarks>
/// <para>This exists to answer one question that no existing signal could answer — <em>where
/// between the editor and the sidebar does a document full of errors become zero issues</em>.
/// The status indicator says «ЛОКАЛЬНАЯ ПРОВЕРКА» whether or not any model was consulted, the
/// availability flags say the model is reachable whether or not it was asked, and the
/// per-layer technical log lines are interleaved across concurrent analyses so they cannot be
/// attributed to one check. This record is per-check and covers the whole path.</para>
///
/// <para><b>Off unless asked for.</b> <see cref="CheckPipelineTracing.Enabled"/> defaults to
/// the <c>WRITELITE_CHECK_TRACE</c> environment variable, so a normal run pays for one static
/// boolean read per check and nothing else.</para>
///
/// <para><b>No user text, ever.</b> Lengths, counts, spans, categories and machine-stable
/// reason codes only — the same discipline <see cref="AiRoutingTrace"/> already follows. A
/// diagnostic that quotes what someone wrote is a privacy defect however useful it is.</para>
/// </remarks>
public sealed class CheckPipelineTrace
{
    private readonly List<string> _notes = [];
    private readonly List<AiFindingTrace> _aiFindings = [];

    // (1) Which document this check was about.
    public string DocumentId { get; set; } = "unknown";

    public string DocumentKind { get; set; } = "unknown";

    public bool IsRestoredDocument { get; set; }

    // (2) What the editor handed to the analyzer.
    public int EditorTextLength { get; set; }

    public int AnalyzedSegmentLength { get; set; }

    // (3) Whether the check ran at all.
    public bool CheckStarted { get; set; }

    public string? CheckAbortReason { get; set; }

    /// <summary>The concrete analyzer the editor used, so a stub cannot masquerade as the stack.</summary>
    public string AnalyzerKind { get; set; } = "unknown";

    // (4) Deterministic layer.
    public int DeterministicFindings { get; set; }

    /// <summary>
    /// True once a layer that can tell deterministic findings apart from AI ones has reported.
    /// </summary>
    /// <remarks>
    /// Only the hybrid service knows that split. When the editor is running an analyzer that
    /// has no AI stage at all, it fills the count in itself — and this flag is how it knows
    /// not to overwrite a real answer with its own coarser one.
    /// </remarks>
    public bool DeterministicReported { get; set; }

    // (5)(6) Routing.
    public int AiRoutingCandidates { get; set; }

    public bool AiConsulted { get; set; }

    public string AiRoutingReason { get; set; } = "not-reached";

    // (7) Real inference calls, measured at the wire.
    public long QwenCallsBefore { get; set; }

    public long QwenCallsAfter { get; set; }

    public long QwenCalls => Math.Max(0, QwenCallsAfter - QwenCallsBefore);

    // (8)(9) What came back and what survived parsing.
    public int AiRawResults { get; set; }

    public int AiParsedFindings { get; set; }

    // (10) Acceptance, per finding.
    public IReadOnlyList<AiFindingTrace> AiFindings => _aiFindings;

    public int AiAccepted { get; set; }

    public int AiRejected => Math.Max(0, AiParsedFindings - AiAccepted);

    // (11)(12)(13) Aggregation and delivery.
    public int MergedAfterDeduplication { get; set; }

    public int DeliveredToViewModel { get; set; }

    public int RenderedInSidebar { get; set; }

    public IReadOnlyList<string> Notes => _notes;

    public void Note(string note) => _notes.Add(note);

    public void RecordAiFinding(AiFindingTrace finding) => _aiFindings.Add(finding);

    /// <summary>The whole record as one privacy-safe block, numbered as the investigation asked.</summary>
    public override string ToString()
    {
        var report = new StringBuilder();
        report.AppendLine("── check-pipeline-trace ────────────────────────────────");
        report.AppendLine($" 1. document        id={DocumentId} kind={DocumentKind} restored={(IsRestoredDocument ? "yes" : "no")}");
        report.AppendLine($" 2. editor text     length={EditorTextLength} analyzedSegment={AnalyzedSegmentLength}");
        report.AppendLine($" 3. check started   {(CheckStarted ? "yes" : "no")}"
                          + (CheckAbortReason is null ? string.Empty : $" abort={CheckAbortReason}")
                          + $" analyzer={AnalyzerKind}");
        report.AppendLine($" 4. deterministic   findings={DeterministicFindings}");
        report.AppendLine($" 5. ai candidates   {AiRoutingCandidates}");
        report.AppendLine($" 6. routing         consulted={(AiConsulted ? "yes" : "no")} reason={AiRoutingReason}");
        report.AppendLine($" 7. qwen calls      {QwenCalls} (process total {QwenCallsBefore} -> {QwenCallsAfter})");
        report.AppendLine($" 8. ai raw results  {AiRawResults}");
        report.AppendLine($" 9. ai parsed       {AiParsedFindings}");
        report.AppendLine($"10. ai accepted={AiAccepted} rejected={AiRejected}");

        foreach (var finding in _aiFindings)
        {
            report.AppendLine(
                $"      [{finding.Start},{finding.Length}] {finding.Category} class={finding.SupportClass} "
                + $"conf={finding.Confidence:F2} required={finding.RequiredConfidence:F2} "
                + $"{(finding.Accepted ? "ACCEPTED" : "REJECTED")} reason={finding.Reason}");
        }

        report.AppendLine($"11. after dedup     {MergedAfterDeduplication}");
        report.AppendLine($"12. to view model   {DeliveredToViewModel}");
        report.AppendLine($"13. in sidebar      {RenderedInSidebar}");

        foreach (var note in _notes)
        {
            report.AppendLine($"    note: {note}");
        }

        report.Append("────────────────────────────────────────────────────────");
        return report.ToString();
    }
}

/// <summary>
/// TEMPORARY: the ambient trace for the check currently running on this async flow.
/// </summary>
/// <remarks>
/// <see cref="AsyncLocal{T}"/> rather than a parameter threaded through every layer, because
/// the path crosses <c>Task.Run</c> boundaries in four services and the alternative was to
/// change the signature of <see cref="Services.ITextAnalyzer"/> itself — a permanent change to
/// a core seam in order to answer a temporary question. Async-local state flows into
/// <c>Task.Run</c>, so every layer of one check sees the same record and concurrent checks
/// never see each other's.
/// </remarks>
public static class CheckPipelineTracing
{
    private static readonly AsyncLocal<CheckPipelineTrace?> Ambient = new();

    /// <summary>Whether checks are traced. Defaults from <c>WRITELITE_CHECK_TRACE=1</c>.</summary>
    public static bool Enabled { get; set; } =
        string.Equals(
            Environment.GetEnvironmentVariable("WRITELITE_CHECK_TRACE"),
            "1",
            StringComparison.Ordinal);

    /// <summary>Where a completed record goes. Defaults to the compatibility log.</summary>
    public static Action<CheckPipelineTrace> Sink { get; set; } =
        static trace => CompatibilityLogger.Technical("check-pipeline-trace", trace.ToString());

    /// <summary>The record for the check on this async flow, or null when tracing is off.</summary>
    public static CheckPipelineTrace? Current => Enabled ? Ambient.Value : null;

    /// <summary>Starts a record. Disposing it publishes the record to <see cref="Sink"/>.</summary>
    public static IDisposable Begin(out CheckPipelineTrace? trace)
    {
        if (!Enabled)
        {
            trace = null;
            return NullScope.Instance;
        }

        var started = new CheckPipelineTrace
        {
            QwenCallsBefore = AI.Local.QwenModelBackend.DispatchedInferenceCalls
        };

        Ambient.Value = started;
        trace = started;
        return new Scope(started);
    }

    private sealed class Scope(CheckPipelineTrace trace) : IDisposable
    {
        public void Dispose()
        {
            trace.QwenCallsAfter = AI.Local.QwenModelBackend.DispatchedInferenceCalls;
            Ambient.Value = null;

            try
            {
                Sink(trace);
            }
            catch
            {
                // A diagnostic must never be the reason a check fails.
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
