using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using CardProbe;

/// <summary>
/// Live check of the indicator path in another process's field: the panel the count badge opens
/// must keep the field's focus and fix several findings in a row, and the pen's «Перевести» must
/// replace the text in place. Types only into WriteLite.TestHost, which this probe starts itself.
/// </summary>
internal static class Program
{
    private static string Sentence = "Сечас все хорошо. Мы долго обсуждали этот вопрос на встрече. Програма роботает стабильно";
    private static readonly List<string> Failures = [];
    private static Process? _host;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var custom = args.FirstOrDefault(a => a.StartsWith("--text=", StringComparison.Ordinal));
        if (custom is not null) Sentence = custom["--text=".Length..];
        var writeLite = Process.GetProcessesByName("WriteLite").FirstOrDefault();
        if (writeLite is null) { Console.WriteLine("WriteLite is not running."); return 2; }

        var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "WriteLite.TestHost", "bin", "Release", "net10.0-windows", "WriteLite.TestHost.exe"));
        if (!File.Exists(exe)) { Console.WriteLine($"missing {exe}"); return 2; }
        var host = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })!;
        _host = host;
        try
        {
            host.WaitForInputIdle(5000);
            Thread.Sleep(1500);
            host.Refresh();
            var window = AutomationElement.FromHandle(host.MainWindowHandle);
            var field = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "WpfInput"));
            if (field is null) { Console.WriteLine("no field"); return 2; }
            ((ValuePattern)field.GetCurrentPattern(ValuePattern.Pattern)).SetValue(string.Empty);

            if (!Guard.Raise(host, TimeSpan.FromSeconds(8))) { Console.WriteLine($"cannot raise: {Guard.DescribeForeground()}"); return 3; }
            var b = field.Current.BoundingRectangle;
            var (cx, cy) = ((int)(b.Left + b.Width / 2), (int)(b.Top + 14));
            if (!Guard.OwnsPoint(host, cx, cy)) { Console.WriteLine("field covered"); return 3; }
            Input.Click(cx, cy);
            Guard.Lower(host);
            Thread.Sleep(400);
            if (!Guard.OwnsForeground(host)) { Console.WriteLine($"focus lost: {Guard.DescribeForeground()}"); return 3; }
            if (args.Contains("--predict"))
            {
                // Teach the phrase (the personal model learns finished sentences), then type a prefix.
                foreach (var taught in new[] { "Завтра проверим документацию. ", "Документацию нужно обновить к пятнице. ", "Коллеги прочитали документацию вчера. " })
                {
                    if (!Guard.OwnsForeground(host)) { Console.WriteLine("focus lost"); return 3; }
                    Input.Type(taught);
                    Thread.Sleep(1800);
                }

                if (!Guard.OwnsForeground(host)) { Console.WriteLine("focus lost"); return 3; }
                Input.Type("Мы снова открыли докум");
                var chip = WaitFor(() => Find(writeLite.Id, name: "Tab", type: ControlType.Text), TimeSpan.FromSeconds(8));
                Console.WriteLine(chip is null ? "  x no prediction chip" : "  chip shown");
                Shot("4-chip");
                if (chip is null) { Fail("no prediction chip"); return Report(); }
                if (!Guard.OwnsForeground(host)) { Console.WriteLine("focus lost"); return 3; }
                var beforeTab = ReadText(field);
                Input.PressVirtualKey(0x09);
                var after = WaitFor(() => ReadText(field) != beforeTab ? ReadText(field) : null, TimeSpan.FromSeconds(3));
                Thread.Sleep(600);
                after = ReadText(field);
                Console.WriteLine($"  after Tab: «{after?[^40..]}»");
                if (after is null || !after.EndsWith("документацию", StringComparison.Ordinal)) Fail("Tab did not complete the word");
                return Report();
            }

            Input.Type(Sentence);
            Console.WriteLine($"typed: {Sentence}");

            if (!args.Contains("--skip-panel"))
            {
                // 1. The count badge opens the panel without taking focus.
                var badge = WaitFor(() => Find(writeLite.Id, automationId: "CountButton"), TimeSpan.FromSeconds(20));
                if (badge is null) { Fail("indicator badge never appeared"); return Report(); }
                Thread.Sleep(3500); // let the deep lane settle so all three findings are in
                ClickElement(badge, writeLite.Id, host);
                var panel = WaitFor(() => FindPanel(writeLite.Id), TimeSpan.FromSeconds(5));
                if (panel is null) { Fail("panel did not open"); return Report(); }
                Thread.Sleep(500);
                Console.WriteLine($"panel open; foreground={Guard.DescribeForeground()}");
                if (args.Contains("--dump"))
                {
                    Thread.Sleep(args.Contains("--long") ? 9000 : 2500);
                    panel = FindPanel(writeLite.Id) ?? panel;
                    foreach (AutomationElement t in panel.FindAll(TreeScope.Descendants,
                                 new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
                    {
                        var name = t.Current.Name;
                        if (!string.IsNullOrWhiteSpace(name)) Console.WriteLine($"  | {name}");
                    }

                    return Report();
                }
                Shot("1-panel");
                if (!Guard.OwnsForeground(host)) Fail($"panel took the focus: {Guard.DescribeForeground()}");

                // 2. Fix every row in a row.
                var fixes = 0;
                for (var i = 0; i < 5; i++)
                {
                    panel = FindPanel(writeLite.Id);
                    if (panel is null) break;
                    // The topmost enabled «Исправить» that is actually on screen inside the list.
                    var scroller = panel.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "IssuesScroller"));
                    var visible = scroller?.Current.BoundingRectangle ?? panel.Current.BoundingRectangle;
                    var fix = panel.FindAll(TreeScope.Descendants, new AndCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                            new PropertyCondition(AutomationElement.NameProperty, "Исправить"),
                            new PropertyCondition(AutomationElement.IsEnabledProperty, true)))
                        .Cast<AutomationElement>()
                        .Where(e => !e.Current.BoundingRectangle.IsEmpty)
                        .Where(e =>
                        {
                            var r = e.Current.BoundingRectangle;
                            return r.Top >= visible.Top && r.Bottom <= visible.Bottom;
                        })
                        .OrderBy(e => e.Current.BoundingRectangle.Top)
                        .FirstOrDefault();
                    if (fix is null) break;
                    Console.WriteLine($"  visible fix button at {fix.Current.BoundingRectangle}");
                    var before = ReadText(field);
                    var sw = Stopwatch.StartNew();
                    ClickElement(fix, writeLite.Id, host);
                    var changed = WaitFor(() => ReadText(field) != before ? "y" : null, TimeSpan.FromSeconds(8));
                    Console.WriteLine($"fix {i + 1}: {(changed is null ? "NO CHANGE" : $"{sw.ElapsedMilliseconds} ms")} -> «{ReadText(field)}»");
                    if (changed is null) { Fail($"fix {i + 1} did not change the text"); break; }
                    fixes++;
                    Thread.Sleep(300);
                }

                if (fixes < 3) Fail($"only {fixes} fixes applied from the panel");
                if (!Guard.OwnsForeground(host)) Fail($"focus lost after fixes: {Guard.DescribeForeground()}");
                Thread.Sleep(1200);
            }

            // 3. Pen -> Перевести -> Заменить.
            var beforeTranslate = ReadText(field);
            var pen = WaitFor(() => Find(writeLite.Id, automationId: "PenButton"), TimeSpan.FromSeconds(10));
            if (pen is null) { Fail("pen button not found"); return Report(); }
            ClickElement(pen, writeLite.Id, host);
            Thread.Sleep(400);
            Shot("2-pen-menu");
            var item = WaitFor(() => Find(writeLite.Id, name: "Перевести", type: ControlType.MenuItem), TimeSpan.FromSeconds(3));
            if (item is null) { Fail("pen menu has no «Перевести»"); return Report(); }
            ClickElement(item, writeLite.Id, host);
            var sw2 = Stopwatch.StartNew();
            var replace = WaitFor(() =>
            {
                var r = Find(writeLite.Id, automationId: "WriteLiteTextActionReplace");
                return r is not null && r.Current.IsEnabled ? r : null;
            }, TimeSpan.FromSeconds(30));
            if (replace is null) { Fail("translation never became ready"); return Report(); }
            Shot("3-translate");
            Console.WriteLine($"translate card ready in {sw2.ElapsedMilliseconds} ms; foreground={Guard.DescribeForeground()}");
            ClickElement(replace, writeLite.Id, host);
            var translated = WaitFor(() => ReadText(field) != beforeTranslate ? ReadText(field) : null, TimeSpan.FromSeconds(8));
            Console.WriteLine($"translated: «{translated}»");
            if (translated is null || translated.Any(c => c is >= 'а' and <= 'я')) Fail("text was not replaced with English");
            return Report();
        }
        finally
        {
            try { host.Kill(); } catch { }
        }
    }

    private static void Shot(string name)
    {
        try
        {
            var dir = Environment.GetEnvironmentVariable("PROBE_SHOTS");
            if (string.IsNullOrEmpty(dir)) return;
            // Only the probe's own window and WriteLite's overlays around it — never the rest of the desktop.
            var hostRect = AutomationElement.FromHandle(_host!.MainWindowHandle).Current.BoundingRectangle;
            var bounds = new System.Drawing.Rectangle((int)hostRect.Left, (int)hostRect.Top,
                (int)hostRect.Width + 40, (int)hostRect.Height + 40);
            using var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
            bmp.Save(Path.Combine(dir, name + ".png"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (screenshot failed: {ex.Message})");
        }
    }

    private static int Report()
    {
        Console.WriteLine(Failures.Count == 0 ? "PASS" : "FAIL:\n  " + string.Join("\n  ", Failures));
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void Fail(string message)
    {
        Failures.Add(message);
        Console.WriteLine("  x " + message);
    }

    private static string ReadText(AutomationElement field) =>
        ((ValuePattern)field.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

    private static void ClickElement(AutomationElement element, int writeLitePid, Process host)
    {
        var r = element.Current.BoundingRectangle;
        var (x, y) = ((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        if (!Guard.OwnsPoint(writeLitePid, x, y) && !Guard.OwnsPoint(host, x, y))
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            {
                Console.WriteLine($"  ({x},{y}) is covered or scrolled away; invoking instead");
                ((InvokePattern)invoke).Invoke();
                return;
            }

            Fail($"refused to click ({x},{y}) - not WriteLite or the host: {Guard.DescribeForeground()}");
            return;
        }

        Input.Click(x, y);
    }

    private static T? WaitFor<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (probe() is { } v) return v;
            }
            catch
            {
                // UIA races with WPF re-rendering; try again.
            }

            Thread.Sleep(150);
        }

        return null;
    }

    private static AutomationElement? FindPanel(int pid)
    {
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children,
                     new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
        {
            if (w.Current.BoundingRectangle.IsEmpty) continue;
            var marker = w.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "Исправить все безопасные"));
            if (marker is not null) return w;
        }

        return null;
    }

    private static AutomationElement? Find(int pid, string? name = null, ControlType? type = null, string? automationId = null)
    {
        var conditions = new List<Condition>();
        if (name is not null) conditions.Add(new PropertyCondition(AutomationElement.NameProperty, name));
        if (type is not null) conditions.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, type));
        if (automationId is not null) conditions.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        var condition = conditions.Count == 1 ? conditions[0] : new AndCondition([.. conditions]);
        foreach (AutomationElement root in AutomationElement.RootElement.FindAll(TreeScope.Children,
                     new PropertyCondition(AutomationElement.ProcessIdProperty, pid)))
        {
            if (root.Current.BoundingRectangle.IsEmpty) continue;
            var hit = root.FindFirst(TreeScope.Descendants, condition);
            if (hit is not null && !hit.Current.BoundingRectangle.IsEmpty) return hit;
        }

        return null;
    }
}
