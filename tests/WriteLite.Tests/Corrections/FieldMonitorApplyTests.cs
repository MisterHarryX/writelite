using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Threading;
using WriteLite.Models;
using WriteLite.Services;

namespace WriteLite.Tests.Corrections;

/// <summary>
/// The external-field apply path, against a real out-of-process control.
/// </summary>
/// <remarks>
/// <para>§38 asks for a prepared external-field correction to apply without invoking the
/// language model, and §18 asks for the UI Automation operations to be profiled separately
/// from the language work. Both need a real provider on the other side of a process boundary,
/// which is what <c>WriteLite.TestHost</c> exists for — an in-process fake would measure the
/// wrong thing entirely, since the cost being investigated is the cross-process round trip.</para>
///
/// <para>The snapshot validator is injected rather than satisfied by driving focus: the
/// staleness check is already pinned by <c>CorrectionApplicationServiceTests</c>, and what this
/// test is about is what happens <i>after</i> that check passes.</para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class FieldMonitorApplyTests
{
    [TestMethod]
    [TestCategory("UIA")]
    [TestProperty("Category", "UIA")]
    public void Applying_a_prepared_field_correction_writes_without_consulting_any_analyzer()
    {
        using var host = StartTestHost();
        try
        {
            var window = WaitForMainWindow(host);
            var element = WaitForElement(
                window, new PropertyCondition(AutomationElement.AutomationIdProperty, "WpfInput"));

            const string original = "Согласно нового плана мы начали работу.";
            const string expected = "Согласно новому плана мы начали работу.";

            ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(original);

            var fast = new CountingAnalyzer();
            var deep = new CountingAnalyzer();
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var monitor = new TextFieldMonitor(dispatcher, fast, deep);

            var target = new AutomationTextTarget(element, new ValuePatternTextAdapter());
            var issue = new TextIssue(
                9, 6, "нового", "новому", "government", "dative required",
                IssueCategory.Grammar, IssueSeverity.Error, true, "ru.case.government.soglasno",
                Confidence: 0.97);

            var snapshot = new TextSnapshot(target, original, [issue], DateTimeOffset.Now);

            // The staleness check has its own tests; this one is about the path after it.
            var service = new CorrectionApplicationService(
                snapshotValidator: (_, _) => CorrectionApplicationStatus.CorrectionApplied);

            var latencies = new List<double>();
            CorrectionApplicationOutcome outcome = null!;

            for (var attempt = 0; attempt < 5; attempt++)
            {
                ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(original);

                var stopwatch = Stopwatch.StartNew();
                outcome = service
                    .ApplyAsync(issue, snapshot, monitor, new AsyncOperationGate())
                    .GetAwaiter()
                    .GetResult();
                stopwatch.Stop();

                Assert.IsTrue(
                    outcome.Succeeded,
                    $"apply reported {outcome.Status}: {outcome.UserMessage}");
                latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
            }

            Assert.AreEqual(
                expected,
                ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);

            Assert.AreEqual(0, fast.Calls, "field apply must not run the deterministic analyzer");
            Assert.AreEqual(0, deep.Calls, "field apply must not run the deep analyzer or the model");

            latencies.Sort();
            var p50 = latencies[latencies.Count / 2];
            var p95 = latencies[Math.Min(latencies.Count - 1, (int)(latencies.Count * 0.95))];
            Console.WriteLine(
                $"field apply: n={latencies.Count} p50={p50:F1} ms p95={p95:F1} ms max={latencies[^1]:F1} ms");

            Assert.IsLessThan(
                300,
                p50,
                $"p50 field apply latency was {p50:F1} ms");
        }
        finally
        {
            StopHost(host);
        }
    }

    // ── Host plumbing, mirroring TestHostUiaIntegrationTests ─────────────────

    private static Process StartTestHost()
    {
        var executable = Path.Combine(
            FindRepositoryRoot(), "src", "WriteLite.TestHost", "bin", "Release", "net10.0-windows",
            "WriteLite.TestHost.exe");
        if (!File.Exists(executable))
        {
            executable = Path.Combine(
                FindRepositoryRoot(), "src", "WriteLite.TestHost", "bin", "Debug", "net10.0-windows",
                "WriteLite.TestHost.exe");
        }

        Assert.IsTrue(File.Exists(executable), $"Test host executable was not built: {executable}");

        var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        });
        Assert.IsNotNull(process);
        return process;
    }

    private static void StopHost(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WriteLite.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Could not locate the repository root.");
        return directory.FullName;
    }

    private static AutomationElement WaitForMainWindow(Process process)
    {
        process.WaitForInputIdle(5_000);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                try
                {
                    var window = AutomationElement.FromHandle(process.MainWindowHandle);
                    if (string.Equals(window.Current.Name, "WriteLite UIA Test Host", StringComparison.Ordinal))
                    {
                        return window;
                    }
                }
                catch (COMException)
                {
                    // Provider not registered yet.
                }
                catch (ElementNotAvailableException)
                {
                    // Transient during startup.
                }
            }

            Thread.Sleep(50);
        }

        Assert.Fail("Test host did not expose a main window handle.");
        throw new InvalidOperationException();
    }

    private static AutomationElement WaitForElement(AutomationElement root, Condition condition)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                var element = root.FindFirst(TreeScope.Descendants, condition);
                if (element is not null) return element;
            }
            catch (COMException)
            {
                // Cross-process providers can be briefly unavailable.
            }
            catch (ElementNotAvailableException)
            {
                // Same.
            }

            Thread.Sleep(50);
        }

        Assert.Fail("Test host did not expose the requested element.");
        throw new InvalidOperationException();
    }
}
