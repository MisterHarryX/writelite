using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using WriteLite.Services;

namespace FieldMatrix;

/// <summary>One row of the compatibility matrix: how to reach a field of a given technology.</summary>
internal sealed record ProbeTarget(
    string Name,
    string Technology,
    Func<ProbeSession?> Open);

/// <summary>A field that has been opened, focused and filled, ready to be probed.</summary>
internal sealed record ProbeSession(AutomationElement Element, Process? Process, string Text);

/// <summary>
/// The applications and control technologies this machine can actually be measured against.
/// </summary>
/// <remarks>
/// §13 and §15: the matrix reports what was run, and a target that is not installed produces
/// an explicit "unavailable" row rather than an assumption. Nothing here is keyed on process
/// name for behaviour — the executables are how the probe launches an application, not how
/// WriteLite decides anything about it.
/// </remarks>
internal static class Targets
{
    public const string Sentence =
        "Привет Алексей я хотел узнать сможешь ли ты завтра приехать в офис. "
        + "Сечас программа роботает стабильней но некоторые ошибки всё ещё возникает.";

    /// <summary>
    /// The targets that are safe to run unattended.
    /// </summary>
    /// <remarks>
    /// Everything here is a scratch field in a process the probe started itself. Nothing in
    /// this list can lose a person's work.
    /// </remarks>
    public static IReadOnlyList<ProbeTarget> All(string repoRoot) =>
    [
        Wpf(repoRoot, "WPF TextBox", "WpfInput"),
        Wpf(repoRoot, "WPF TextBox (multiline)", "MultilineInput"),
        Wpf(repoRoot, "WPF RichTextBox", "RichInput"),
        Wpf(repoRoot, "WinForms TextBox", "WinFormsHost"),
        Wpf(repoRoot, "WPF TextBox (read-only)", "ReadOnlyInput", fill: false),
        Wpf(repoRoot, "WPF PasswordBox", "PasswordInput", fill: false),
        Notepad(),
        Browser("Browser <input>", "input"),
        Browser("Browser <textarea>", "textarea"),
        Browser("Browser contenteditable", "editable"),
    ];

    /// <summary>
    /// Targets that type into an application the user already has open.
    /// </summary>
    /// <remarks>
    /// <para><b>Kept out of the default run on purpose.</b> A messenger composer may hold a
    /// draft and a word processor holds the user's document; typing a probe sentence into
    /// either destroys work that is not the probe's to touch. An earlier version of this file
    /// had these in the unattended list and appended the probe sentence to an open Word
    /// document — a real cost for a matrix row.</para>
    ///
    /// <para>Run with <c>--include-open-apps</c>, against an empty scratch document or
    /// conversation, and only deliberately.</para>
    /// </remarks>
    public static IReadOnlyList<ProbeTarget> OpenApplications() =>
    [
        Running("Telegram composer", "Qt (Telegram Desktop)", "Telegram"),
        Running("Discord composer", "Electron", "Discord"),
        Running("VS Code editor", "Electron", "Code"),
        Running("Word document", "Office", "WINWORD"),
    ];

    private static ProbeTarget Wpf(string repoRoot, string name, string automationId, bool fill = true)
        => new(name, "WPF / WinForms", () =>
        {
            var host = Path.Combine(
                repoRoot, "src", "WriteLite.TestHost", "bin", "Release", "net10.0-windows", "WriteLite.TestHost.exe");
            if (!File.Exists(host))
            {
                Console.WriteLine($"  test host not built: {host}");
                return null;
            }

            var process = TestHost.Value ?? Driver.Launch(host);
            if (process is null) return null;
            TestHost.Value ??= process;

            var element = Driver.FindByAutomationId(process.Id, automationId);
            if (element is null) return null;

            // The WinForms host is a container; the edit control is the child inside it.
            if (automationId == "WinFormsHost")
            {
                element = element.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)) ?? element;
            }

