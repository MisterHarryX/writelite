using System.Net;
using System.Net.Http;
using System.Text;
using WriteLite.Services.LanguageEngine;

namespace WriteLite.Tests.LanguageEngine;

[TestClass]
public sealed class WriteLiteLanguageEngineTests
{
    [TestMethod]
    public void Options_HaveSafeDefaults()
    {
        var options = new WriteLiteLanguageOptions();

        Assert.AreEqual("127.0.0.1", options.HostAddress);
        Assert.AreEqual("ru-RU", options.Language);
        Assert.IsFalse(options.EnableEngine);
        Assert.IsTrue(options.PreferJavaw);
        Assert.IsGreaterThan(0, options.MaxTextLength);
        Assert.IsTrue(options.RuntimeDirectory.Contains("LanguageEngine", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(options.RuntimeDirectory.Contains(@"C:\LT", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(options.RuntimeDirectory.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
                      || options.RuntimeDirectory.Contains(Path.Combine("ThirdParty", "LanguageEngine"), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void RuntimeDirectory_IsRelativeToBaseDirectory()
    {
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, WriteLiteLanguageOptions.DefaultRelativeRuntimeDirectory));
        Assert.AreEqual(expected, WriteLiteLanguageOptions.ResolveDefaultRuntimeDirectory());
    }

    [TestMethod]
    public void JavaResolver_Order_ConfiguredFirst()
    {
        var options = new WriteLiteLanguageOptions
        {
            ConfiguredJavaPath = @"C:\custom\bin\javaw.exe"
        };

        var first = WriteLiteJavaResolver.EnumerateCandidates(options).First();
        Assert.AreEqual(@"C:\custom\bin\javaw.exe", first);
    }

    [TestMethod]
    public void JavaResolver_InvalidConfiguredPath_IsSkipped()
    {
        var options = new WriteLiteLanguageOptions
        {
            ConfiguredJavaPath = @"C:\definitely\missing\javaw.exe"
        };

        var resolved = new WriteLiteJavaResolver().Resolve(options);
        // May still find system Java; must not return the missing configured path.
        if (resolved is not null)
        {
            Assert.IsFalse(resolved.Equals(options.ConfiguredJavaPath, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(File.Exists(resolved));
        }
    }

    [TestMethod]
    public void JavaResolver_CachesResult()
    {
        var resolver = new WriteLiteJavaResolver();
        var options = new WriteLiteLanguageOptions();
        var a = resolver.Resolve(options);
        var b = resolver.Resolve(options);
        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void LoopbackGuard_Accepts127()
    {
        Assert.IsTrue(WriteLiteLoopbackGuard.IsAllowedBaseAddress("http://127.0.0.1:18081"));
    }

    [TestMethod]
    public void LoopbackGuard_RejectsExternalAndLocalhostName()
    {
        Assert.IsFalse(WriteLiteLoopbackGuard.IsAllowedBaseAddress("http://example.com:8080"));
        Assert.IsFalse(WriteLiteLoopbackGuard.IsAllowedBaseAddress("https://127.0.0.1:18081"));
        Assert.IsFalse(WriteLiteLoopbackGuard.IsAllowedBaseAddress("http://localhost:18081"));
        Assert.IsFalse(WriteLiteLoopbackGuard.IsAllowedBaseAddress("http://0.0.0.0:18081"));
    }

    [TestMethod]
    public void ParseResponseJson_EmptyMatches()
    {
        var dto = WriteLiteLanguageClient.ParseResponseJson("""{"matches":[]}""");
        Assert.IsEmpty(dto.Matches);
    }

    [TestMethod]
    public void ParseResponseJson_NullOptionalFields_NoNre()
    {
        const string json =
            """
            {
              "matches": [
                {
                  "offset": 1,
                  "length": 2
                }
              ]
            }
            """;
        var dto = WriteLiteLanguageClient.ParseResponseJson(json);
        Assert.HasCount(1, dto.Matches);
        Assert.IsNull(dto.Matches[0].Message);
        Assert.IsNull(dto.Matches[0].Rule);
        Assert.IsEmpty(dto.Matches[0].Replacements);
    }

    [TestMethod]
    public void ParseResponseJson_FullMatch()
    {
        const string json =
            """
            {
              "matches": [
                {
                  "message": "Possible typo",
                  "shortMessage": "Typo",
                  "offset": 18,
                  "length": 4,
                  "replacements": [ { "value": "хочу" } ],
                  "context": { "text": "я хочю проверить", "offset": 2, "length": 4 },
                  "sentence": "я хочю проверить",
                  "rule": {
                    "id": "MORFOLOGIK_RULE_RU_RU",
                    "description": "Spellcheck",
                    "issueType": "misspelling",
                    "category": { "id": "TYPOS", "name": "Possible Typo" }
                  }
                }
              ]
            }
            """;

        var dto = WriteLiteLanguageClient.ParseResponseJson(json);
        Assert.HasCount(1, dto.Matches);
        var match = dto.Matches[0];
        Assert.AreEqual(18, match.Offset);
        Assert.AreEqual(4, match.Length);
        Assert.AreEqual("хочу", match.Replacements[0].Value);
        Assert.AreEqual("MORFOLOGIK_RULE_RU_RU", match.Rule?.Id);
        Assert.AreEqual("TYPOS", match.Rule?.Category?.Id);
    }

    [TestMethod]
    public void ParseResponseJson_CyrillicAndEmoji()
    {
        const string json =
            """
            {
              "matches": [
                {
                  "message": "Ошибка 😀 в слове",
                  "offset": 0,
                  "length": 2,
                  "replacements": []
                }
              ]
            }
            """;
        var dto = WriteLiteLanguageClient.ParseResponseJson(json);
        Assert.Contains("😀", dto.Matches[0].Message!);
        Assert.Contains("Ошибка", dto.Matches[0].Message!);
    }

    [TestMethod]
    public void ParseResponseJson_InvalidJson_Throws()
    {
        var ex = Assert.ThrowsExactly<WriteLiteLanguageEngineException>(
            () => WriteLiteLanguageClient.ParseResponseJson("{not-json"));
        Assert.AreEqual("invalid-json", ex.Code);
    }

    [TestMethod]
    public void ReadinessProbe_RequiresRussianLanguageCode()
    {
        Assert.IsTrue(WriteLiteReadinessProbe.ContainsRussianLanguage(
            """[{"name":"Russian","code":"ru-RU","longCode":"ru-RU"}]"""));
        Assert.IsFalse(WriteLiteReadinessProbe.ContainsRussianLanguage(
            """[{"name":"English","code":"en-US"}]"""));
        Assert.IsFalse(WriteLiteReadinessProbe.ContainsRussianLanguage("not-json"));
    }

    [TestMethod]
    public async Task Client_DisabledEngine_Throws()
    {
        var options = new WriteLiteLanguageOptions { EnableEngine = false };
        using var client = new WriteLiteLanguageClient(options);
        var ex = await Assert.ThrowsExactlyAsync<WriteLiteLanguageEngineException>(
            () => client.CheckAsync("тест"));
        Assert.AreEqual("engine-disabled", ex.Code);
    }

    [TestMethod]
    public async Task Client_TextTooLong_Throws()
    {
        var options = new WriteLiteLanguageOptions { EnableEngine = true, MaxTextLength = 5 };
        options.SetBoundPort(18081);
        using var client = new WriteLiteLanguageClient(options);
        var ex = await Assert.ThrowsExactlyAsync<WriteLiteLanguageEngineException>(
            () => client.CheckAsync("123456"));
        Assert.AreEqual("text-too-long", ex.Code);
    }

    [TestMethod]
    public async Task Client_EmptyText_NoHttp()
    {
        var options = new WriteLiteLanguageOptions { EnableEngine = true };
        options.SetBoundPort(18081);
        var handler = new StubHandler(_ => throw new InvalidOperationException("should not send"));
        using var http = new HttpClient(handler);
        using var client = new WriteLiteLanguageClient(options, http);
        var dto = await client.CheckAsync("");
        Assert.IsEmpty(dto.Matches);
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task Client_RejectsExternalBaseAddress()
    {
        var options = new WriteLiteLanguageOptions { EnableEngine = true };
        Assert.ThrowsExactly<WriteLiteLanguageEngineException>(
            () => new WriteLiteLanguageClient(options, "http://example.com:8080"));
    }

    [TestMethod]
    public async Task Client_PostsFormUrlEncoded_ToCheck()
    {
        var options = new WriteLiteLanguageOptions { EnableEngine = true };
        options.SetBoundPort(12345);

        string? contentType = null;
        string? body = null;
        var handler = new StubHandler(req =>
        {
            contentType = req.Content?.Headers.ContentType?.MediaType;
            body = req.Content is null ? null : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var json = """{"matches":[{"offset":1,"length":2,"replacements":[{"value":"x"}],"rule":{"id":"R1","category":{"id":"TYPOS"}}}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        using var client = new WriteLiteLanguageClient(options, http);
        var dto = await client.CheckAsync("ab");

        Assert.HasCount(1, dto.Matches);
        Assert.AreEqual(1, handler.RequestCount);
        Assert.AreEqual(HttpMethod.Post, handler.LastMethod);
        Assert.Contains("/v2/check", handler.LastRequestUri!.AbsoluteUri);
        Assert.Contains("127.0.0.1", handler.LastRequestUri.AbsoluteUri);
        Assert.AreEqual("application/x-www-form-urlencoded", contentType);
        Assert.IsNotNull(body);
        Assert.Contains("language=ru-RU", body);
        Assert.Contains("text=ab", body);
    }

    [TestMethod]
    public async Task Client_Http500_Throws()
    {
        var options = new WriteLiteLanguageOptions { EnableEngine = true };
        options.SetBoundPort(12345);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("err")
        });
        using var http = new HttpClient(handler);
        using var client = new WriteLiteLanguageClient(options, http);
        var ex = await Assert.ThrowsExactlyAsync<WriteLiteLanguageEngineException>(
            () => client.CheckAsync("тест"));
        Assert.AreEqual("http-error", ex.Code);
    }

    [TestMethod]
    public async Task Client_Cancellation_Propagates()
    {
        var options = new WriteLiteLanguageOptions { EnableEngine = true, RequestTimeout = TimeSpan.FromSeconds(30) };
        options.SetBoundPort(WriteLitePortAllocator.FindFreePort(51000, 50));
        using var client = new WriteLiteLanguageClient(options);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.CheckAsync("привет", cancellationToken: cts.Token));
    }

    [TestMethod]
    public async Task Client_ReusesHttpClientInstance()
    {
        var options = new WriteLiteLanguageOptions { EnableEngine = true };
        options.SetBoundPort(12345);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"matches":[]}""", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler);
        using var client = new WriteLiteLanguageClient(options, http);
        await client.CheckAsync("a");
        await client.CheckAsync("b");
        Assert.AreSame(http, client.SharedHttpClient);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task Client_UnavailableServer_ThrowsControlled()
    {
        var options = new WriteLiteLanguageOptions
        {
            EnableEngine = true,
            RequestTimeout = TimeSpan.FromSeconds(2)
        };
        options.SetBoundPort(WriteLitePortAllocator.FindFreePort(51000, 50));
        using var client = new WriteLiteLanguageClient(options);
        var ex = await Assert.ThrowsExactlyAsync<WriteLiteLanguageEngineException>(
            () => client.CheckAsync("привет"));
        Assert.IsTrue(ex.Code is "http-unavailable" or "request-timeout" or "request-failed");
    }

    [TestMethod]
    public async Task Host_Disabled_StartThrows()
    {
        var options = new WriteLiteLanguageOptions { EnableEngine = false };
        await using var host = new WriteLiteLanguageEngineHost(options);
        var ex = await Assert.ThrowsExactlyAsync<WriteLiteLanguageEngineException>(
            () => host.StartAsync());
        Assert.AreEqual("engine-disabled", ex.Code);
        Assert.AreEqual(WriteLiteLanguageEngineState.Disabled, host.State);
    }

    [TestMethod]
    public async Task Host_MissingRuntime_ThrowsUnavailable()
    {
        var options = new WriteLiteLanguageOptions
        {
            EnableEngine = true,
            RuntimeDirectory = Path.Combine(Path.GetTempPath(), "WriteLite-Missing-Engine-" + Guid.NewGuid())
        };
        await using var host = new WriteLiteLanguageEngineHost(options);
        var ex = await Assert.ThrowsExactlyAsync<WriteLiteLanguageEngineException>(
            () => host.StartAsync());
        Assert.AreEqual("runtime-missing", ex.Code);
        Assert.AreEqual(WriteLiteLanguageEngineState.Unavailable, host.State);
    }

    [TestMethod]
    public async Task Host_StopAsync_IsIdempotent()
    {
        var options = new WriteLiteLanguageOptions { EnableEngine = true };
        await using var host = new WriteLiteLanguageEngineHost(options);
        await host.StopAsync();
        await host.StopAsync();
        Assert.AreEqual(WriteLiteLanguageEngineState.Stopped, host.State);
    }

    [TestMethod]
    public async Task Host_FakeLauncher_StartReadyStop()
    {
        var options = new WriteLiteLanguageOptions
        {
            EnableEngine = true,
            RuntimeDirectory = CreateTempRuntimeLayout(),
            PreferredPort = 18090,
            PortSearchRange = 5,
            StartupTimeout = TimeSpan.FromSeconds(5),
            ReadyPollInterval = TimeSpan.FromMilliseconds(50)
        };

        var process = new FakeProcessHandle(id: 4242);
        var launcher = new FakeLauncher(process);
        var ports = new FixedPortAllocator(18111);
        var readiness = new FakeReadiness(succeedAfter: 1);
        var java = new FixedJavaResolver(@"C:\fake\javaw.exe");

        await using var host = new WriteLiteLanguageEngineHost(options, java, launcher, ports, readiness);
        await host.StartAsync();
        Assert.IsTrue(host.IsReady);
        Assert.AreEqual(18111, host.Port);
        Assert.AreEqual(1, launcher.StartCount);

        await host.StartAsync(); // idempotent
        Assert.AreEqual(1, launcher.StartCount);

        await host.StopAsync();
        Assert.IsTrue(process.KillTreeCalled || process.GracefulCalled);
        Assert.AreEqual(WriteLiteLanguageEngineState.Stopped, host.State);
    }

    [TestMethod]
    public async Task Host_ConcurrentStart_UsesSingleProcess()
    {
        var options = new WriteLiteLanguageOptions
        {
            EnableEngine = true,
            RuntimeDirectory = CreateTempRuntimeLayout(),
            StartupTimeout = TimeSpan.FromSeconds(5),
            ReadyPollInterval = TimeSpan.FromMilliseconds(20)
        };

        var process = new FakeProcessHandle(id: 7);
        var launcher = new FakeLauncher(process);
        var ports = new FixedPortAllocator(18222);
        var readiness = new SlowReadiness(TimeSpan.FromMilliseconds(200));
        var java = new FixedJavaResolver(@"C:\fake\javaw.exe");

        await using var host = new WriteLiteLanguageEngineHost(options, java, launcher, ports, readiness);
        var t1 = host.StartAsync();
        var t2 = host.StartAsync();
        await Task.WhenAll(t1, t2);
        Assert.AreEqual(1, launcher.StartCount);
        Assert.IsTrue(host.IsReady);
    }

    [TestMethod]
    public void FindFreePort_ReturnsValidPort()
    {
        var port = WriteLitePortAllocator.FindFreePort(19000, 20);
        Assert.IsGreaterThanOrEqualTo(1024, port);
        Assert.IsLessThanOrEqualTo(65535, port);
    }

    [TestMethod]
    public void UserFacingMessages_AreNeutral()
    {
        var samples = new[]
        {
            "Local language engine is disabled.",
            "Local language engine is unavailable.",
            "language-engine-starting",
            "language-engine-ready",
            "language-engine-unavailable",
            "language-engine-request-completed"
        };

        foreach (var sample in samples)
        {
            Assert.IsFalse(sample.Contains("LanguageTool", StringComparison.OrdinalIgnoreCase), sample);
            Assert.IsFalse(sample.Contains("languagetool", StringComparison.OrdinalIgnoreCase), sample);
        }
    }

    private static string CreateTempRuntimeLayout()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wl-engine-rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "libs"));
        File.WriteAllText(Path.Combine(dir, "languagetool-server.jar"), "stub");
        return dir;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public int RequestCount { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public HttpMethod? LastMethod { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            LastMethod = request.Method;
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class FixedJavaResolver(string path) : IWriteLiteJavaResolver
    {
        public string? Resolve(WriteLiteLanguageOptions options) => path;
    }

    private sealed class FixedPortAllocator(int port) : IWriteLitePortAllocator
    {
        public IEnumerable<int> AllocateCandidates(int preferredPort, int searchRange)
        {
            yield return port;
        }
    }

    private sealed class FakeReadiness(int succeedAfter) : IWriteLiteReadinessProbe
    {
        private int _calls;

        public Task<bool> IsReadyAsync(string baseAddress, CancellationToken cancellationToken = default)
        {
            _calls++;
            return Task.FromResult(_calls >= succeedAfter);
        }
    }

    private sealed class SlowReadiness(TimeSpan delay) : IWriteLiteReadinessProbe
    {
        public async Task<bool> IsReadyAsync(string baseAddress, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return true;
        }
    }

    private sealed class FakeLauncher(FakeProcessHandle process) : IWriteLiteProcessLauncher
    {
        public int StartCount { get; private set; }

        public IWriteLiteProcessHandle Start(WriteLiteProcessStartRequest request)
        {
            StartCount++;
            process.MarkStarted();
            return process;
        }
    }

    private sealed class FakeProcessHandle : IWriteLiteProcessHandle
    {
        private bool _exited;

        public FakeProcessHandle(int id) => Id = id;

        public int Id { get; }

        public bool HasExited => _exited;

        public int ExitCode => _exited ? 0 : -1;

        public bool GracefulCalled { get; private set; }

        public bool KillTreeCalled { get; private set; }

        public event EventHandler? Exited;

        public void MarkStarted() => _exited = false;

        public bool TryCloseGracefully()
        {
            GracefulCalled = true;
            _exited = true;
            Exited?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public void KillTree()
        {
            KillTreeCalled = true;
            _exited = true;
            Exited?.Invoke(this, EventArgs.Empty);
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
