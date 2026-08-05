using WriteLite.Models;
using WriteLite.Services;

namespace WriteLite.Tests;

[TestClass]
public sealed class ResponsivenessTests
{
    [TestMethod]
    public async Task RapidChangesDoNotStartParallelAnalyses()
    {
        var analyzer = new SlowAnalyzer(TimeSpan.FromMilliseconds(25));
        var runner = new DebouncedTextAnalyzer(analyzer, TimeSpan.FromMilliseconds(40));

        var tasks = Enumerable.Range(0, 20)
            .Select(index => runner.StartAsync("text " + index))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.AreEqual(1, runner.StartedCount);
        Assert.AreEqual(1, runner.CompletedCount);
        Assert.AreEqual(1, runner.MaxConcurrentAnalyses);
    }

    [TestMethod]
    public async Task PreviousAnalysisIsCancelled()
    {
        var analyzer = new SlowAnalyzer(TimeSpan.FromMilliseconds(300));
        var runner = new DebouncedTextAnalyzer(analyzer, TimeSpan.FromMilliseconds(10));

        var first = runner.StartAsync("first");
        await Task.Delay(60);
        var second = runner.StartAsync("second");

        var firstResult = await first;
        var secondResult = await second;

        Assert.IsNull(firstResult);
        Assert.IsNotNull(secondResult);
        Assert.IsGreaterThanOrEqualTo(runner.CancelledCount, 1);
    }

    [TestMethod]
    public async Task StaleResultDoesNotPublishAsCurrent()
    {
        var analyzer = new NonCancellableSlowAnalyzer(TimeSpan.FromMilliseconds(180));
        var runner = new DebouncedTextAnalyzer(analyzer, TimeSpan.FromMilliseconds(10));

        var first = runner.StartAsync("first");
        await Task.Delay(40);
        var second = runner.StartAsync("second");

        var firstResult = await first;
        var secondResult = await second;

        Assert.IsTrue(firstResult is null || firstResult.IsStale);
        Assert.IsNotNull(secondResult);
        Assert.IsFalse(secondResult.IsStale);
    }

    [TestMethod]
    public void RepeatedCorrectionClickDoesNotEnterSecondOperation()
    {
        var gate = new AsyncOperationGate();

        Assert.IsTrue(gate.TryEnter());
        Assert.IsFalse(gate.TryEnter());

        gate.Exit();
        Assert.IsTrue(gate.TryEnter());
    }

    [TestMethod]
    public void WriteLiteProcessIdIsIgnored()
    {
        Assert.IsTrue(TextFieldMonitor.IsWriteLiteProcess(42, 42));
        Assert.IsFalse(TextFieldMonitor.IsWriteLiteProcess(43, 42));
        Assert.IsTrue(TextFieldMonitor.IsWriteLiteProcess(43, 42, "WriteLite"));
        Assert.IsFalse(TextFieldMonitor.IsWriteLiteProcess(43, 42, "notepad"));
    }

    [TestMethod]
    public async Task CorrectionSuppressionPreventsImmediateLoop()
    {
        var monitor = new TextFieldMonitor(System.Windows.Threading.Dispatcher.CurrentDispatcher, new SlowAnalyzer(TimeSpan.Zero));

        monitor.BeginApplyingCorrection();
        Assert.IsTrue(monitor.IsSuppressed);

        monitor.EndApplyingCorrection();
        Assert.IsTrue(monitor.IsSuppressed);

        await Task.Delay(600);
        Assert.IsFalse(monitor.IsSuppressed);
    }

    [TestMethod]
    public async Task LongTextAnalysisCanBeCancelled()
    {
        var analyzer = new SlowAnalyzer(TimeSpan.FromSeconds(5));
        var runner = new DebouncedTextAnalyzer(analyzer, TimeSpan.Zero);

        using var cancellation = new CancellationTokenSource(50);
        var result = await runner.StartAsync(new string('x', 10000), cancellation.Token);

        Assert.IsNull(result);
        Assert.IsGreaterThanOrEqualTo(runner.CancelledCount, 1);
    }

    [TestMethod]
    public async Task AnalysisStartDoesNotBlockCallingThread()
    {
        var analyzer = new SlowAnalyzer(TimeSpan.FromMilliseconds(250));
        var runner = new DebouncedTextAnalyzer(analyzer, TimeSpan.Zero);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var task = runner.StartAsync("пливет");
        stopwatch.Stop();

        Assert.IsLessThan(50, stopwatch.ElapsedMilliseconds);
        var result = await task;
        Assert.IsNotNull(result);
    }

    private sealed class SlowAnalyzer(TimeSpan delay) : ITextAnalyzer
    {
        public IReadOnlyList<TextIssue> Analyze(string text) => [];

        public async Task<IReadOnlyList<TextIssue>> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return [];
        }
    }

    private sealed class NonCancellableSlowAnalyzer(TimeSpan delay) : ITextAnalyzer
    {
        public IReadOnlyList<TextIssue> Analyze(string text) => [];

        public async Task<IReadOnlyList<TextIssue>> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay);
            return [];
        }
    }

}
