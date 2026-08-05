using System.Diagnostics;
using WriteLite.Services.LanguageEngine;

namespace WriteLite.Tests.LanguageEngine;

[TestClass]
public sealed class WriteLiteJobObjectTests
{
    [TestMethod]
    public void WindowsJobObject_IsAvailableOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows only");
            return;
        }

        using var job = new WindowsJobObject();
        Assert.IsTrue(job.IsAvailable);
    }

    [TestMethod]
    public void JobObject_KillOnClose_TerminatesAssignedChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows only");
            return;
        }

        using var job = new WindowsJobObject();
        Assert.IsTrue(job.IsAvailable);

        // Long-running child that we only own for this test.
        var psi = new ProcessStartInfo
        {
            FileName = "ping.exe",
            Arguments = "-t 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var child = Process.Start(psi);
        Assert.IsNotNull(child);
        try
        {
            Assert.IsTrue(job.TryAssign(child));
            Assert.IsFalse(child.HasExited);

            // Closing the job handle with KILL_ON_JOB_CLOSE must end the child.
            job.Dispose();

            var exited = child.WaitForExit(8000);
            Assert.IsTrue(exited, "Child process should exit when job is closed.");
        }
        finally
        {
            try
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // best effort
            }
        }
    }

    [TestMethod]
    public void JobObject_Dispose_IsIdempotent()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows only");
            return;
        }

        var job = new WindowsJobObject();
        job.Dispose();
        job.Dispose();
        Assert.IsFalse(job.IsAvailable);
    }
}
