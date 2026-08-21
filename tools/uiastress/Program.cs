using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace WriteLite.UiaStress;

/// <summary>
/// Drives the real WriteLite desktop application until something breaks.
/// </summary>
/// <remarks>
/// The unit tests exercise the same code on the same thread they assert from, which
/// makes them structurally incapable of catching the failures this pass is about: a
/// blocked dispatcher, a window that stops answering, a page that accumulates state
/// across a hundred visits. This drives the shipped executable from another process, so
/// "the UI froze" is something it can actually observe — and it keeps observing while
/// the application is busy, which is the only moment the observation is worth anything.
///
/// Usage: <c>uiastress &lt;path-to-WriteLite.exe&gt; [scenario…]</c>, scenarios being
/// the letters A–G. With none given it runs all of them.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: uiastress <WriteLite.exe> [A B C D E F G]");
            return 2;
        }

        var executable = Path.GetFullPath(args[0]);
        if (!File.Exists(executable))
        {
            Console.Error.WriteLine($"not found: {executable}");
            return 2;
        }

        var wanted = args.Skip(1).Select(argument => argument.ToUpperInvariant()).ToHashSet();
        if (wanted.Count == 0)
        {
            wanted = ["A", "B", "C", "D", "E", "F", "G"];
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"WriteLite UI stress · {executable}");
        Console.WriteLine(new string('─', 78));

        using var ui = Launch(executable);
        var results = new List<Result>();

        foreach (var (letter, name, scenario) in Scenarios.All)
        {
            if (!wanted.Contains(letter))
            {
                continue;
            }

            results.Add(Run(ui, letter, name, scenario));
        }

        Console.WriteLine(new string('─', 78));

        foreach (var result in results)
        {
            Console.WriteLine(
                $"{(result.Passed ? "PASS" : "FAIL")}  {result.Letter}  {result.Name,-38} " +
                $"{result.Duration.TotalSeconds,6:0.0}s  worst stall {result.WorstStall.TotalMilliseconds,6:0} ms");

            if (!result.Passed)
            {
                Console.WriteLine($"        {result.Failure}");
            }
        }

        var alive = ui.IsAlive;
        Console.WriteLine(new string('─', 78));
        Console.WriteLine($"process alive at end: {alive}   working set: {(alive ? ui.WorkingSetMb + " MB" : "n/a")}");

        var failed = results.Count(result => !result.Passed);
        Console.WriteLine(failed == 0 && alive
            ? $"ALL {results.Count} SCENARIOS PASSED"
            : $"{failed} SCENARIO(S) FAILED");

        return failed == 0 && alive ? 0 : 1;
    }

    private static Result Run(Ui ui, string letter, string name, Action<Ui> scenario)
    {
        Console.WriteLine($"▸ {letter} — {name}");

        var before = ui.WorstStall;
        var clock = Stopwatch.StartNew();

        try
        {
            scenario(ui);
            ui.Ping();
            clock.Stop();

            return new Result(letter, name, true, clock.Elapsed, ui.WorstStall - before, null);
        }
        catch (Exception exception)
        {
            clock.Stop();
            return new Result(letter, name, false, clock.Elapsed, ui.WorstStall - before, exception.Message);
        }
    }

    /// <summary>
    /// Starts the application and gets it to a usable main window.
    /// </summary>
    /// <remarks>
    /// The document-recovery prompt is a modal owned by the editor and appears whenever
    /// a previous session left an unsaved file behind — which, after a run that ends by
    /// killing the process, is every time. Dismissing it is part of getting to the shell
    /// rather than something the scenarios should each have to handle.
    /// </remarks>
    private static Ui Launch(string executable)
    {
        foreach (var stale in Process.GetProcessesByName("WriteLite"))
        {
            using (stale)
            {
                try
                {
                    stale.Kill(entireProcessTree: true);
                    stale.WaitForExit(5_000);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
            }
        }

        var process = Process.Start(new ProcessStartInfo(executable, "--show-main-window")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        }) ?? throw new StressException("WriteLite did not start.");

        var ui = new Ui(process);

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(90))
        {
            if (!ui.IsAlive)
            {
                throw new StressException($"WriteLite exited during startup with code {process.ExitCode}.");
            }

            DismissRecoveryPrompt(ui);

            if (ui.ByName("Главная", timeoutMs: 500) is not null)
            {
                Console.WriteLine($"started in {deadline.Elapsed.TotalSeconds:0.0}s (pid {process.Id})");
                Thread.Sleep(1_500);
                return ui;
            }

            Thread.Sleep(250);
        }

        // Say what was actually on screen rather than only that the rail was not.
        var seen = new List<string>();
        foreach (var window in ui.Windows())
        {
            try
            {
                seen.Add($"«{window.Current.Name}» [{window.Current.ClassName}]");
            }
            catch (ElementNotAvailableException)
            {
                seen.Add("<gone>");
            }
        }

        throw new StressException(
            $"The navigation rail never appeared after 90 s. Windows seen: " +
            (seen.Count == 0 ? "none" : string.Join(", ", seen)));
    }

    private static void DismissRecoveryPrompt(Ui ui)
    {
        foreach (var window in ui.Windows())
        {
            try
            {
                if (!window.Current.ClassName.Equals("#32770", StringComparison.Ordinal))
                {
                    continue;
                }

                // A Win32 message box. "Нет" leaves the recovered document alone.
                var walker = TreeWalker.ControlViewWalker;
                for (var child = walker.GetFirstChild(window); child is not null; child = walker.GetNextSibling(child))
                {
                    if (child.Current.Name is "Нет" or "No")
                    {
                        var rect = child.Current.BoundingRectangle;
                        Input.ClickScreen((int)(rect.Left + rect.Width / 2), (int)(rect.Top + rect.Height / 2));
                        Thread.Sleep(400);
                        return;
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
                // The dialog closed itself while being inspected.
            }
        }
    }

    private readonly record struct Result(
        string Letter,
        string Name,
        bool Passed,
        TimeSpan Duration,
        TimeSpan WorstStall,
        string? Failure);
}