            if (!Driver.Focus(element)) return null;
            if (fill) Fill(element);
            return new ProbeSession(element, process, Sentence);
        });

    /// <summary>
    /// Types the probe sentence and makes sure it actually landed.
    /// </summary>
    /// <remarks>
    /// The first keystrokes after a window is raised are routinely swallowed while the
    /// application finishes activating, which produced a truncated sentence and a row that
    /// said SKIP for a control that works. One retry is enough; a second failure is a real
    /// finding about the target and is reported as one.
    /// </remarks>
    private static bool Fill(AutomationElement element)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Driver.TypeIntoFocused(Sentence);
            if (Contains(element, "роботает")) return true;

            Console.WriteLine($"  retype (attempt {attempt + 1} did not land)");
            Driver.Focus(element);
        }

        return Contains(element, "роботает");
    }

    /// <summary>
    /// Types the probe sentence into a field the user has focused, refusing to overwrite
    /// anything already there.
    /// </summary>
    public static bool FillFocused(AutomationElement element)
    {
        var existing = ReadText(element);
        if (existing.Length > 0 && !existing.Contains("роботает", StringComparison.Ordinal))
        {
            Console.WriteLine($"  REFUSED — the field already holds {existing.Length} characters.");
            return false;
        }

        return Fill(element);
    }

    private static bool Contains(AutomationElement element, string marker)
    {
        try
        {
            var adapter = new CompositeTextTargetAdapter().Select(element);
            if (adapter is null) return false;
            var read = adapter.ReadTextAsync(element).GetAwaiter().GetResult();
            return read.Succeeded && read.Text.Contains(marker, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static readonly Box<Process?> TestHost = new();

    private static ProbeTarget Notepad() => new("Notepad", "Win32 Edit / RichEdit", () =>
    {
        // Windows 11 Notepad is a packaged application: the executable that is started is a
        // launcher that exits, and the window belongs to a different process. Find it by
        // process name rather than by the id we happen to have — and not by window title,
        // which is localised.
        Driver.Launch(@"C:\Windows\System32\notepad.exe", waitMs: 8000);
        Thread.Sleep(2500);

        var window = Driver.MainWindowOf("Notepad");
        if (window is null)
        {
            Console.WriteLine("  Notepad window not found");
            return null;
        }

        var element = Driver.FirstTextField(window);
        if (element is null || !Driver.Focus(element)) return null;

        Fill(element);
        return new ProbeSession(element, null, Sentence);
    });

    /// <summary>
    /// A local page carrying the three browser shapes, so the row measures the control rather
    /// than whatever a live site happens to render.
    /// </summary>
    private static ProbeTarget Browser(string name, string elementId) => new(name, "Chromium", () =>
    {
        var page = WritePage();
        var browser = ChromiumBrowser();
        if (browser is null)
        {
            Console.WriteLine("  no Chromium browser installed");
            return null;
        }

        var process = BrowserProcess.Value;
        if (process is null || process.HasExited)
        {
            process = Driver.Launch(browser, $"--new-window \"file:///{page.Replace('\\', '/')}\"", waitMs: 12000);
            if (process is null) return null;
            Thread.Sleep(3500);
            BrowserProcess.Value = process;
        }

        // Chromium hosts every tab's tree under the browser's own process id in UIA.
        var element = FindBrowserField(elementId);
        if (element is null || !Driver.Focus(element)) return null;

        Fill(element);
        return new ProbeSession(element, process, Sentence);
    });

    /// <summary>The first Chromium browser on this machine, or null when there is none.</summary>
    private static string? ChromiumBrowser()
    {
        string[] candidates =
        [
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\Opera GX\opera.exe"),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static readonly Box<Process?> BrowserProcess = new();

    private static AutomationElement? FindBrowserField(string elementId)
    {
        var deadline = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                var byName = AutomationElement.RootElement.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "wl-" + elementId));
                if (byName is not null) return byName;
            }
            catch (Exception exception) when (exception is ElementNotAvailableException
                                                  or InvalidOperationException)
            {
                // Renderer still building its tree.
            }

            Thread.Sleep(500);
        }

        return null;
    }

    private static string WritePage()
    {
        var path = Path.Combine(Path.GetTempPath(), "writelite-fieldmatrix.html");
        File.WriteAllText(path, """
            <!doctype html><html lang="ru"><head><meta charset="utf-8"><title>WriteLite field matrix</title>
            <style>body{font:16px system-ui;padding:24px;display:grid;gap:18px;max-width:760px}
            input,textarea,div.ce{font:16px system-ui;padding:10px;width:100%;box-sizing:border-box}
            div.ce{border:1px solid #999;min-height:80px}</style></head><body>
            <h1>WriteLite field matrix</h1>
            <label>input <input id="input" aria-label="wl-input"></label>
            <label>textarea <textarea id="textarea" aria-label="wl-textarea" rows="4"></textarea></label>
            <label>contenteditable</label>
            <div class="ce" id="editable" contenteditable="true" role="textbox" aria-label="wl-editable"></div>
            </body></html>
            """);
        return path;
    }

    /// <summary>
    /// A target that is already running: bring its window forward, put the caret in its main
    /// text field, and measure that.
    /// </summary>
    /// <remarks>
    /// <para>The application is not launched. Messengers and editors need an account, an
    /// open conversation or an open document before they have an editable field at all, and
    /// a probe that starts them cold measures a splash screen.</para>
    ///
    /// <para>The measurement is refused unless the control that ends up focused belongs to
    /// the expected process. Without that check the probe measured whatever window happened
    /// to be in front — on this machine, a browser — and produced rows naming applications
    /// it had never touched. A row that cannot be trusted is worse than one that says
    /// "not reached".</para>
    /// </remarks>
    private static ProbeTarget Running(string name, string technology, string processName)
        => new(name, technology, () =>
        {
            var window = Driver.MainWindowOf(processName, waitMs: 2000);
            if (window is null)
            {
                Console.WriteLine($"  {processName} is not running");
                return null;
            }

            var element = Driver.FirstTextField(window);
            if (element is null)
            {
                Console.WriteLine("  no text field found in the main window");
                return null;
            }

            if (!Driver.Focus(element)) return null;

            var actual = ProcessNameOf(element);
            if (!string.Equals(actual, processName, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  NOT REACHED — focus landed in «{actual}»");
                return null;
            }

            // Never overwrite content the probe did not put there.
            var existing = ReadText(element);
            if (existing.Length > 0 && !existing.Contains("роботает", StringComparison.Ordinal))
            {
                Console.WriteLine($"  REFUSED — the field already holds {existing.Length} characters. "
                                  + "Open an empty document or conversation and run again.");
                return null;
            }

            Fill(element);
            return new ProbeSession(element, null, Sentence);
        });

    private static string ReadText(AutomationElement element)
    {
        try
        {
            var adapter = new CompositeTextTargetAdapter().Select(element);
            if (adapter is null) return string.Empty;
            var read = adapter.ReadTextAsync(element).GetAwaiter().GetResult();
            return read.Succeeded ? read.Text : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ProcessNameOf(AutomationElement element)
    {
        try { return Process.GetProcessById(element.Current.ProcessId).ProcessName; }
        catch { return "?"; }
    }

    private sealed class Box<T>
    {
        public T? Value { get; set; }
    }
}
