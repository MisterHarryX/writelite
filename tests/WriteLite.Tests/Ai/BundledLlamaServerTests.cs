using System.IO;
using System.Net.Http;
using WriteLite.AI.Local;
using WriteLite.Services.Ai;

namespace WriteLite.Tests.Ai;

/// <summary>
/// The llama.cpp server shipped in <c>runtimes\</c>: started by the backend on demand, and
/// still answering after many requests.
/// </summary>
/// <remarks>
/// The second half is the regression. The backend redirected the server's output and never
/// read it; llama-server logs several lines per request, so after a few dozen requests the
/// pipe was full and the server stopped mid-request while <c>/health</c> still answered.
/// A hundred short requests is well past where that used to happen.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class BundledLlamaServerTests
{
    private static bool DefaultPortAnswers()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            return client.GetAsync(QwenModelBackend.DefaultEndpoint + "/health").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    [TestMethod]
    public void The_server_and_the_libraries_it_loads_are_copied_beside_the_app()
    {
        var runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
        if (!File.Exists(Path.Combine(runtimes, "llama-server.exe")))
        {
            Assert.Inconclusive("artifacts\\llama-bin is not present on this machine, so no runtime was bundled.");
        }

        foreach (var file in new[] { "llama-server-impl.dll", "llama.dll", "llama-common.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu-x64.dll" })
        {
            Assert.IsTrue(File.Exists(Path.Combine(runtimes, file)), $"{file} must ship with the server or it cannot start.");
        }

        Assert.IsFalse(File.Exists(Path.Combine(runtimes, "llama-cli.exe")), "Only the server ships, not the other llama.cpp tools.");
    }

    [TestMethod]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task The_backend_starts_the_bundled_server_and_it_keeps_answering()
    {
        var pack = Path.Combine(AppContext.BaseDirectory, "models", "writelight-qwen");
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", "llama-server.exe"))
            || !Directory.Exists(pack) || Directory.GetFiles(pack, "*.gguf").Length == 0)
        {
            Assert.Inconclusive("No bundled server or model pack on this machine.");
        }

        if (DefaultPortAnswers())
        {
            Assert.Inconclusive("Another server already answers on the default port; the bundled one would not be started.");
        }

        await using var backend = new QwenModelBackend(pack);
        var model = new QwenTextModel(backend);

        Assert.AreEqual(LocalModelReadiness.Ready, await model.EnsureReadyAsync(CancellationToken.None),
            "EnsureReady must start the bundled server. Last log lines:\n" + string.Join("\n", backend.ServerLogTail));

        for (var request = 0; request < 100; request++)
        {
            var answer = await backend.CompleteAsync(
                [("system", "Отвечай одним словом."), ("user", $"Скажи «да». Запрос {request}.")],
                maxTokens: 16,
                temperature: 0,
                CancellationToken.None);

            Assert.IsNotNull(answer, $"The server stopped answering at request {request}. Last log lines:\n"
                                     + string.Join("\n", backend.ServerLogTail));
        }

        Assert.IsTrue(backend.ServerLogTail.Count > 0, "The server's output is read, not left to fill the pipe.");
    }

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Every model-backed AI action, end to end on the shipped model: each must come back as a
    /// proposal the preview can show — a changed text, or the unchanged text with a notice
    /// saying why — and never as an exception the user would read as "broken".
    /// </summary>
    [TestMethod]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task Every_ai_action_produces_a_reviewable_proposal_on_the_shipped_model()
    {
        var pack = Path.Combine(AppContext.BaseDirectory, "models", "writelight-qwen");
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", "llama-server.exe"))
            || !Directory.Exists(pack) || Directory.GetFiles(pack, "*.gguf").Length == 0 || DefaultPortAnswers())
        {
            Assert.Inconclusive("No bundled server or model pack, or the default port is taken.");
        }

        await using var backend = new QwenModelBackend(pack);
        var service = new TextActionService(new QwenTextModel(backend));
        string[] note =
        [
            "Планы на запуск",
            "Вчера мы с командой обсуждали запуск нового сервиса доставки. Главная проблема в том, что курьеров не хватает в вечерние часы, а заказов в это время больше всего.",
            "Предложили ввести повышенную оплату за вечерние смены и нанять ещё десять человек к концу месяца. Также решили протестировать доставку дронами в одном районе."
        ];

        foreach (var operation in new[] { RewriteOperation.KeyPoints, RewriteOperation.Explain, RewriteOperation.Shorten, RewriteOperation.Expand, RewriteOperation.Rewrite })
        {
            var started = DateTime.UtcNow;
            var result = await service.RunAsync(new TextActionRequest(operation, note));

            TestContext.WriteLine($"== {operation} ({(DateTime.UtcNow - started).TotalSeconds:F1} s) notice: {result.Notice}");
            TestContext.WriteLine(result.Suggestion);

            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Suggestion), operation.ToString());
            Assert.IsTrue(!result.IsNoOp || result.Notice is not null, $"{operation}: an unchanged answer must say why.");
            Assert.AreEqual("Планы на запуск", operation is RewriteOperation.KeyPoints or RewriteOperation.Explain ? note[0] : result.Suggestions[0],
                "Headings are not rewritten.");
        }
    }
}
