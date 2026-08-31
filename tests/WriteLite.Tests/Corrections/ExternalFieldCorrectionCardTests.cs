using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Views;
using Condition = System.Windows.Automation.Condition;

namespace WriteLite.Tests.Corrections;

/// <summary>
/// The correction card's lifecycle against a real out-of-process text field.
/// </summary>
/// <remarks>
/// <para>The reported defect only happens where WriteLite actually runs: a card open over
/// somebody else's edit control while that control's text changes underneath it. The other
/// tests in this folder pin the decisions and the rendering in isolation; this one runs the
/// same coordinator and the same card window against a genuine cross-process UI Automation
/// target, so the identity comparison, the text reads and the offsets are the real ones.</para>
///
/// <para>The field belongs to <c>WriteLite.TestHost</c>, which this test starts and stops.
/// Nothing here touches an application the user already had open.</para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ExternalFieldCorrectionCardTests
{
    private const string Opened = "он роботает сейчас";
    private static readonly Rect Anchor = new(200, 200, 60, 18);

    [TestMethod]
    [TestCategory("UIA")]
    [TestProperty("Category", "UIA")]
    public void ACardOverAnExternalFieldFollowsThatFieldsTextOrCloses()
    {
        using var host = StartTestHost();
        try
        {
            var element = WaitForElement(
                WaitForMainWindow(host),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "WpfInput"));

            WpfTestHost.RunAsync(async () =>
            {
                WpfTestHost.EnsureThemeApplied();

                var target = new AutomationTextTarget(element, new ValuePatternTextAdapter());
                var coordinator = new CorrectionCardCoordinator();
                var popup = new CorrectionPopupWindow();

                // ── The card opens over the word as the field actually reads it ──────────
                SetFieldText(element, Opened);
                var opened = await ReadAsync(target);
                Assert.AreEqual(Opened, opened, "the field did not take the prepared text");

                var issue = SpellingAt(opened, "роботает", "работает");
                coordinator.Open(ContextFor(target, opened, [issue]), issue);
                popup.ShowForIssue(issue, Anchor);
                popup.UpdateLayout();
                Assert.Contains("Исправить", CorrectionPopupFallbackTests.ButtonsOf(popup));

                // ── The user types in front of the word, in the other application ────────
                SetFieldText(element, "и " + Opened);
                var edited = await ReadAsync(target);

                var reaction = Stopwatch.StartNew();

                // The monitor announces the edit before any analysis of it exists.
                Assert.AreEqual(
                    CorrectionCardActionKind.Recheck,
                    coordinator.Observe(ContextFor(target, edited, [], analysisInFlight: true)).Kind);
                popup.BeginRecheck();
                popup.UpdateLayout();

                var duringCheck = CorrectionPopupFallbackTests.ButtonsOf(popup);
                Assert.IsFalse(duringCheck.Contains("Исправить"), "the old result's action is gone at once");
                Assert.IsFalse(duringCheck.Contains("Повторить"), "editing text is not a failure to retry");
                Assert.IsFalse(CorrectionPopupFallbackTests.TextOf(popup)
                    .Any(t => t.Contains("Текст изменился", StringComparison.Ordinal)));

                // The pass finishes and reports the same word two characters further along.
                var refreshed = SpellingAt(edited, "роботает", "работает");
                var action = coordinator.Observe(ContextFor(target, edited, [refreshed]));
                Assert.AreEqual(CorrectionCardActionKind.Render, action.Kind);
                popup.ShowForIssue(action.Issue!, Anchor, popup.BeginRecheck());
                popup.UpdateLayout();
                reaction.Stop();

                Assert.AreEqual(CorrectionCardPhase.Result, popup.Phase);
                Assert.Contains("Исправить", CorrectionPopupFallbackTests.ButtonsOf(popup));
                Assert.AreEqual(opened.IndexOf("роботает", StringComparison.Ordinal) + 2, action.Issue!.Start,
                    "the card followed the word to where the edit put it");

                // §8: the card's own reaction to a change is immediate. What the user waits
                // for beyond this is the monitor's analysis debounce, which this does not
                // measure and does not add to.
                Assert.IsLessThan(1000, reaction.Elapsed.TotalMilliseconds,
                    $"card took {reaction.Elapsed.TotalMilliseconds:F0} ms to leave the old result and draw the new one");

                // ── The user then edits the word itself: there is nothing to follow ──────
                SetFieldText(element, "и он работает сейчас");
                var corrected = await ReadAsync(target);

                Assert.AreEqual(
                    CorrectionCardActionKind.Dismiss,
                    coordinator.Observe(ContextFor(target, corrected, [])).Kind);
                popup.Dismiss();
                popup.UpdateLayout();

                Assert.AreEqual(CorrectionCardPhase.Hidden, popup.Phase);
                Assert.IsFalse(popup.IsVisible, "a card about text that no longer exists closes");

                popup.Close();
            });
        }
        finally
        {
            StopHost(host);
        }
    }

    private static CorrectionCardContext ContextFor(
        AutomationTextTarget target,
        string text,
        IReadOnlyList<TextIssue> issues,
        bool analysisInFlight = false) =>
        new(target.Identity.RuntimeId, text, issues, analysisInFlight);

    private static async Task<string> ReadAsync(AutomationTextTarget target)
    {
        var read = await target.TryReadTextAsync();
        Assert.IsTrue(read.Succeeded, "could not read the external field back");
        return read.Text;
    }

    private static TextIssue SpellingAt(string text, string word, string replacement)
    {
        var start = text.IndexOf(word, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, $"«{word}» is not in «{text}»");
        return new TextIssue(
            start, word.Length, word, replacement,
            "Орфография", "Слово не найдено в русском орфографическом словаре.",
            IssueCategory.Orthography, IssueSeverity.Warning,
            CanApplyAutomatically: false, RuleId: "ru.spelling.typo");
    }

    private static void SetFieldText(AutomationElement element, string text)
    {
        ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);

        // The provider reports the new value a beat after the set on a busy machine.
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (string.Equals(
                    ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current.Value,
                    text,
                    StringComparison.Ordinal))
            {
                return;
            }

            Thread.Sleep(20);
        }

        Assert.Fail($"the test host field never reported «{text}»");
    }

    // ── Host plumbing, mirroring FieldMonitorApplyTests ──────────────────────

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
            var element = root.FindFirst(TreeScope.Descendants, condition);
            if (element is not null) return element;
            Thread.Sleep(50);
        }

        Assert.Fail("Test host did not expose the expected input field.");
        throw new InvalidOperationException();
    }
}
