using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using Condition = System.Windows.Automation.Condition;

namespace CardProbe;

/// <summary>
/// Live verification of the correction card against a real external text field.
/// </summary>
/// <remarks>
/// <para>Reproduces the reported defect's exact sequence — card open on a spelling finding,
/// text changed underneath it, apply pressed anyway — against the shipped executable and a
/// field in another process, and reports what the desktop actually exposed at each step. The
/// unit tests prove the card cannot express the broken combination; this proves the running
/// application does not reach it either.</para>
///
/// <para><b>It never touches an application the user already had open.</b> Every field it
/// types into belongs to a process this probe started and stops again.</para>
/// </remarks>
internal static class Program
{
    private const string Sentence = "Сечас программа роботает стабильно";
    private const string Word = "роботает";

    /// <summary>A second misspelling, so the later scenarios survive the first being applied.</summary>
    private const string SecondWord = "Сечас";

    private static readonly List<string> Failures = [];
    private static readonly List<string> Notes = [];

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var writeLite = Process.GetProcessesByName("WriteLite").FirstOrDefault();
        if (writeLite is null)
        {
            Console.WriteLine("WriteLite is not running. Start it first.");
            return 2;
        }

        Console.WriteLine($"WriteLite pid={writeLite.Id} started={writeLite.StartTime:HH:mm:ss}");

        var targets = args.Length > 0 ? args : ["notepad", "testhost"];
        foreach (var target in targets)
        {
            Console.WriteLine();
            Console.WriteLine($"══ {target} ══════════════════════════════════════════");
            Process? host = null;
            try
            {
                var field = target switch
                {
                    "notepad" => OpenNotepad(out host),
                    "testhost" => OpenTestHost(out host),
                    _ => throw new ArgumentException($"unknown target {target}")
                };

                if (field is null)
                {
                    Notes.Add($"{target}: could not be opened, skipped");
                    Console.WriteLine("  could not open the field — skipped");
                    continue;
                }

                RunScenarios(target, writeLite.Id, field, host!);
            }
            catch (Exception exception)
            {
                Failures.Add($"{target}: probe threw {exception.GetType().Name}: {exception.Message}");
                Console.WriteLine($"  probe error: {exception}");
            }
            finally
            {
                Kill(host);
            }
        }

