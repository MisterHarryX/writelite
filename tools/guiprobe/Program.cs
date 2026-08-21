using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Clipboard = System.Windows.Forms.Clipboard;

namespace GuiProbe;

/// <summary>
/// Drives the real WriteLite window through UI Automation and reports what a person sees.
/// </summary>
/// <remarks>
/// §40 asks for the sprint to be validated by interacting with the application rather than by
/// observing that a binary launched. Everything here goes through the same accessibility
/// surface a screen reader would use: no internal hooks, no injected state, no privileged
/// access to the analyzer. The numbers are wall-clock from a synthesised click to a change
/// visible in the tree.
/// </remarks>
internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const int SwRestore = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public unsafe fixed byte Padding[24];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VkControl = 0x11;

    /// <summary>
    /// Presses Ctrl+<paramref name="key"/> as four separate input events.
    /// </summary>
    /// <remarks>
    /// <c>SendKeys</c> was tried first and did not work: WriteLite received the letters without
    /// the modifier, so "^a^v" typed the literal text "av" into the document — visible in the
    /// probe's own diagnostics as the character count growing by exactly two per attempt.
    /// <c>SendInput</c> with explicit key-down and key-up events puts the modifier into the
    /// keyboard state the target actually reads.
    /// </remarks>
    private static void SendControlKey(char key)
    {
        var code = (ushort)char.ToUpperInvariant(key);
        var inputs = new[]
        {
            KeyEvent(VkControl, false),
            KeyEvent(code, false),
            KeyEvent(code, true),
            KeyEvent(VkControl, true),
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        Thread.Sleep(60);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const uint InputMouse = 0;
    private const uint MouseMove = 0x0001;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    /// <summary>
    /// Clicks the middle of an element, the way a person gives a text box the caret.
    /// </summary>
    /// <remarks>
    /// <c>AutomationElement.SetFocus</c> alone was not enough for the editor: the probe held
    /// the foreground and sent a correct Ctrl+V, and nothing arrived, because the WPF
    /// <c>RichTextBox</c> behind the Document peer had no keyboard focus. A real click is what
    /// the product is built to receive, so that is what this sends.
    /// </remarks>
    private static void ClickCentre(AutomationElement element)
    {
        var bounds = element.Current.BoundingRectangle;
        if (bounds.IsEmpty) return;

        var x = (int)(bounds.Left + bounds.Width / 2);
        var y = (int)(bounds.Top + bounds.Height / 2);

        var screenWidth = GetSystemMetrics(SmCxScreen);
        var screenHeight = GetSystemMetrics(SmCyScreen);
        var absoluteX = x * 65535 / Math.Max(1, screenWidth - 1);
        var absoluteY = y * 65535 / Math.Max(1, screenHeight - 1);

        var inputs = new[]
        {
            MouseEvent(absoluteX, absoluteY, MouseMove | MouseAbsolute),
            MouseEvent(absoluteX, absoluteY, MouseLeftDown | MouseAbsolute),
            MouseEvent(absoluteX, absoluteY, MouseLeftUp | MouseAbsolute),
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        Thread.Sleep(200);
    }

    private static Input MouseEvent(int x, int y, uint flags) => new()
    {
        Type = InputMouse,
        Union = new InputUnion
        {
            Mouse = new MouseInput { X = x, Y = y, Flags = flags }
        }
    };

    private static Input KeyEvent(ushort virtualKey, bool up) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = up ? KeyEventKeyUp : 0
            }
        }
    };

    /// <summary>
    /// Puts a window in front from a console process.
    /// </summary>
    /// <remarks>
    /// Windows refuses <c>SetForegroundWindow</c> from a process that does not own the
    /// foreground, which is why the first attempt at this probe pasted into nothing. Attaching
    /// this thread's input queue to the current foreground thread lifts that restriction for
    /// the duration of the call — the standard way to do it, and the reason the synthesised
    /// keystrokes below land in WriteLite rather than in the console.
    /// </remarks>
    private static void ForceForeground(IntPtr handle)
    {
        ShowWindow(handle, SwRestore);

        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var thisThread = GetCurrentThreadId();

        if (foregroundThread != thisThread)
        {
            AttachThreadInput(thisThread, foregroundThread, true);
            SetForegroundWindow(handle);
            AttachThreadInput(thisThread, foregroundThread, false);
        }
        else
        {
            SetForegroundWindow(handle);
        }
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var exe = args.FirstOrDefault()
                  ?? Path.Combine(
                      RepoRoot(), "src", "WriteLite.App", "bin", "Debug", "net10.0-windows", "WriteLite.exe");
        var samplePath = args.Skip(1).FirstOrDefault()
                         ?? Path.Combine(RepoRoot(), "benchmarks", "realtext", "sample-463.txt");

        if (!File.Exists(exe)) { Console.WriteLine($"FAIL: no binary at {exe}"); return 2; }
        var sample = File.ReadAllText(samplePath);
        Console.WriteLine($"sample: {sample.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length} words, {sample.Length} chars");

        // WriteLite starts to the tray; the shell only exists once something asks for it.
        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        startInfo.ArgumentList.Add("--show-main-window");
        var process = Process.Start(startInfo)!;

        try
        {
            var window = WaitForWindow(process);
            Console.WriteLine($"[startup] main window '{window.Current.Name}' after {Elapsed()} ms");

            ForceForeground(process.MainWindowHandle);
            Thread.Sleep(2500);

            // A leftover recovery snapshot puts a modal question in front of everything.
            // Answer it the way a user opening a fresh document would.
            DismissModal(process, "Нет");

            // The suggestions panel becomes a toggle below 1080 px and is not in the tree at
            // all while collapsed, so the window is maximised before anything is measured.
            if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var windowPattern))
            {
                ((WindowPattern)windowPattern).SetWindowVisualState(WindowVisualState.Maximized);
                Thread.Sleep(1200);
            }


            // Open the editor section.
            var nav = WaitFor(window, "NavPanel", TimeSpan.FromSeconds(20));
            Toggle(nav);
            Thread.Sleep(2500);
            Console.WriteLine($"[startup] page is now '{Value(WaitFor(window, "PageNameText", TimeSpan.FromSeconds(5)))}'");

            if (Environment.GetEnvironmentVariable("GUIPROBE_DUMP") == "1")
            {
                Dump(window);
            }

            var editor = WaitFor(window, "Editor", TimeSpan.FromSeconds(10));


            var badge = WaitFor(window, "IssueBadge", TimeSpan.FromSeconds(10));
            var countText = WaitFor(window, "IssueCountText", TimeSpan.FromSeconds(10));

            // ── Paste ────────────────────────────────────────────────────────
            SetClipboard(sample);
            Console.WriteLine($"[paste] clipboard holds {ReadClipboard().Length} chars");
            var wordCount = WaitFor(window, "WordCountText", TimeSpan.FromSeconds(10));

            var paste = Stopwatch.StartNew();
            var pasted = false;
            for (var attempt = 0; attempt < 6 && !pasted; attempt++)
            {
                process.Refresh();
                ForceForeground(process.MainWindowHandle);
                try { window.SetFocus(); } catch (InvalidOperationException) { /* not focusable yet */ }
                Thread.Sleep(300);
                try { editor.SetFocus(); } catch (InvalidOperationException) { /* retry */ }
                ClickCentre(editor);
                Thread.Sleep(250);

                paste.Restart();
                SendControlKey('a');
                SendControlKey('v');

                // The word counter is the page's own report that text arrived. It is used
                // rather than the document's own text because it is the number the user reads.
                var landed = Stopwatch.StartNew();
                while (landed.Elapsed < TimeSpan.FromSeconds(3))
                {
                    if (WordsIn(Value(wordCount)) > 100) { pasted = true; break; }
                    Thread.Sleep(50);
                }

                if (!pasted)
                {
                    Console.WriteLine(
                        $"[paste] attempt {attempt + 1}: words='{Value(wordCount)}' "
                        + $"docTextLen={ReadEditorText(editor).Length} "
                        + $"foreground={(GetForegroundWindow() == process.MainWindowHandle ? "app" : "other")}");
                }
            }

            if (!pasted)
            {
                Console.WriteLine("[paste] FAIL: the sample never reached the editor");
                return 3;
            }

            Console.WriteLine($"[paste] {Value(wordCount)} in the document");

            var firstIssueMs = WaitForIssues(badge, minimum: 1, TimeSpan.FromSeconds(120), paste);
            Console.WriteLine($"[paste] first issue visible after {Fmt(firstIssueMs)}");

            var settled = WaitForSettled(badge, TimeSpan.FromSeconds(90), paste);
            Console.WriteLine($"[paste] issue count settled at {settled.Count} after {Fmt(settled.Ms)}");
            Console.WriteLine($"[paste] sidebar says: {Value(countText)}");

            // Responsiveness while the deep lane is still working: a UIA property read
            // that has to cross to the WriteLite dispatcher tells us whether it is pumping.
            var probe = Stopwatch.StartNew();
            _ = Value(countText);
            probe.Stop();
            Console.WriteLine($"[paste] UI responded to a property read in {probe.Elapsed.TotalMilliseconds:F1} ms");

            // ── One correction ───────────────────────────────────────────────
            if (Environment.GetEnvironmentVariable("GUIPROBE_DUMP") == "1")
            {
                Dump(window);
            }

            EnsurePanelOpen(window);
            Screenshot(window, "after-paste");
            Console.WriteLine(
                $"[probe] emptyState={(FindById(window, "EmptyTitle") is null ? "absent" : "PRESENT")} "
                + $"list={(FindById(window, "IssuesList") is null ? "absent" : "present")} "
                + $"analyzing={(FindById(window, "AnalyzingState") is null ? "absent" : "PRESENT")} "
                + $"safeEnabled={FindById(window, "ApplySafeButton")?.Current.IsEnabled}");

            var before = ReadEditorText(editor);
            var beforeCount = ReadInt(badge);
            var applyButton = FindFirstApplyButton(window);
            if (applyButton is null)
            {
                Console.WriteLine("[apply] FAIL: no «Исправить» button found on any card");
                if (FindById(window, "IssuesList") is { } list)
                {
                    var kids = list.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                    Console.WriteLine($"[apply] IssuesList exposes {kids.Count} descendants");
                    foreach (AutomationElement kid in kids)
                    {
                        try
                        {
                            Console.WriteLine(
                                $"[apply]   {kid.Current.ControlType.ProgrammaticName} "
                                + $"id='{kid.Current.AutomationId}' name='{kid.Current.Name}'");
                        }
                        catch (ElementNotAvailableException) { /* moved on */ }
                    }
                }
                else
                {
                    Console.WriteLine("[apply] IssuesList is not in the tree");
                }
            }
            else
            {
                var click = Stopwatch.StartNew();
                Invoke(applyButton);
                var changedMs = WaitForChange(editor, before, TimeSpan.FromSeconds(60), click);
                var countDropMs = WaitForCountBelow(badge, beforeCount, TimeSpan.FromSeconds(60), click);
                Console.WriteLine($"[apply] text changed after {Fmt(changedMs)}");
                Console.WriteLine($"[apply] issue left the sidebar after {Fmt(countDropMs)} ({beforeCount} -> {ReadInt(badge)})");
            }

            // ── Fix everything safe ──────────────────────────────────────────
            Thread.Sleep(1500);
            var safe = FindById(window, "ApplySafeButton");
            if (safe is null || !safe.Current.IsEnabled)
            {
                Console.WriteLine("[safe-batch] skipped: button unavailable or disabled");
            }
            else
            {
                var beforeBatch = ReadEditorText(editor);
                var beforeBatchCount = ReadInt(badge);
                var batch = Stopwatch.StartNew();
                Invoke(safe);
                var batchMs = WaitForChange(editor, beforeBatch, TimeSpan.FromSeconds(60), batch);
                Console.WriteLine($"[safe-batch] text changed after {Fmt(batchMs)} ({beforeBatchCount} -> {ReadInt(badge)} issues)");
            }

            // ── Duplicate check ──────────────────────────────────────────────
            Thread.Sleep(6000);
            Console.WriteLine($"[after 6 s] issues={ReadInt(badge)} · {Value(countText)}");
            Thread.Sleep(20000);
            Console.WriteLine($"[after 26 s] issues={ReadInt(badge)} · {Value(countText)}");

            Screenshot(window, "after-safe-batch");
            Console.WriteLine("[final] editor text head: " + Head(ReadEditorText(editor), 220));

            var finalPath = Path.Combine(RepoRoot(), "artifacts", "guiprobe-final-text.txt");
            File.WriteAllText(finalPath, ReadEditorText(editor));
            Console.WriteLine($"[final] full text written to {finalPath}");
            return 0;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* gone */ }
        }
    }

    // ── Waiting on the tree ──────────────────────────────────────────────────

    private static readonly Stopwatch Boot = Stopwatch.StartNew();
    private static long Elapsed() => Boot.ElapsedMilliseconds;
    private static string Fmt(long ms) => ms < 0 ? "TIMEOUT" : $"{ms} ms";

    private static long WaitForIssues(AutomationElement badge, int minimum, TimeSpan timeout, Stopwatch since)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (ReadInt(badge) >= minimum) return since.ElapsedMilliseconds;
            Thread.Sleep(25);
        }

        return -1;
    }

    /// <summary>Waits until the count stops moving for a second — "the answer stopped growing".</summary>
    private static (int Count, long Ms) WaitForSettled(AutomationElement badge, TimeSpan timeout, Stopwatch since)
    {
        var deadline = Stopwatch.StartNew();
        var last = ReadInt(badge);
        var lastChange = Stopwatch.StartNew();
        var lastChangeMs = since.ElapsedMilliseconds;

        while (deadline.Elapsed < timeout)
        {
            var now = ReadInt(badge);
            if (now != last)
            {
                last = now;
                lastChange.Restart();
                lastChangeMs = since.ElapsedMilliseconds;
            }
            else if (lastChange.Elapsed > TimeSpan.FromSeconds(4) && last > 0)
            {
                return (last, lastChangeMs);
            }

            Thread.Sleep(50);
        }

        return (last, lastChangeMs);
    }

    private static long WaitForChange(AutomationElement editor, string before, TimeSpan timeout, Stopwatch since)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (!string.Equals(ReadEditorText(editor), before, StringComparison.Ordinal))
            {
                return since.ElapsedMilliseconds;
            }

            Thread.Sleep(10);
        }

        return -1;
    }

    private static long WaitForCountBelow(AutomationElement badge, int before, TimeSpan timeout, Stopwatch since)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (ReadInt(badge) < before) return since.ElapsedMilliseconds;
            Thread.Sleep(10);
        }

        return -1;
    }

    // ── Tree access ──────────────────────────────────────────────────────────

    private static AutomationElement WaitForWindow(Process process)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                try { return AutomationElement.FromHandle(process.MainWindowHandle); }
                catch (COMException) { /* provider not ready */ }
                catch (ElementNotAvailableException) { /* window recreation */ }
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException("WriteLite did not show a main window.");
    }

    /// <summary>Answers a modal dialog by button name, if one is up. Harmless when none is.</summary>
    private static void DismissModal(Process process, string buttonName)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                process.Refresh();
                var root = AutomationElement.FromHandle(process.MainWindowHandle);
                var button = root.FindFirst(
                    TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new PropertyCondition(AutomationElement.NameProperty, buttonName)));
                if (button is not null)
                {
                    Console.WriteLine($"[startup] dismissed a modal with «{buttonName}»");
                    Invoke(button);
                    Thread.Sleep(800);
                    return;
                }
            }
            catch (COMException) { /* transient */ }
            catch (ElementNotAvailableException) { /* transient */ }

            Thread.Sleep(400);
        }
    }

    /// <summary>
    /// Makes sure the suggestions panel is on screen.
    /// </summary>
    /// <remarks>
    /// Below 1080 px of page width the panel becomes a toggle and leaves the automation tree
    /// entirely, which is why the first runs of this probe reported "no «Исправить» button"
    /// while the editor was quite correctly underlining seventeen findings. The toggle is
    /// pressed after the paste rather than before, because a size change resets the page's
    /// forced-open flag and a click made too early is undone by the next layout pass.
    /// </remarks>
    private static void EnsurePanelOpen(AutomationElement window)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (FindById(window, "IssuesList") is not null) return;

            var toggle = FindById(window, "TogglePanelButton");
            if (toggle is null) return;

            Console.WriteLine("[panel] suggestions panel was collapsed; opening it");
            Invoke(toggle);
            Thread.Sleep(900);
        }
    }

    /// <summary>Saves what the window looks like, so a claim about the UI can be checked.</summary>
    private static void Screenshot(AutomationElement window, string label)
    {
        try
        {
            var bounds = window.Current.BoundingRectangle;
            if (bounds.IsEmpty) return;

            using var bitmap = new System.Drawing.Bitmap((int)bounds.Width, (int)bounds.Height);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    (int)bounds.Left, (int)bounds.Top, 0, 0, bitmap.Size);
            }

            var path = Path.Combine(RepoRoot(), "artifacts", $"guiprobe-{label}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"[shot] {path}");
        }
        catch (Exception error)
        {
            Console.WriteLine($"[shot] failed: {error.GetType().Name}");
        }
    }

    /// <summary>Lists what the tree actually exposes, for when an expected id is missing.</summary>
    private static void Dump(AutomationElement root)
    {
        try
        {
            var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            Console.WriteLine($"[dump] {all.Count} elements");
            foreach (AutomationElement element in all)
            {
                try
                {
                    var id = element.Current.AutomationId;
                    if (string.IsNullOrEmpty(id)) continue;
                    Console.WriteLine($"[dump]   {id} ({element.Current.ControlType.ProgrammaticName}) name='{element.Current.Name}'");
                }
                catch (ElementNotAvailableException) { /* moved on */ }
            }
        }
        catch (COMException) { Console.WriteLine("[dump] unavailable"); }
    }

    private static AutomationElement? FindById(AutomationElement root, string id)
    {
        try
        {
            return root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        }
        catch (COMException) { return null; }
        catch (ElementNotAvailableException) { return null; }
    }

    private static AutomationElement WaitFor(AutomationElement root, string id, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (FindById(root, id) is { } found) return found;
            Thread.Sleep(100);
        }

        throw new InvalidOperationException($"Control '{id}' never appeared.");
    }

    /// <summary>
    /// The «Исправить» button on the first correction card.
    /// </summary>
    /// <remarks>
    /// Searched from the window rather than from the list: the list is collapsed whenever the
    /// count is zero and is therefore absent from the tree, and scoping the search to it made
    /// "no cards yet" indistinguishable from "the button is missing". The name is matched
    /// exactly so that the panel-wide «Исправить все безопасные замечания» is never mistaken
    /// for a single card's button.
    /// </remarks>
    private static AutomationElement? FindFirstApplyButton(AutomationElement root)
    {
        try
        {
            var buttons = root.FindAll(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "Исправить")));
            foreach (AutomationElement button in buttons)
            {
                if (button.Current.IsEnabled) return button;
            }
        }
        catch (COMException) { /* transient */ }
        catch (ElementNotAvailableException) { /* transient */ }

        return null;
    }

    private static void Invoke(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
        {
            ((InvokePattern)pattern).Invoke();
        }
    }

    /// <summary>
    /// Selects a rail item. The rail is radio buttons, so selection — not toggling — is what
    /// raises the Checked event the shell listens to.
    /// </summary>
    private static void Toggle(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
        {
            ((SelectionItemPattern)selection).Select();
            return;
        }

        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
        {
            ((TogglePattern)toggle).Toggle();
            return;
        }

        Invoke(element);
    }

    private static string Value(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
            {
                return ((ValuePattern)value).Current.Value ?? string.Empty;
            }

            return element.Current.Name ?? string.Empty;
        }
        catch (COMException) { return string.Empty; }
        catch (ElementNotAvailableException) { return string.Empty; }
    }

    /// <summary>Parses the leading number out of a counter label such as "480 СЛОВ".</summary>
    private static int WordsIn(string label)
    {
        var digits = new string(label.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : 0;
    }

    private static int ReadInt(AutomationElement element)
        => int.TryParse(Value(element).Trim(), out var parsed) ? parsed : 0;

    private static string ReadEditorText(AutomationElement editor)
    {
        try
        {
            if (editor.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
            {
                return ((TextPattern)pattern).DocumentRange.GetText(-1);
            }
        }
        catch (COMException) { /* transient */ }
        catch (ElementNotAvailableException) { /* transient */ }

        return string.Empty;
    }

    private static string ReadClipboard()
    {
        var result = string.Empty;
        var thread = new Thread(() =>
        {
            try { result = Clipboard.GetText() ?? string.Empty; }
            catch (ExternalException) { result = string.Empty; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    /// <summary>
    /// Puts text on the clipboard so that it survives this process.
    /// </summary>
    /// <remarks>
    /// <c>SetDataObject(text, copy: true)</c> rather than <c>SetText</c>: the copy flag is what
    /// asks the clipboard to take ownership of the data. Without it the payload belongs to the
    /// STA thread that placed it, and that thread is joined and gone before WriteLite is ever
    /// asked to paste — which is exactly the empty paste the first run of this probe measured.
    /// </remarks>
    private static void SetClipboard(string text)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, copy: true);
                    failure = null;
                    return;
                }
                catch (ExternalException error)
                {
                    // Another process can hold the clipboard open; retry rather than fail.
                    failure = error;
                    Thread.Sleep(120);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw failure;
    }

    private static string Head(string text, int length)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ");
        return flat.Length <= length ? flat : flat[..length] + "…";
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WriteLite.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }
}
