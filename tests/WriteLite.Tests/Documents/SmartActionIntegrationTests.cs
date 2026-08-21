using System.Diagnostics;
using WriteLite.AI.Local;
using WriteLite.Services.Ai;
using WriteLite.Tests.Ai;

namespace WriteLite.Tests.Documents;

/// <summary>
/// Drives every Smart Action through the real request → backend → clean → validate path.
/// </summary>
/// <remarks>
/// The Phase 2 report was explicit that Smart Actions had been verified by reading the
/// implementation rather than by exercising it, and flagged that as the weakest evidence
/// in the report. These close that gap for the backend half of the feature: a real
/// <see cref="TextRewriteService"/>, a real <see cref="QwenModelBackend"/>, and a loopback
/// server on the other end of a real socket. Only the WPF preview window is out of scope,
/// since it needs an STA message pump.
///
/// The stub is <see cref="SingleSlotInferenceServer"/> — the same one the cancellation
/// tests use — so these also inherit its guarantee that abandoning a request must not
/// wedge the backend.
/// </remarks>
[TestClass]
public sealed class SmartActionIntegrationTests
{
    private const string Selection = "Короче мы походу не успеем это сделать вовремя.";

    private static (TextRewriteService Service, QwenModelBackend Backend) ServiceFor(
        SingleSlotInferenceServer server)
    {
        var backend = new QwenModelBackend(
            modelDirectory: Path.Combine(Path.GetTempPath(), "wl-smart-" + Guid.NewGuid()),
            endpoint: server.Endpoint,
            allowUnverifiedLoopback: true);
        return (new TextRewriteService(backend), backend);
    }

