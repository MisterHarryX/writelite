using System.Text.Json.Serialization;

namespace LangBench;

internal sealed class RawItem
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("target")] public string? Target { get; set; }
    [JsonPropertyName("errors")] public List<RawError>? Errors { get; set; }
    [JsonPropertyName("must_not_change")] public List<string>? MustNotChange { get; set; }
}

internal sealed class RawError
{
    [JsonPropertyName("start")] public int Start { get; set; }
    [JsonPropertyName("end")] public int End { get; set; }
    [JsonPropertyName("replacement")] public string? Replacement { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}

internal sealed record GoldError(int Start, int End, string Replacement, string Type);

internal sealed class Item
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public required List<GoldError> Errors { get; init; }
    public required List<string> MustNotChange { get; init; }
}

/// <summary>
/// One prediction that matched no gold error, with enough provenance to cluster it.
/// </summary>
/// <remarks>
/// Aggregate false-positive counts say a layer is noisy; they do not say what kind of
/// noise, which is what a routing rule has to target. Rule ids carry the source — AI
/// findings are prefixed <c>WL-AI-</c>, the spelling layer uses <c>ru.spelling.*</c>, the
/// contextual pass <c>ru.context.*</c> — so a false positive can be attributed to the
/// layer that produced it.
/// </remarks>
internal sealed class FalsePositiveDetail
{
    public required string ItemId { get; init; }
    public required string Category { get; init; }
    public required string Source { get; init; }
    public required string RuleId { get; init; }
    public required string IssueCategory { get; init; }
    public required string Original { get; init; }
    public string? Replacement { get; init; }
    public double Confidence { get; init; }
    public required string Sentence { get; init; }
}

/// <summary>
/// One gold error that was detected, and what happened to its correction.
/// </summary>
/// <remarks>
/// Detection and correction are scored separately, and Phase 3 showed them diverging:
/// consulting the model found 14 more gold errors and produced zero additional exact
/// corrections. An aggregate cannot say why. This records, per detected error, whether the
/// span matched exactly, what was proposed, and what was expected — which localises the
/// loss to span alignment, candidate generation, or replacement quality.
/// </remarks>
internal sealed class CorrectionTraceEntry
{
    public required string ItemId { get; init; }
    public required string Category { get; init; }
    public required string Source { get; init; }
    public bool ExactSpan { get; init; }
    public required string Original { get; init; }
    public string? Predicted { get; init; }
    public required string Expected { get; init; }
    public bool CountedCorrect { get; init; }

    /// <summary>
    /// Whether applying the product's edit yields the sentence the gold expects, regardless
    /// of whether the edit's span is the one the gold annotated.
    /// </summary>
    /// <remarks>
    /// This is the §7 product-level judgement. <see cref="CountedCorrect"/> requires the span
    /// to match exactly, which is the right convention for comparing two annotations and the
    /// wrong one for asking whether the user's text ended up right: a phrase edit
    /// «будем ждали» → «будем ждать» against a gold that annotates only «ждали» produces the
    /// expected sentence and scores as a span mismatch. Phase 4 measured 15 of 34 span
    /// mismatches as already producing the expected text.
    /// </remarks>
    public bool FinalTextCorrect { get; init; }

    /// <summary>Which stage lost this correction, or "correct" when nothing was lost.</summary>
    public required string Outcome { get; init; }
}

/// <summary>
/// The shape of what the pipeline emitted, aggregated. See <see cref="EditQuality"/>.
/// </summary>
internal sealed class EditQualityBlock
{
    public int Edits { get; init; }
    public int SubWordFragments { get; init; }
    public int ZeroLengthNonPunctuationInsertions { get; init; }
    public int PhraseLevelEdits { get; init; }
    public int OverWideEdits { get; init; }
    public int MeaningfulEdits { get; init; }
    public double SubWordFragmentRate { get; init; }
    public double PhraseLevelEditRate { get; init; }
    public double MeaningfulEditRate { get; init; }
    public double OverWideEditRate { get; init; }
}

internal sealed class ItemOutcome
{
    public List<CorrectionTraceEntry> CorrectionTrace { get; init; } = [];
    public required Item Item { get; init; }
    public List<FalsePositiveDetail> FalsePositiveDetails { get; init; } = [];

    /// <summary>Everything the pipeline predicted for this item, for source attribution.</summary>
    public required IReadOnlyList<WriteLite.Models.TextIssue> Issues { get; init; }
    public int TokenCount { get; init; }
    public int PredictedCount { get; init; }
    public int GoldCount { get; init; }
    public int TruePositives { get; init; }
    public int FalsePositives { get; init; }
    public int CorrectedExactly { get; init; }
    public int CorrectedFinalText { get; init; }
    public int PreservedTokens { get; init; }
    public required string ProducedText { get; init; }
    public bool TextChanged { get; init; }
    public required SemanticVerdict Semantic { get; init; }
}

internal sealed class Report
{
    public required string Corpus { get; init; }
    public required string CorpusSha256 { get; init; }
    public required string CorpusSha256Lf { get; init; }
    public int ItemCount { get; init; }
    public required string RunUtc { get; init; }
    public required string Label { get; init; }
    public required IReadOnlyList<string> Layers { get; init; }
    public required string Configuration { get; init; }

