using System.Net.NetworkInformation;
using WriteLite.Services.LanguageEngine;

namespace WriteLite.Tests.LanguageEngine;

/// <summary>
/// Optional live integration against the bundled runtime. Disabled unless:
/// WRITELITE_RUN_LANGUAGE_ENGINE_INTEGRATION_TESTS=1
/// </summary>
[TestClass]
public sealed class WriteLiteLanguageEngineIntegrationTests
{
    private static bool IntegrationEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("WRITELITE_RUN_LANGUAGE_ENGINE_INTEGRATION_TESTS"),
            "1",
            StringComparison.Ordinal);

    [TestMethod]
    public async Task LiveHost_Check_Stop_FreesProcessAndPort()
    {
        if (!IntegrationEnabled)
        {
            Assert.Inconclusive(
                "Set WRITELITE_RUN_LANGUAGE_ENGINE_INTEGRATION_TESTS=1 to run the live language-engine integration test.");
            return;
        }

        var runtime = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            WriteLiteLanguageOptions.DefaultRelativeRuntimeDirectory));

        // Tests project output may not copy ThirdParty; fall back to app output if needed.
        if (!Directory.Exists(runtime) || !File.Exists(Path.Combine(runtime, "languagetool-server.jar")))
        {
            var alt = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "src", "WriteLite.App", "bin", "Debug", "net10.0-windows",
                WriteLiteLanguageOptions.DefaultRelativeRuntimeDirectory));
            if (Directory.Exists(alt))
            {
                runtime = alt;
            }
        }

        Assert.IsTrue(
            File.Exists(Path.Combine(runtime, "languagetool-server.jar")),
            $"Runtime not found under {runtime}");

        var java = new WriteLiteJavaResolver().Resolve(new WriteLiteLanguageOptions { PreferJavaw = true });
        Assert.IsNotNull(java, "Java executable not found for integration test.");
        // Developer diagnostics only (not user UI):
        Console.WriteLine($"integration-java={java}");
        Console.WriteLine($"integration-runtime={runtime}");

        var options = new WriteLiteLanguageOptions
        {
            EnableEngine = true,
            RuntimeDirectory = runtime,
            PreferredPort = 18333,
            PortSearchRange = 30,
            StartupTimeout = TimeSpan.FromSeconds(60),
            RequestTimeout = TimeSpan.FromSeconds(60),
            PreferJavaw = true
        };

        await using var host = new WriteLiteLanguageEngineHost(options);
        var swStart = System.Diagnostics.Stopwatch.StartNew();
        await host.StartAsync();
        swStart.Stop();
        Assert.IsTrue(host.IsReady);
        Assert.IsNotNull(host.Port);
        Assert.IsNotNull(host.ProcessId);
        var pid = host.ProcessId!.Value;
        var port = host.Port!.Value;
        Console.WriteLine($"integration-start-ms={swStart.ElapsedMilliseconds} pid={pid} port={port}");

        // Second StartAsync must be idempotent.
        await host.StartAsync();
        Assert.AreEqual(pid, host.ProcessId);

        using var client = new WriteLiteLanguageClient(options, host.BaseAddress!);
        const string sample = "Привет как дела я хочю проверить этот текс.";

        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        var first = await client.CheckAsync(sample);
        sw1.Stop();
        Assert.IsGreaterThan(0, first.Matches.Count);
        Console.WriteLine($"integration-first-check-ms={sw1.ElapsedMilliseconds} matches={first.Matches.Count}");

        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var second = await client.CheckAsync(sample);
        sw2.Stop();
        Assert.IsGreaterThan(0, second.Matches.Count);
        Console.WriteLine($"integration-second-check-ms={sw2.ElapsedMilliseconds} matches={second.Matches.Count}");

        // Stage 5: map real DTO → TextIssue
        var mapper = new WriteLiteIssueMapper();
        var mapResult = mapper.MapAll(sample, first);
        Assert.IsGreaterThan(0, mapResult.Issues.Count);
        foreach (var issue in mapResult.Issues)
        {
            Assert.IsGreaterThanOrEqualTo(0, issue.Start);
            Assert.IsGreaterThan(0, issue.Length);
            Assert.IsLessThanOrEqualTo(sample.Length, issue.Start + issue.Length);
            Assert.AreEqual(sample.Substring(issue.Start, issue.Length), issue.Original);
            Assert.IsFalse(WriteLiteIssueNormalization.ContainsThirdPartyBrand(issue.Title));
            Assert.IsFalse(WriteLiteIssueNormalization.ContainsThirdPartyBrand(issue.Explanation));
            Assert.StartsWith("WL-", issue.RuleId);
            Assert.IsFalse(issue.RuleId.Contains("хочю", StringComparison.OrdinalIgnoreCase));
        }

        await host.StopAsync();
        await host.StopAsync(); // idempotent

        // Process must be gone.
        Assert.IsFalse(IsProcessAlive(pid), $"Process {pid} still alive after StopAsync.");
        Assert.IsFalse(IsPortListening(port), $"Port {port} still listening after StopAsync.");
        Assert.IsFalse(host.IsReady);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPortListening(int port)
    {
        var props = IPGlobalProperties.GetIPGlobalProperties();
        return props.GetActiveTcpListeners().Any(ep => ep.Port == port);
    }
}
