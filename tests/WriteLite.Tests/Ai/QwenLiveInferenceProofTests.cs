using WriteLite.AI.Local;
using WriteLite.Services.Ai;

namespace WriteLite.Tests.Ai;

/// <summary>
/// Proves C# → Qwen loopback → C# when server is up. Skips if health is down.
/// </summary>
// Writes to the process-wide LocalAiDiagnostics.Log sink, so it must not run beside the
// tests in LocalAiAnalyzerTests that assert on that sink's contents.
[TestClass]
[DoNotParallelize]
public sealed class QwenLiveInferenceProofTests
{
    private static bool ServerUp()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var r = client.GetAsync("http://127.0.0.1:8742/health").GetAwaiter().GetResult();
            return r.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    [TestMethod]
    public async Task CSharp_Provider_Calls_Qwen_And_Corrects_Russian()
    {
        if (!ServerUp())
        {
            Assert.Inconclusive("Qwen server not on http://127.0.0.1:8742/health");
            return;
        }

        var logs = new List<string>();
        LocalAiDiagnostics.Log = (e, d) => logs.Add(e + " " + d);

        var modelDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models", "writelight-qwen"));
        if (!Directory.Exists(modelDir))
        {
            modelDir = Path.Combine(AppContext.BaseDirectory, "models", "writelight-qwen");
        }

        await using var provider = new LocalAiTextProvider(new LocalAiOptions
        {
            Enabled = true,
            Profile = WriteLite.AI.Contracts.AiModelProfile.Standard,
            ModelDirectory = modelDir,
            QwenEndpoint = "http://127.0.0.1:8742",
            PreferQwen = true,
            TimeoutSeconds = 120,
            MinInputChars = 1,
            MaxInputChars = 12_000
        });

        await provider.WarmupAsync();
        Assert.IsTrue(provider.QwenAvailable, "Qwen should be available after health probe");

        const string input = "Я сегодня небыл дома";
        var response = await provider.AnalyzeAsync(input);

        Assert.IsTrue(
            logs.Any(l => l.Contains("qwen-request-started", StringComparison.Ordinal)),
            "Expected qwen-request-started in diagnostics. Logs:\n" + string.Join("\n", logs));
        Assert.IsTrue(
            logs.Any(l => l.Contains("qwen-response-received", StringComparison.Ordinal)
                          || l.Contains("qwen-response-rejected", StringComparison.Ordinal)),
            "Expected qwen-response-received|rejected. Logs:\n" + string.Join("\n", logs));

        Assert.IsFalse(string.IsNullOrWhiteSpace(response.CorrectedText));
        // Prefer neural backend when server is healthy.
        Assert.AreEqual(
            "writelight-qwen",
            provider.LastBackend,
            "LastBackend should be writelight-qwen. Logs:\n" + string.Join("\n", logs));

        var corrected = response.CorrectedText!;
        // Smoke model + Lite polish: expect the high-precision Russian fix.
        Assert.IsTrue(
            corrected.Contains("не был", StringComparison.OrdinalIgnoreCase),
            "Expected Russian grammar polish from Qwen and/or Lite. Got: " + corrected
            + "\nLogs:\n" + string.Join("\n", logs));

        // Must differ from raw input when server is healthy (proves inference applied).
        Assert.AreNotEqual(input, corrected, "Corrected text should change input when Qwen runs.");
    }
}
