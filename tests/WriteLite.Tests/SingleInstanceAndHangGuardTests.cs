using System.Windows.Threading;
using WriteLite.Services;

namespace WriteLite.Tests;

[TestClass]
public sealed class SingleInstanceAndHangGuardTests
{
    [TestMethod]
    public void UiWatchdog_ReportsNormalInitially()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var wd = new UiResponsivenessWatchdog(dispatcher, TimeSpan.FromMilliseconds(200));
        var snap = wd.Snapshot;
        Assert.AreEqual(UiResponsivenessLevel.Normal, snap.Level);
    }

    [TestMethod]
    public void DebouncedAnalyzer_TracksDroppedStale()
    {
        var analyzer = new CountingAnalyzer();
        var runner = new DebouncedTextAnalyzer(analyzer, TimeSpan.FromMilliseconds(5));
        Assert.AreEqual(0, runner.DroppedAsStaleCount);
        Assert.IsLessThanOrEqualTo(1, runner.PendingDebounceCount);
    }

    [TestMethod]
    public void CorrectionOutcome_NeverEmptyUserMessage()
    {
        foreach (CorrectionApplicationStatus status in Enum.GetValues(typeof(CorrectionApplicationStatus)))
        {
            var o = CorrectionApplicationOutcome.FromStatus(status);
            Assert.IsFalse(string.IsNullOrWhiteSpace(o.UserMessage), status.ToString());
        }
    }

    private sealed class CountingAnalyzer : ITextAnalyzer
    {
        public IReadOnlyList<Models.TextIssue> Analyze(string text) => [];
        public Task<IReadOnlyList<Models.TextIssue>> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Models.TextIssue>>([]);
    }
}