    /// <summary>Items abandoned after <see cref="Harness.PerItemTimeout"/>. Non-zero
    /// invalidates recall for this run: those items were never actually analysed.</summary>
    public int TimedOutItems { get; init; }
    public required MetricBlock Overall { get; init; }
    public required Dictionary<string, MetricBlock> PerCategory { get; init; }
    public required FalsePositiveBlock FalsePositives { get; init; }
    public required RateBlock NoChangeAccuracy { get; init; }
    public required SemanticBlock SemanticCorruption { get; init; }
    public required TimingBlock Timings { get; init; }

    /// <summary>Every false positive, with the layer that produced it.</summary>
    public required List<FalsePositiveDetail> FalsePositives_Detail { get; init; }

    /// <summary>Per-layer attribution of findings and false positives.</summary>
    public required Dictionary<string, SourceStats> BySource { get; init; }

    /// <summary>§8 edit-shape quality over every finding.</summary>
    public required EditQualityBlock EditQuality { get; init; }

    /// <summary>§8 edit-shape quality per emitting layer, so a regression is attributable.</summary>
    public required Dictionary<string, EditQualityBlock> EditQualityBySource { get; init; }

    /// <summary>Why detected errors did not become exact corrections.</summary>
    public required Dictionary<string, int> CorrectionOutcomes { get; init; }

    /// <summary>The per-error correction trace.</summary>
    public required List<CorrectionTraceEntry> CorrectionTrace { get; init; }

    /// <summary>How often the model was consulted, and how often it was believed.</summary>
    public RoutingBlock? Routing { get; init; }
}

/// <summary>§18 routing metrics.</summary>
internal sealed class RoutingBlock
{
    public long SentencesSeen { get; init; }
    public long SentencesRouted { get; init; }
    public double RoutedFraction { get; init; }
    public long FindingsOffered { get; init; }
    public long FindingsAccepted { get; init; }
    public double AcceptedFraction { get; init; }
    public double AiCallsPerSentence { get; init; }
    public required Dictionary<string, long> RoutingReasons { get; init; }
    public required Dictionary<string, long> RejectionReasons { get; init; }
    public required Dictionary<string, long> OfferedBySupportClass { get; init; }
    public required Dictionary<string, long> AcceptedBySupportClass { get; init; }
}

/// <summary>What one layer contributed, and what it cost.</summary>
internal sealed class SourceStats
{
    public int Predicted { get; init; }
    public int TruePositives { get; init; }
    public int FalsePositives { get; init; }
    public double Precision { get; init; }
}

internal sealed class MetricBlock
{
    public int Items { get; init; }
    public int Tokens { get; init; }
    public int GoldErrors { get; init; }
    public int Predicted { get; init; }
    public int TruePositives { get; init; }
    public int FalsePositives { get; init; }
    public int FalseNegatives { get; init; }
    public double Precision { get; init; }
    public double Recall { get; init; }
    public double F1 { get; init; }

    /// <summary>
    /// Unchanged since Phase 2: exact span and exact replacement. Kept under its original
    /// name so historical reports stay directly comparable.
    /// </summary>
    public double CorrectionAccuracy { get; init; }

    /// <summary>The same number as <see cref="CorrectionAccuracy"/>, named for what it is.</summary>
    public double StrictExactCorrectionAccuracy { get; init; }

    /// <summary>
    /// Corrections that produce the expected sentence, whatever span they used. Always at
    /// least the strict figure. See <see cref="CorrectionTraceEntry.FinalTextCorrect"/>.
    /// </summary>
    public double FinalTextCorrectionAccuracy { get; init; }

    public double NoChangeAccuracy { get; init; }
    public int SemanticCorruptions { get; init; }
}

internal sealed class FalsePositiveBlock
{
    public int Sentences { get; init; }
    public int Tokens { get; init; }
    public int FalsePositiveIssues { get; init; }
    public int SentencesWithAnyIssue { get; init; }
    public double PerHundredTokens { get; init; }
    public double PerSentenceRate { get; init; }
}

internal sealed class RateBlock
{
    public required string Scope { get; init; }
    public int Total { get; init; }
    public int Correct { get; init; }
    public double Accuracy { get; init; }
}

internal sealed class SemanticBlock
{
    public required string Scope { get; init; }
    public int Total { get; init; }
    public int Corrupted { get; init; }
    public double Rate { get; init; }
    public required Dictionary<string, int> ByReason { get; init; }
    public required List<SemanticExample> Examples { get; init; }
}

internal sealed class SemanticExample
{
    public required string Category { get; init; }
    public required string Id { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }
    public required string Original { get; init; }
    public required string Produced { get; init; }
}

internal sealed class TimingBlock
{
    public double TotalMs { get; init; }
    public double MsPerSentenceMean { get; init; }
    public double MsPerSentenceP50 { get; init; }
    public double MsPerSentenceP95 { get; init; }
    public double MsPerSentenceP99 { get; init; }
    public double MsPerSentenceMax { get; init; }
    public double PeakWorkingSetMb { get; init; }
    public double WorkingSetMb { get; init; }
    public double ManagedHeapMb { get; init; }
}