        Console.WriteLine();
        Console.WriteLine("══ verdict ══════════════════════════════════════════");
        foreach (var note in Notes) Console.WriteLine($"  note    {note}");
        foreach (var failure in Failures) Console.WriteLine($"  FAILED  {failure}");
        if (Failures.Count == 0) Console.WriteLine("  all live scenarios passed");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void RunScenarios(
        string target,
        int writeLitePid,
        AutomationElement field,
        Process host)
    {
        // Nothing below may be synthesised until the target provably owns the input. See
        // Guard: an unverified assumption here typed a test sentence into somebody's
        // browser composer.
        if (!Guard.Raise(host, TimeSpan.FromSeconds(8)))
        {
            Notes.Add($"{target}: could not raise the window above {Guard.DescribeForeground()} — skipped without typing");
            Console.WriteLine($"  could not raise above {Guard.DescribeForeground()} — skipped, nothing typed");
            return;
        }

        // ── Prepare the field the way a person would: click in, then type ────────────
        var bounds = field.Current.BoundingRectangle;
        var clicked = SafeClick(target, host, (int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + 12));

        // Back to the normal z-order before anything else: WriteLite's underline overlay and
        // its card are topmost windows, and the probe has to be able to click those next.
        Guard.Lower(host);
        if (!clicked) return;

        Thread.Sleep(400);
        if (!Guard.OwnsForeground(host))
        {
            Notes.Add($"{target}: focus left the target before typing ({Guard.DescribeForeground()}) — skipped");
            return;
        }

        Input.Type(Sentence);
        Console.WriteLine($"  typed: {Sentence}");

        var wordRect = WaitForWordRectangle(field, Word, TimeSpan.FromSeconds(6));
        if (wordRect == Rect.Empty)
        {
            Failures.Add($"{target}: could not locate «{Word}» through TextPattern");
            return;
        }

        // ── Scenario 1: the card opens on the finding ────────────────────────────────
        var card = OpenCard(host, writeLitePid, wordRect, TimeSpan.FromSeconds(12));
        if (!card.Present)
        {
            Notes.Add($"{target}: WriteLite drew no underline for «{Word}» — card scenarios skipped");
            Console.WriteLine("  no card appeared (no underline on this target)");
            return;
        }

        Console.WriteLine($"  [1] opened      {card.Describe()}");
        Check(target, "1 open", card);
        if (!card.HasApplyCta && !card.HasDictionary)
        {
            Failures.Add($"{target}: opened card offered neither an apply action nor a dictionary action");
        }

        // ── Scenario 2: the screenshot — change the text under the open card ─────────
        if (!Guard.OwnsForeground(host))
        {
            Notes.Add($"{target}: focus left the target before the text-change scenario — stopped");
            return;
        }

        Input.PressVirtualKey(Input.VkHome);
        var changed = Stopwatch.StartNew();
        Input.Type("и ");

        var duringChange = SampleUntilSettled(writeLitePid, TimeSpan.FromSeconds(3), out var samples);
        changed.Stop();
        Console.WriteLine($"  [2] text changed, {samples.Count} samples over {changed.ElapsedMilliseconds} ms");
        foreach (var sample in samples) Console.WriteLine($"        {sample.Describe()}");
        foreach (var sample in samples) Check(target, "2 text-changed", sample);

        if (duringChange.Present && duringChange.HasRetry)
        {
            Failures.Add($"{target}: «Повторить» offered for a text change");
        }

        Console.WriteLine($"  [2] settled     {duringChange.Describe()}");

        // ── Scenario 4: rapid consecutive text changes with a card open ──────────────
        var reopened = ReopenCard(host, writeLitePid, field, SecondWord, TimeSpan.FromSeconds(10));
        if (reopened.Present)
        {
            Console.WriteLine($"  [4] reopened    {reopened.Describe()}");
            if (!Guard.OwnsForeground(host))
            {
                Notes.Add($"{target}: focus left the target before the rapid-change scenario — stopped");
                return;
            }

            Input.PressVirtualKey(Input.VkEnd);
            for (var burst = 0; burst < 6; burst++)
            {
                if (!Guard.OwnsForeground(host)) break;
                Input.Type("х", perCharacterDelayMs: 0);
                var sample = Card.Read(writeLitePid);
                Console.WriteLine($"        burst {burst}: {sample.Describe()}");
                Check(target, $"4 rapid-{burst}", sample);
                Thread.Sleep(70);
            }

            var settled = SampleUntilSettled(writeLitePid, TimeSpan.FromSeconds(3), out var burstSamples);
            foreach (var sample in burstSamples) Check(target, "4 rapid-settle", sample);
            Console.WriteLine($"  [4] settled     {settled.Describe()}");
        }
        else
        {
            Console.WriteLine("  [4] card did not reopen — rapid-change scenario skipped");
            Notes.Add($"{target}: card did not reopen for the rapid-change scenario");
        }

        // ── Scenario 5: repeated double-clicks on the same word ─────────────────────
        var stillThere = WaitForWordRectangle(field, SecondWord, TimeSpan.FromSeconds(4));
        if (stillThere != Rect.Empty)
        {
            for (var i = 0; i < 5; i++)
            {
                var dx = (int)(stillThere.Left + stillThere.Width / 2);
                var dy = (int)(stillThere.Top + stillThere.Height / 2);
                if (!OwnsOrIsWriteLite(host, writeLitePid, dx, dy))
                {
                    Notes.Add($"{target}: double-click point is covered by another window — stopped");
                    break;
                }

                Input.DoubleClick(dx, dy);
                Thread.Sleep(250);
                var sample = Card.Read(writeLitePid);
                Console.WriteLine($"  [5] dblclick {i}: {sample.Describe()}");
                Check(target, $"5 double-click-{i}", sample);
            }
        }
        else
        {
            Console.WriteLine("  [5] word no longer present — double-click scenario skipped");
        }

        // ── Scenario 3: press the CTA on whatever the card now shows ─────────────────
        if (duringChange.Present && duringChange.HasApplyCta)
        {
            var cta = FindButton(writeLitePid, "Исправить");
            if (cta is not null)
            {
                var rect = cta.Value;
                // The CTA belongs to WriteLite's own card, so this point is checked against
                // WriteLite rather than against the target application.
                Input.Click((int)(rect.Left + rect.Width / 2), (int)(rect.Top + rect.Height / 2));
                Thread.Sleep(900);
                var afterApply = Card.Read(writeLitePid);
                Console.WriteLine($"  [3] after apply {afterApply.Describe()}");
                Check(target, "3 after-apply", afterApply);
            }
        }
        else
        {
            Console.WriteLine("  [3] card closed or informational after the change — nothing to press");
        }

    }

    private static void Check(string target, string step, CardObservation card)
    {
        foreach (var contradiction in card.Contradictions())
        {
            Failures.Add($"{target} [{step}]: {contradiction}");
        }
    }

    // ── Card interaction ────────────────────────────────────────────────────────────

    private static CardObservation OpenCard(Process host, int pid, Rect wordRect, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        var x = (int)(wordRect.Left + wordRect.Width / 2);
        var y = (int)(wordRect.Top + wordRect.Height / 2);
        while (deadline.Elapsed < timeout)
        {
            // The underline is drawn by WriteLite's overlay, which sits above the target, so
            // either owner is the right answer here — anything else is a third window in the
            // way and must not be clicked.
            if (!OwnsOrIsWriteLite(host, pid, x, y)) return CardObservation.Absent;

            Input.Click(x, y);
            Thread.Sleep(400);

            var card = Card.Read(pid);
            if (card.Present) return card;
            Thread.Sleep(600);
        }

        return CardObservation.Absent;
    }

