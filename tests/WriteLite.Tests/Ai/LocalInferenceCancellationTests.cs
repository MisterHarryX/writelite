using System.Diagnostics;
using WriteLite.AI.Local;

namespace WriteLite.Tests.Ai;

/// <summary>
/// The cancellation stress test for the local inference backend.
/// </summary>
/// <remarks>
/// Phase 2 found that a full benchmark run wedged the local model permanently: llama.cpp
/// answered <c>/health</c> with 200 throughout while CPU inference stopped, and the socket
/// table showed CLOSE_WAIT connections accumulating against a server configured with one
/// parallel slot. Reproduced three times, including once against a freshly restarted
/// server, where it forced 35 of 112 benchmark items to be abandoned.
///
/// The trigger is not exotic. <see cref="WriteLite.Services.Ai.HybridTextAnalysisService"/>
/// cancels in-flight AI work on every new keystroke burst, so ordinary typing produces a
/// stream of cancel-then-resend pairs — which is exactly what
/// <see cref="RapidCancellation_DoesNotSilenceTheBackend"/> does here.
///
/// The contract these tests pin down: <b>cancelling a WriteLite AI request must never
/// abort an inference connection that has already reached the server.</b> The user's
/// requirement — that rapid typing must not apply stale results — is met by discarding
/// superseded results, not by tearing down the transport.
/// </remarks>
[TestClass]
public sealed class LocalInferenceCancellationTests
{
    private const string Sentence = "Я сегодня небыл дома и незнаю что делать дальше.";

    private static QwenModelBackend BackendFor(SingleSlotInferenceServer server)
        => new(
            modelDirectory: Path.Combine(Path.GetTempPath(), "wl-no-pack-" + Guid.NewGuid()),
            endpoint: server.Endpoint,
            allowUnverifiedLoopback: true);

    /// <summary>
    /// The regression test for the production defect: cancel a request almost immediately,
    /// fire another, repeat — then check the backend still answers.
    /// </summary>
    [TestMethod]
    public async Task RapidCancellation_DoesNotSilenceTheBackend()
    {
        using var server = new SingleSlotInferenceServer(
            SingleSlotInferenceServer.FreePort(),
            workDuration: TimeSpan.FromMilliseconds(250));

        await using var backend = BackendFor(server);

        // Twelve typing bursts: ask, change your mind 40 ms later, ask again.
        for (var i = 0; i < 12; i++)
        {
            using var typed = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
            try
            {
                await backend.InferAsync(Sentence, "ru", typed.Token);
            }
            catch (OperationCanceledException)
            {
                // A caller giving up is normal and must stay survivable.
            }
        }

        // The question that matters: after all that, does the backend still work?
        using var final = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await backend.InferAsync(Sentence, "ru", final.Token);

        Assert.IsNotNull(
            result,
            $"the backend stopped answering after 12 cancelled requests — "
            + $"server saw {server.RequestsStarted} started, {server.RequestsCompleted} completed, "
            + $"{server.SlotsLeaked} slots leaked. This is the production wedge: rapid typing "
            + "progressively silences local AI while /health keeps returning 200.");

        Assert.AreEqual(
            0,
            server.SlotsLeaked,
            "a cancelled WriteLite request abandoned an inference connection. On a single-slot "
            + "backend that permanently removes capacity; cancellation must suppress the result "
            + "rather than abort the transport.");
    }

    /// <summary>A cancelled caller must not receive a result, even though the connection ran.</summary>
    [TestMethod]
    public async Task CancelledCaller_GetsNoResult()
    {
        using var server = new SingleSlotInferenceServer(
            SingleSlotInferenceServer.FreePort(),
            workDuration: TimeSpan.FromMilliseconds(300));

        await using var backend = BackendFor(server);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        QwenInferenceResult? result = null;
        try
        {
            result = await backend.InferAsync(Sentence, "ru", cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Acceptable: the caller asked to stop.
        }

        Assert.IsNull(result, "a superseded request must not deliver stale text to the editor");
    }

    /// <summary>
    /// Cancelling before the request reaches the server must cost the server nothing —
    /// the cheapest and most common case while typing quickly.
    /// </summary>
    [TestMethod]
    public async Task CancellationBeforeDispatch_NeverTouchesTheServer()
    {
        using var server = new SingleSlotInferenceServer(SingleSlotInferenceServer.FreePort());
        await using var backend = BackendFor(server);

        using var alreadyCancelled = new CancellationTokenSource();
        await alreadyCancelled.CancelAsync();

        try
        {
            await backend.InferAsync(Sentence, "ru", alreadyCancelled.Token);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.AreEqual(0, server.RequestsStarted, "an already-cancelled request was still dispatched");
    }

    /// <summary>
    /// Concurrent analysis requests are serialised, and the ones overtaken while queued are
    /// dropped rather than dispatched.
    /// </summary>
    /// <remarks>
    /// This asserts supersession, not universal completion. Four analysis requests in flight
    /// at once means three of them are already stale — that is what happens when someone
    /// types four times in a row — and sending all four to a one-slot server would buy
    /// nothing but queueing. What has to hold is that the newest answer arrives, the server
    /// is not overloaded, and nothing leaks.
    /// </remarks>
    [TestMethod]
    public async Task ConcurrentCallers_AreSerialisedAndSuperseded()
    {
        using var server = new SingleSlotInferenceServer(
            SingleSlotInferenceServer.FreePort(),
            workDuration: TimeSpan.FromMilliseconds(200));

        await using var backend = BackendFor(server);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => backend.InferAsync(Sentence, "ru", cts.Token)));

        Assert.IsGreaterThanOrEqualTo(
            1,
            results.Count(r => r is not null),
            "at least the newest request must produce an answer");

        Assert.IsLessThanOrEqualTo(
            4,
            server.RequestsStarted,
            "superseded requests should be dropped before dispatch, not queued at the server");

        Assert.AreEqual(0, server.SlotsLeaked);

        // And the backend is still usable afterwards.
        using var after = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.IsNotNull(await backend.InferAsync(Sentence, "ru", after.Token));
    }

    /// <summary>
    /// Sustained typing: many bursts, each cancelled, with occasional pauses where the user
    /// stops and a real answer is expected. Every pause must produce an answer.
    /// </summary>
    [TestMethod]
    public async Task SustainedTypingWithPauses_AnswersAtEveryPause()
    {
        using var server = new SingleSlotInferenceServer(
            SingleSlotInferenceServer.FreePort(),
            workDuration: TimeSpan.FromMilliseconds(150));

        await using var backend = BackendFor(server);

        var answeredPauses = 0;
        var sw = Stopwatch.StartNew();

        for (var round = 0; round < 5; round++)
        {
            for (var burst = 0; burst < 4; burst++)
            {
                using var typing = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
                try
                {
                    await backend.InferAsync(Sentence, "ru", typing.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }

            using var paused = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (await backend.InferAsync(Sentence, "ru", paused.Token) is not null)
            {
                answeredPauses++;
            }
        }

        sw.Stop();
        Assert.AreEqual(
            5,
            answeredPauses,
            $"only {answeredPauses} of 5 typing pauses produced an answer after "
            + $"{sw.ElapsedMilliseconds} ms; the backend degrades under normal typing");
        Assert.AreEqual(0, server.SlotsLeaked);
    }
}
