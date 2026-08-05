using System.Diagnostics;

namespace WriteLite.Tests.Uia;

[TestClass]
[DoNotParallelize]
public sealed class ApplicationLifecycleIntegrationTests
{
    [TestMethod]
    [TestCategory("UIA")]
    [TestCategory("Stress")]
    [TestProperty("Category", "UIA")]
    [TestProperty("Category", "Stress")]
    public void TrayShutdownPath_StopsApplicationWithExitCodeZero()
    {
        var preExistingLlama = GetProcessIds("llama-server");
        var executable = FindRepositoryRoot(
            "src", "WriteLite.App", "bin", "Release", "net10.0-windows", "WriteLite.exe");
        Assert.IsTrue(File.Exists(executable), $"WriteLite executable was not built: {executable}");

        using var process = Process.Start(new ProcessStartInfo(executable, "--lifecycle-smoke")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        });
        Assert.IsNotNull(process);

        try
        {
            Assert.IsTrue(process.WaitForExit(30_000), "WriteLite did not complete its orderly shutdown path.");
            Assert.AreEqual(0, process.ExitCode);
            Thread.Sleep(1_000);
            Assert.IsFalse(Process.GetProcesses().Any(candidate =>
            {
                try
                {
                    return candidate.Id == process.Id && !candidate.HasExited;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    candidate.Dispose();
                }
            }), "The tested WriteLite process remained alive after shutdown.");
            var leakedLlama = GetProcessIds("llama-server").Except(preExistingLlama).ToArray();
            Assert.IsEmpty(leakedLlama, "WriteLite left a llama-server process after shutdown.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3_000);
            }
        }
    }

    private static HashSet<int> GetProcessIds(string processName)
    {
        var result = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        result.Add(process.Id);
                    }
                }
                catch
                {
                    // A process that exits while enumerating is not a leak.
                }
            }
        }

        return result;
    }

    private static string FindRepositoryRoot(params string[] relativeParts)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "WriteLite.sln")))
                {
                    return Path.Combine(new[] { current.FullName }.Concat(relativeParts).ToArray());
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("WriteLite repository root could not be located.");
    }
}