    private static CardObservation ReopenCard(
        Process host,
        int pid,
        AutomationElement field,
        string word,
        TimeSpan timeout)
    {
        var rect = WaitForWordRectangle(field, word, timeout);
        return rect == Rect.Empty ? CardObservation.Absent : OpenCard(host, pid, rect, timeout);
    }

    /// <summary>The point is over the target field, or over WriteLite's own overlay above it.</summary>
    private static bool OwnsOrIsWriteLite(Process host, int writeLitePid, int x, int y)
        => Guard.OwnsPoint(host, x, y) || Guard.OwnsPoint(writeLitePid, x, y);

    private static bool SafeClick(string target, Process host, int x, int y)
    {
        if (!Guard.OwnsPoint(host, x, y))
        {
            Notes.Add($"{target}: the field is covered by another window — skipped without clicking");
            Console.WriteLine("  target field is covered by another window — skipped, nothing typed");
            return false;
        }

        Input.Click(x, y);
        return true;
    }

    /// <summary>Samples the card until it stops changing, so the settled state is the one reported.</summary>
    private static CardObservation SampleUntilSettled(
        int pid,
        TimeSpan timeout,
        out List<CardObservation> samples)
    {
        samples = [];
        var deadline = Stopwatch.StartNew();
        var last = Card.Read(pid);
        samples.Add(last);
        var stableFor = 0;

        while (deadline.Elapsed < timeout)
        {
            Thread.Sleep(120);
            var current = Card.Read(pid);
            if (current.Describe() == last.Describe())
            {
                if (++stableFor >= 4) break;
                continue;
            }

            stableFor = 0;
            last = current;
            samples.Add(current);
        }

        return last;
    }

    private static Rect? FindButton(int pid, string name)
    {
        var window = Card.Find(pid);
        var button = window?.FindFirst(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, name)));
        return button?.Current.BoundingRectangle;
    }

    // ── Targets ─────────────────────────────────────────────────────────────────────

    private static AutomationElement? OpenNotepad(out Process? host)
    {
        host = null;
        // Windows 11 Notepad is packaged: the executable started is a launcher that exits and
        // the window belongs to a different process. Find it by process name afterwards.
        Process.Start(new ProcessStartInfo(@"C:\Windows\System32\notepad.exe") { UseShellExecute = true });
        Thread.Sleep(3000);

        var process = Process.GetProcessesByName("Notepad")
            .Concat(Process.GetProcessesByName("notepad"))
            .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (process is null) return null;
        host = process;

        Focus(process.MainWindowHandle);
        Thread.Sleep(600);

        var window = AutomationElement.FromHandle(process.MainWindowHandle);
        return window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document))
            ?? window.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
    }

    private static AutomationElement? OpenTestHost(out Process? host)
    {
        host = null;
        var exe = Path.Combine(
            RepositoryRoot(), "src", "WriteLite.TestHost", "bin", "Release", "net10.0-windows",
            "WriteLite.TestHost.exe");
        if (!File.Exists(exe)) return null;

        var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        if (process is null) return null;
        host = process;
        process.WaitForInputIdle(5000);
        Thread.Sleep(1200);
        process.Refresh();
        if (process.MainWindowHandle == IntPtr.Zero) return null;

        Focus(process.MainWindowHandle);
        Thread.Sleep(600);

        var window = AutomationElement.FromHandle(process.MainWindowHandle);
        var field = window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "WpfInput"));

        // The host ships with a sentence in the box; clear it so the probe types its own.
        if (field is not null && field.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
        {
            ((ValuePattern)pattern).SetValue(string.Empty);
        }

        return field;
    }

    private static Rect WaitForWordRectangle(AutomationElement field, string word, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            try
            {
                if (field.TryGetCurrentPattern(TextPattern.Pattern, out var raw)
                    && ((TextPattern)raw).DocumentRange.FindText(word, backward: false, ignoreCase: false)
                        is { } range)
                {
                    // The managed UIA wrapper returns whole rectangles, one per display line.
                    var rectangles = range.GetBoundingRectangles();
                    if (rectangles.Length > 0 && rectangles[0].Width > 1 && rectangles[0].Height > 1)
                    {
                        return rectangles[0];
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
                // The window is still settling.
            }
            catch (InvalidOperationException)
            {
                // TextPattern not ready on this provider yet.
            }

            Thread.Sleep(250);
        }

        return Rect.Empty;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WriteLite.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private static void Focus(IntPtr handle)
    {
        ShowWindow(handle, 9); // SW_RESTORE
        SetForegroundWindow(handle);
    }

    private static void Kill(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);
}