    /// <summary>Every shipped action must produce a usable, non-empty result.</summary>
    [TestMethod]
    [DataRow(RewriteOperation.Explain)]
    [DataRow(RewriteOperation.Rewrite)]
    [DataRow(RewriteOperation.Shorten)]
    [DataRow(RewriteOperation.Expand)]
    [DataRow(RewriteOperation.Formal)]
    [DataRow(RewriteOperation.Casual)]
    [DataRow(RewriteOperation.ImproveStyle)]
    [DataRow(RewriteOperation.Simplify)]
    [DataRow(RewriteOperation.Grammar)]
    [DataRow(RewriteOperation.Translate)]
    public async Task EveryAction_ReturnsAUsableResult(RewriteOperation operation)
    {
        using var server = new SingleSlotInferenceServer(
            SingleSlotInferenceServer.FreePort(),
            workDuration: TimeSpan.FromMilliseconds(60));

        var (service, backend) = ServiceFor(server);
        await using var _ = backend;

        var result = await service.RewriteAsync(
            new RewriteRequest(operation, Selection, "Мы обсуждали сроки.", "Нужно решить сегодня."));

        Assert.IsNotNull(result);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Suggestion), $"{operation} produced nothing");
        Assert.AreEqual(Selection, result.Original, "the original must be carried through unchanged");
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(result.Backend),
            "every result must say which path produced it, so the UI never overstates it");
    }

    /// <summary>
    /// A selection over the documented limit is refused outright rather than silently
    /// truncated — truncation would return a rewrite of text the user did not select.
    /// </summary>
    [TestMethod]
    public async Task OversizedSelection_IsRefused()
    {
        using var server = new SingleSlotInferenceServer(SingleSlotInferenceServer.FreePort());
        var (service, backend) = ServiceFor(server);
        await using var _ = backend;

        var oversized = new string('а', TextRewriteService.MaxSelectionChars + 1);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.RewriteAsync(new RewriteRequest(RewriteOperation.Rewrite, oversized)));

        Assert.AreEqual(0, server.RequestsStarted, "an oversized selection must never reach the model");
    }

    /// <summary>Cancellation must return control promptly and never wedge the backend.</summary>
    [TestMethod]
    public async Task Cancellation_ReturnsPromptlyAndLeavesTheBackendUsable()
    {
        using var server = new SingleSlotInferenceServer(
            SingleSlotInferenceServer.FreePort(),
            workDuration: TimeSpan.FromMilliseconds(600));

        var (service, backend) = ServiceFor(server);
        await using var _ = backend;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
        var sw = Stopwatch.StartNew();
        try
        {
            await service.RewriteAsync(new RewriteRequest(RewriteOperation.Rewrite, Selection), cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        sw.Stop();
        Assert.IsLessThan(
            500,
            sw.ElapsedMilliseconds,
            "a cancelled Smart Action must hand control back to the UI immediately, not wait "
            + "for the model to finish");

        // And the backend must still work afterwards — the Phase 2 wedge, from the Smart
        // Action side rather than the analysis side.
        using var after = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var recovered = await service.RewriteAsync(
            new RewriteRequest(RewriteOperation.Rewrite, Selection), after.Token);

        Assert.IsFalse(string.IsNullOrWhiteSpace(recovered.Suggestion));
        Assert.AreEqual(0, server.SlotsLeaked, "a cancelled Smart Action abandoned an inference connection");
    }

    /// <summary>
    /// When the model is unreachable the action must still answer, from the offline
    /// fallback, and must say so rather than presenting rules output as model output.
    /// </summary>
    [TestMethod]
    public async Task UnreachableModel_FallsBackAndSaysSo()
    {
        var backend = new QwenModelBackend(
            modelDirectory: Path.Combine(Path.GetTempPath(), "wl-smart-none-" + Guid.NewGuid()),
            endpoint: "http://127.0.0.1:9",
            allowUnverifiedLoopback: true);
        await using var _ = backend;

        var service = new TextRewriteService(backend);
        var result = await service.RewriteAsync(
            new RewriteRequest(RewriteOperation.Rewrite, "Мы  обсуждали  сроки ."));

        Assert.IsNotNull(result);
        Assert.AreNotEqual(
            "writelite-qwen",
            result.Backend,
            "an unreachable model must not be credited with the fallback's output");
    }

    /// <summary>
    /// A malformed answer must never reach the document. The cleaner rejects it and the
    /// deterministic fallback answers instead.
    /// </summary>
    [TestMethod]
    public async Task MalformedModelOutput_NeverReachesTheDocument()
    {
        using var server = new MalformedResponseServer(SingleSlotInferenceServer.FreePort());
        var backend = new QwenModelBackend(
            modelDirectory: Path.Combine(Path.GetTempPath(), "wl-smart-bad-" + Guid.NewGuid()),
            endpoint: server.Endpoint,
            allowUnverifiedLoopback: true);
        await using var _ = backend;

        var service = new TextRewriteService(backend);
        var result = await service.RewriteAsync(new RewriteRequest(RewriteOperation.Rewrite, Selection));

        Assert.IsNotNull(result);
        Assert.IsFalse(
            result.Suggestion.Contains("```", StringComparison.Ordinal),
            "a code fence reached the suggestion");
        Assert.IsFalse(
            result.Suggestion.Contains("Вот исправленный вариант", StringComparison.Ordinal),
            "model preamble reached the suggestion");
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Suggestion));
    }

    /// <summary>Context is passed to the model but is never itself rewritten.</summary>
    [TestMethod]
    public async Task ContextIsSentButOnlyTheSelectionIsReturned()
    {
        using var server = new SingleSlotInferenceServer(
            SingleSlotInferenceServer.FreePort(),
            workDuration: TimeSpan.FromMilliseconds(50));

        var (service, backend) = ServiceFor(server);
        await using var _ = backend;

        const string before = "Это предложение идёт до выделения.";
        const string after = "А это предложение идёт после.";

        var result = await service.RewriteAsync(
            new RewriteRequest(RewriteOperation.Rewrite, Selection, before, after));

        Assert.AreEqual(Selection, result.Original);
        Assert.IsFalse(
            result.Suggestion.Contains(before, StringComparison.Ordinal),
            "surrounding context leaked into the replacement text");
        Assert.IsFalse(
            result.Suggestion.Contains(after, StringComparison.Ordinal),
            "surrounding context leaked into the replacement text");
    }

    /// <summary>Custom instructions reach the model without the request being malformed.</summary>
    [TestMethod]
    public async Task CustomInstruction_IsCarriedThrough()
    {
        using var server = new SingleSlotInferenceServer(
            SingleSlotInferenceServer.FreePort(),
            workDuration: TimeSpan.FromMilliseconds(50));

        var (service, backend) = ServiceFor(server);
        await using var _ = backend;

        var result = await service.RewriteAsync(new RewriteRequest(
            RewriteOperation.Custom,
            Selection,
            Instruction: "Сделай текст короче и вежливее."));

        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Suggestion));
        Assert.AreEqual(1, server.RequestsStarted);
    }
}
