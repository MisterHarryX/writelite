using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WriteLite.Tests.Uia;

[TestClass]
[DoNotParallelize]
public sealed class TestHostUiaIntegrationTests
{
    [TestMethod]
    [TestCategory("UIA")]
    [TestProperty("Category", "UIA")]
    public void TestHost_ExposesEditableReadOnlyAndPasswordControls_AndExitsCleanly()
    {
        using var host = StartTestHost();
        try
        {
            var window = WaitForMainWindow(host);
            Assert.AreEqual("WriteLite UIA Test Host", window.Current.Name);

            var editable = FindByAutomationId(window, "WpfInput");
            var editablePattern = (ValuePattern)editable.GetCurrentPattern(ValuePattern.Pattern);
            Assert.IsFalse(editablePattern.Current.IsReadOnly);
            editablePattern.SetValue("Привет как тваи дела");
            Assert.AreEqual("Привет как тваи дела", editablePattern.Current.Value);

            var readOnly = FindByAutomationId(window, "ReadOnlyInput");
            var readOnlyPattern = (ValuePattern)readOnly.GetCurrentPattern(ValuePattern.Pattern);
            Assert.IsTrue(readOnlyPattern.Current.IsReadOnly);

            var password = FindByAutomationId(window, "PasswordInput");
            Assert.IsTrue(password.Current.IsPassword);

            Invoke(FindByAutomationId(window, "CloseHostButton"));
            Assert.IsTrue(host.WaitForExit(5_000), "Test host did not stop after its close command.");
            Assert.AreEqual(0, host.ExitCode);
        }
        finally
        {
            StopHost(host);
        }
    }

    [TestMethod]
    [TestCategory("UIA")]
    [TestProperty("Category", "UIA")]
    public void TestHost_DynamicTextChange_IsVisibleAcrossProcessBoundary()
    {
        using var host = StartTestHost();
        try
        {
            var window = WaitForMainWindow(host);
            var editable = FindByAutomationId(window, "WpfInput");
            var before = ((ValuePattern)editable.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

            Invoke(FindByAutomationId(window, "ChangeTextButton"));
            var after = WaitForValueChange(editable, before);

            Assert.AreNotEqual(before, after);
            Assert.IsTrue(after.Contains("нужю", StringComparison.Ordinal));
        }
        finally
        {
            StopHost(host);
        }
    }

    private static Process StartTestHost()
    {
        var executable = FindRepositoryRoot()
            .Combine("src", "WriteLite.TestHost", "bin", "Release", "net10.0-windows", "WriteLite.TestHost.exe");
        Assert.IsTrue(File.Exists(executable), $"Test host executable was not built: {executable}");

        var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        });
        Assert.IsNotNull(process);
        return process;
    }

    private static AutomationElement FindByAutomationId(AutomationElement root, string automationId) =>
        WaitForElement(root, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

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
                    // The native HWND can be visible just before the WPF UIA
                    // provider finishes registering it. Retry inside the bound.
                }
                catch (ElementNotAvailableException)
                {
                    // Window recreation during startup is transient.
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
                if (element is not null)
                {
                    return element;
                }
            }
            catch (COMException)
            {
                // Cross-process UIA providers can be briefly unavailable.
            }
            catch (ElementNotAvailableException)
            {
                // Retry until the explicit deadline.
            }

            Thread.Sleep(50);
        }

        Assert.Fail("Expected UI Automation element was not available within ten seconds.");
        throw new InvalidOperationException();
    }

    private static string WaitForValueChange(AutomationElement element, string previous)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            var value = ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
            if (!string.Equals(value, previous, StringComparison.Ordinal))
            {
                return value;
            }

            Thread.Sleep(50);
        }

        Assert.Fail("UI Automation did not observe the dynamic text change.");
        return previous;
    }

    private static void Invoke(AutomationElement element) =>
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private static void StopHost(Process host)
    {
        if (host.HasExited)
        {
            return;
        }

        host.CloseMainWindow();
        if (!host.WaitForExit(2_000))
        {
            host.Kill(entireProcessTree: true);
            host.WaitForExit(2_000);
        }
    }

    private static RepositoryPath FindRepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "WriteLite.sln")))
                {
                    return new RepositoryPath(current.FullName);
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("WriteLite repository root could not be located.");
    }

    private sealed record RepositoryPath(string Value)
    {
        public string Combine(params string[] parts) =>
            Path.Combine(new[] { Value }.Concat(parts).ToArray());
    }
}
