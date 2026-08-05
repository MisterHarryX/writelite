using WriteLite.AI.Contracts;
using WriteLite.AI.Local;

namespace WriteLite.Tests.Ai;

[TestClass]
public sealed class QwenLiveIntegrationTests
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
    public async Task LiveServer_IfRunning_ReturnsValidatedCorrection()
    {
        if (!ServerUp())
        {
            Assert.Inconclusive("WriteLite-Qwen local server not running on 127.0.0.1:8742");
            return;
        }

        var pack = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models", "writelight-qwen"));
        if (!Directory.Exists(pack))
        {
            pack = Path.Combine(AppContext.BaseDirectory, "models", "writelight-qwen");
        }

        await using var analyzer = new LocalAiTextAnalyzer(modelDirectory: pack);
        analyzer.Qwen.RefreshAvailability();
        Assert.IsTrue(analyzer.Qwen.IsAvailable, "Qwen pack should be available with endpoint");

        var text = "привет как у тебя дела я сегодня небыл в школе";
        var result = await analyzer.AnalyzeAsync(new AiTextAnalysisRequest(
            text, "ru", AiAnalysisMode.Correct, AiSchema.CurrentVersion, AiModelProfile.Standard));

        Assert.AreEqual(AiErrorCodes.Ok, result.ErrorCode ?? AiErrorCodes.Ok);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.CorrectedText));
        // Either neural or lite fallback must preserve safety and produce something usable.
        Assert.IsTrue(
            result.Backend is "writelight-qwen" or "lite" or "lite-fallback",
            result.Backend);
        // Smoke LoRA is short — quality is not guaranteed. Require only safety invariants.
        static bool IsCyr(char ch) =>
            ch is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё';
        Assert.IsTrue(
            result.CorrectedText.Any(IsCyr) || result.Backend != "writelight-qwen",
            "If neural path ran, Cyrillic input should not be fully rewritten to unrelated English prose. Got: "
            + result.CorrectedText);
        // Re-validate protected-token style invariants on the output path.
        Assert.IsTrue(new AiResultValidator().IsRewriteSane(text, result.CorrectedText)
                      || result.CorrectedText == text);
    }
}
