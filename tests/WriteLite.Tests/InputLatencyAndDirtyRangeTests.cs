using System.Diagnostics;
using System.Windows;
using WriteLite.Models;
using WriteLite.Services;

namespace WriteLite.Tests;

[TestClass]
public sealed class InputLatencyAndDirtyRangeTests
{
    [TestMethod]
    public void DirtyRangeFindsSingleUtf16Insertion()
    {
        var range = DirtyTextRangeTracker.Calculate("hello world", "hello, world");
        Assert.AreEqual(5, range.Start);
        Assert.AreEqual(0, range.RemovedLength);
        Assert.AreEqual(1, range.InsertedLength);
    }

    [TestMethod]
    public void DirtyRangeUsesUtf16OffsetsWithEmoji()
    {
        var range = DirtyTextRangeTracker.Calculate("🙂 тваи", "🙂 твои");
        Assert.AreEqual(5, range.Start);
        Assert.AreEqual(1, range.RemovedLength);
        Assert.AreEqual(1, range.InsertedLength);
    }

    [TestMethod]
    public void LongDocumentAnalysisRangeIsBounded()
    {
        var text = new string('a', 50_000);
        var range = DirtyTextRangeTracker.ExpandToSentence(text, new DirtyTextRange(25_000, 0, 1));
        Assert.IsLessThanOrEqualTo(2400, range.Length);
        Assert.IsTrue(range.Start <= 25_000 && range.End >= 25_000);
    }

    [TestMethod]
    public void DirtyMergePreservesAndShiftsUnchangedIssues()
    {
        const string before = "bad. second bad.";
        const string after = "very bad. second bad.";
        var first = Issue(0, 3, "bad", "good", IssueCategory.Orthography);
        var second = Issue(12, 3, "bad", "good", IssueCategory.Orthography);
        var plan = DirtyIssueAnalysis.Plan(before, after);
        var updatedFirst = Issue(5, 3, "bad", "good", IssueCategory.Orthography);
        var merged = DirtyIssueAnalysis.Merge(after, [first, second], [updatedFirst], plan);
        Assert.HasCount(2, merged);
        Assert.AreEqual(5, merged[0].Start);
        Assert.AreEqual(17, merged[1].Start);
    }

    [TestMethod]
    public void MetricsExposeDeterministicPercentilesAndRemainBounded()
    {
        var metrics = new InputLatencyMetrics(32);
        for (var index = 1; index <= 100; index++) metrics.Record(TimeSpan.FromMilliseconds(index));
        var snapshot = metrics.Snapshot();
        Assert.AreEqual(32, snapshot.Count);
        Assert.AreEqual(84, snapshot.P50Milliseconds);
        Assert.AreEqual(99, snapshot.P95Milliseconds);
        Assert.AreEqual(100, snapshot.MaxMilliseconds);
    }

    [TestMethod]
    public void FiveHundredInputSignalsStayBelowHandlerBudget()
    {
        var metrics = new InputLatencyMetrics(512);
        CancellationTokenSource? current = null;
        var version = 0;
        for (var index = 0; index < 500; index++)
        {
            InputSignalScheduler.Signal(ref current, ref version, metrics, (_, _) => { });
        }
        var snapshot = metrics.Snapshot();
        Console.WriteLine($"input-signal samples={snapshot.Count} p50={snapshot.P50Milliseconds:F4} p95={snapshot.P95Milliseconds:F4} max={snapshot.MaxMilliseconds:F4}");
        Assert.IsLessThan(8d, snapshot.P95Milliseconds);
        Assert.AreEqual(500, version);
        current?.Dispose();
    }

    [TestMethod]
    public void GeometryCacheUsesTextVersionAndHostPosition()
    {
        var cache = new InlineGeometryCache();
        var key = Key(1, new Rect(10, 10, 300, 40));
        cache.Set(key, [new Rect(30, 30, 20, 12)]);
        Assert.IsTrue(cache.TryGet(key, out var value));
        Assert.AreEqual(1, value.Count);
        Assert.IsFalse(cache.TryGet(Key(2, key.HostBounds), out _));
        Assert.IsFalse(cache.TryGet(Key(1, new Rect(20, 10, 300, 40)), out _));
    }

    [TestMethod]
    public void DoubleClickPrioritizesCorrectionOverLexicalCard()
    {
        var spelling = Issue(7, 4, "тваи", "твои", IssueCategory.Orthography);
        var style = Issue(0, 14, "Привет тваи мир", null, IssueCategory.Style);
        Assert.AreSame(spelling, CorrectionInteractionResolver.FindIssueAtRange([style, spelling], 7, 4));
        Assert.IsNull(CorrectionInteractionResolver.FindIssueAtRange([spelling], 0, 6));
    }

    [TestMethod]
    public void ZeroLengthPunctuationIsInsertedAtExactUtf16Position()
    {
        const string text = "Привет 🙂 как дела";
        var position = text.IndexOf(" как", StringComparison.Ordinal);
        var issue = Issue(position, 0, string.Empty, ",", IssueCategory.Punctuation);
        Assert.IsTrue(TextCorrectionService.TryApplyManual(text, issue, true, out var corrected, out var caret));
        Assert.AreEqual("Привет 🙂, как дела", corrected);
        Assert.AreEqual(position + 1, caret);
    }

    private static InlineGeometryCacheKey Key(long version, Rect bounds) => new(
        "target", version, "rule", IssueCategory.Orthography, 1, 3, "bad", bounds, 1, 1);

    private static TextIssue Issue(int start, int length, string original, string? replacement, IssueCategory category) => new(
        start, length, original, replacement, "Issue", "Explanation", category, IssueSeverity.Error,
        CanApplyAutomatically: false, RuleId: "test.rule");
}
