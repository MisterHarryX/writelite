using WriteLite.Models;
using WriteLite.Services;

namespace WriteLite.Tests.Corrections;

/// <summary>
/// An analyzer that counts what it was asked and can be told what to answer.
/// </summary>
/// <remarks>
/// The counters are the point. "Apply must not call the model" is only a real guarantee if
/// something fails when it does, and a spy that records call counts is the only way to state
/// that as a test rather than as a comment.
/// </remarks>
internal sealed class CountingAnalyzer : ITextAnalyzer
{
    private readonly Func<string, IReadOnlyList<TextIssue>> _answer;

    public CountingAnalyzer(Func<string, IReadOnlyList<TextIssue>>? answer = null)
        => _answer = answer ?? (_ => []);

    public int Calls;

    public IReadOnlyList<TextIssue> Analyze(string text)
    {
        Interlocked.Increment(ref Calls);
        return _answer(text);
    }

    public Task<IReadOnlyList<TextIssue>> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(Analyze(text));
}

/// <summary>
/// A staged analyzer whose deep lane blocks until released, and counts its model calls.
/// </summary>
/// <remarks>
/// Models the shipping stack's shape: a deterministic answer available in milliseconds behind
/// a lane that takes tens of seconds. <see cref="Release"/> lets a test decide exactly when
/// the slow answer lands, including "after the user has applied something".
/// </remarks>
internal sealed class StagedSpyAnalyzer : IStagedTextAnalyzer
{
    private readonly Func<string, IReadOnlyList<TextIssue>> _deterministic;
    private readonly Func<string, IReadOnlyList<TextIssue>> _deep;
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StagedSpyAnalyzer(
        Func<string, IReadOnlyList<TextIssue>> deterministic,
        Func<string, IReadOnlyList<TextIssue>>? deep = null,
        bool blockDeepLane = false)
    {
        _deterministic = deterministic;
        _deep = deep ?? deterministic;
        if (!blockDeepLane) _gate.TrySetResult();
    }

    /// <summary>How many times the slow lane was entered. Must stay zero across an apply.</summary>
    public int DeepLaneCalls;

    /// <summary>How many times any analysis was started at all.</summary>
    public int AnalysisCalls;

    public void Release() => _gate.TrySetResult();

    public IReadOnlyList<TextIssue> Analyze(string text)
        => AnalyzeAsync(text).GetAwaiter().GetResult();

    public Task<IReadOnlyList<TextIssue>> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
        => AnalyzeStagedAsync(text, static (_, _) => { }, cancellationToken);

    public async Task<IReadOnlyList<TextIssue>> AnalyzeStagedAsync(
        string text,
        Action<AnalysisLane, IReadOnlyList<TextIssue>> publish,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref AnalysisCalls);
        publish(AnalysisLane.Deterministic, _deterministic(text));

        await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        Interlocked.Increment(ref DeepLaneCalls);
        return _deep(text);
    }
}
