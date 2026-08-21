using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace FieldMatrix;

/// <summary>
/// Drives the shipping WriteLite build against a real external field, the way a person does.
/// </summary>
/// <remarks>
/// <para>§23: unit tests cannot show that the popup appears next to the right word, that the
/// card says what the write will do, or that pressing the button changes the other
/// application's text. This clicks the underline with a real mouse event, reads the card
/// through the accessibility tree, presses the button, and then reads the target application
/// back — no internal hooks into WriteLite at any point.</para>
///
/// <para>Screenshots are taken at each step for §25.</para>
/// </remarks>
internal static class EndToEnd
{
    private const string Sentence = Targets.Sentence;
    private const string Marker = "роботает";
    private const string Fixed = "работает";

    public static int Run(string[] args)
    {
        var shotDirectory = Program.Arg(args, "--shots")
                            ?? Path.Combine(Path.GetTempPath(), "writelite-e2e");
        Directory.CreateDirectory(shotDirectory);

        if (Process.GetProcessesByName("WriteLite").Length == 0)
        {
            Console.WriteLine("FAIL  WriteLite is not running");
            return 2;
        }

        Console.WriteLine("▶ opening Notepad and typing the sample sentence");
        Driver.Launch(@"C:\Windows\System32\notepad.exe", waitMs: 8000);
        Thread.Sleep(2500);

        var window = Driver.MainWindowOf("Notepad");
        if (window is null)
        {
            Console.WriteLine("FAIL  Notepad window not found");
            return 2;
        }

        var field = Driver.FirstTextField(window);
        if (field is null || !Driver.Focus(field))
        {
            Console.WriteLine("FAIL  Notepad edit control not reachable");
            return 2;
        }

        // A real click in the text area. SetFocus alone leaves whatever overlay service last
        // claimed the desktop still reported as the focused element, and WriteLite tracks the
        // field through that same report.
        var area = field.Current.BoundingRectangle;
        Click((int)(area.Left + area.Width / 2), (int)(area.Top + Math.Min(60, area.Height / 2)));
        Thread.Sleep(600);
        Console.WriteLine($"  focus after click: {FocusedDescription()}");

        Driver.TypeIntoFocused(Sentence);

        // WriteLite shows nothing until it has seen the user edit a field it is already
        // tracking — focus alone is not an editing session. Typing the whole sentence before
        // it acquires the target leaves it with a baseline and nothing to report, so give it
        // one more keystroke once it has settled.
        Thread.Sleep(2500);
        Driver.TypeIntoFocused(" ", clearFirst: false);
        Thread.Sleep(600);

        var before = ReadField(field);
        Console.WriteLine($"  typed  : «{Shorten(before)}»");
        if (!before.Contains(Marker, StringComparison.Ordinal))
        {
            Console.WriteLine("FAIL  the sample did not land in Notepad");
            return 2;
        }

        Console.WriteLine("▶ waiting for WriteLite to analyse");

        // The desktop is not quiet: overlay services take the foreground unpredictably, and
        // WriteLite correctly follows the foreground. Keep nudging the field until WriteLite
        // is actually showing something for it, rather than assuming one keystroke was seen.
        var analysed = false;
        for (var attempt = 0; attempt < 12 && !analysed; attempt++)
        {
            Thread.Sleep(1200);
            var windows = CountWriteLiteWindows();
            Console.WriteLine($"    attempt {attempt + 1}: focus={FocusedDescription()} writeLiteWindows={windows}");
            if (windows > 0)
            {
                analysed = true;
                break;
            }

            Driver.Focus(field);
            Driver.TypeIntoFocused(" ", clearFirst: false);
        }

        if (!analysed)
        {
            Console.WriteLine("FAIL  WriteLite never showed anything for this field "
                              + "(the desktop kept taking the foreground)");
            Shot(shotDirectory, "01-analysed");
            return 3;
        }

        Thread.Sleep(1200);
        Shot(shotDirectory, "01-analysed");

        if (!TryGetWordRectangle(field, before, Marker, out var rectangle))
        {
            Console.WriteLine("FAIL  could not locate the word on screen");
            return 2;
        }

        Console.WriteLine($"  «{Marker}» at {rectangle}");
        Console.WriteLine("▶ clicking the underlined word");
        // Another application may have come forward while WriteLite was analysing; the click
        // has to land on the field, not on whatever is on top of it.
        Driver.Focus(field);
        Thread.Sleep(500);

        // The overlay claims hit-testing only over the underline itself, which is a few
        // pixels at the bottom of the word — a click in the middle of the glyphs goes
        // straight through to the host application. Walk down through the word.
        var x = (int)(rectangle.Left + rectangle.Width / 2);
        AutomationElement? popup = null;
        foreach (var offset in (int[])[1, 0, 2, 3, -1])
        {
            var y = (int)(rectangle.Bottom) + offset;
            Console.WriteLine($"  click at ({x}, {y})");
            Click(x, y);
            Thread.Sleep(1400);
            popup = FindWriteLiteWindow();
            if (popup is not null && ReadCard(popup).Any(t => t.Contains(Marker, StringComparison.Ordinal)))
            {
                break;
            }

            popup = null;
        }

        Shot(shotDirectory, "02-popup");

        if (popup is null)
        {
            Console.WriteLine("FAIL  no WriteLite popup appeared");
            return 1;
        }

        var card = ReadCard(popup);
        Console.WriteLine($"  card   : {string.Join(" | ", card)}");

        var original = card.FirstOrDefault(t => t.Contains(Marker, StringComparison.Ordinal));
        var replacement = card.FirstOrDefault(t =>
            t.Contains(Fixed, StringComparison.Ordinal) && !t.Contains(Marker, StringComparison.Ordinal));

        Console.WriteLine($"  shows original    : {(original is null ? "FAIL" : $"PASS «{original}»")}");
        Console.WriteLine($"  shows replacement : {(replacement is null ? "FAIL" : $"PASS «{replacement}»")}");
        Console.WriteLine($"  no split glyph    : "
                          + (card.Any(t => t.Contains('·')) ? "FAIL (middle dot on the card)" : "PASS"));

        var button = FindApplyButton(popup, Fixed);
        if (button is null)
        {
            Console.WriteLine("FAIL  no apply button on the card");
            return 1;
        }

        Console.WriteLine($"▶ pressing «{button.Current.Name}»");
        var stopwatch = Stopwatch.StartNew();
        if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObject)
            && invokeObject is InvokePattern invoke)
        {
            invoke.Invoke();
        }
        else
        {
            var box = button.Current.BoundingRectangle;
            Click((int)(box.Left + box.Width / 2), (int)(box.Top + box.Height / 2));
        }

        string after = before;
        for (var i = 0; i < 30; i++)
        {
            Thread.Sleep(120);
            after = ReadField(field);
            if (!string.Equals(after, before, StringComparison.Ordinal)) break;
        }

        stopwatch.Stop();
        Thread.Sleep(900);
        Shot(shotDirectory, "03-applied");

        var expected = before.Replace(Marker, Fixed, StringComparison.Ordinal);
        var correct = string.Equals(after, expected, StringComparison.Ordinal);
        Console.WriteLine($"  after  : «{Shorten(after)}»");
        Console.WriteLine($"  applied: {(correct ? "PASS" : "FAIL")} in {stopwatch.ElapsedMilliseconds} ms");
        Console.WriteLine($"  only the intended range changed: {(correct ? "PASS" : "FAIL")}");
        Console.WriteLine($"  screenshots in {shotDirectory}");

        return correct ? 0 : 1;
    }

    private static string FocusedDescription()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            var process = Process.GetProcessById(focused.Current.ProcessId).ProcessName;
            return $"{process}/{focused.Current.LocalizedControlType}";
        }
        catch (Exception exception)
        {
            return exception.GetType().Name;
        }
    }

    private static int CountWriteLiteWindows()
    {
        var count = 0;
        foreach (var process in Process.GetProcessesByName("WriteLite"))
        {
            try
            {
                var windows = AutomationElement.RootElement.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
                foreach (AutomationElement window in windows)
                {
                    if (!window.Current.IsOffscreen) count++;
                }
            }
            catch (Exception exception) when (exception is ElementNotAvailableException
                                                  or InvalidOperationException)
            {
                // transient
            }
        }

        return count;
    }

    private static string ReadField(AutomationElement element)
    {
        try
        {
            var adapter = new WriteLite.Services.CompositeTextTargetAdapter().Select(element);
            if (adapter is null) return string.Empty;
            var read = adapter.ReadTextAsync(element).GetAwaiter().GetResult();
            return read.Succeeded ? read.Text : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Where a word sits on screen, asked of the provider that renders it.</summary>
    private static bool TryGetWordRectangle(
        AutomationElement element, string text, string word, out System.Windows.Rect rectangle)
    {
        rectangle = default;
        var index = text.IndexOf(word, StringComparison.Ordinal);
        if (index < 0) return false;

        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject)
            || patternObject is not TextPattern textPattern)
        {
            return false;
        }

        var range = textPattern.DocumentRange.Clone();
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, index + word.Length);
        range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, index);

        var rectangles = range.GetBoundingRectangles();
        if (rectangles.Length == 0) return false;

        rectangle = new System.Windows.Rect(
            rectangles[0].Left, rectangles[0].Top, rectangles[0].Width, rectangles[0].Height);
        return rectangle.Width > 0 && rectangle.Height > 0;
    }

    /// <summary>
    /// The WriteLite window that is actually showing a correction card.
    /// </summary>
    /// <remarks>
    /// WriteLite keeps several top-level windows alive at once — the indicator bubble, the
    /// inline underline overlay, the panel — and most of them carry no text. Picking the
    /// first visible one found an empty overlay and reported the card as blank. The card is
    /// the window with text on it.
    /// </remarks>
    private static AutomationElement? FindWriteLiteWindow()
    {
        AutomationElement? best = null;
        var bestCount = 0;

        foreach (var process in Process.GetProcessesByName("WriteLite"))
        {
            try
            {
                var windows = AutomationElement.RootElement.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
                foreach (AutomationElement candidate in windows)
                {
                    if (candidate.Current.IsOffscreen) continue;
                    if (candidate.Current.BoundingRectangle.Width < 100) continue;

                    var count = ReadCard(candidate).Count;
                    Console.WriteLine($"    window «{candidate.Current.Name}» "
                                      + $"{candidate.Current.BoundingRectangle} texts={count}");
                    if (count <= bestCount) continue;
                    bestCount = count;
                    best = candidate;
                }
            }
            catch (Exception exception) when (exception is ElementNotAvailableException
                                                  or InvalidOperationException)
            {
                // Window came and went while we looked.
            }
        }

        return best;
    }

    private static IReadOnlyList<string> ReadCard(AutomationElement popup)
    {
        var texts = new List<string>();
        try
        {
            var found = popup.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            foreach (AutomationElement item in found)
            {
                var name = item.Current.Name;
                if (!string.IsNullOrWhiteSpace(name)) texts.Add(name.Trim());
            }
        }
        catch (Exception exception) when (exception is ElementNotAvailableException
                                              or InvalidOperationException)
        {
            // Popup closed under us.
        }

        return texts;
    }

    private static AutomationElement? FindApplyButton(AutomationElement popup, string replacement)
    {
        try
        {
            var buttons = popup.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            AutomationElement? fallback = null;
            foreach (AutomationElement button in buttons)
            {
                var name = button.Current.Name ?? string.Empty;
                if (name.Contains(replacement, StringComparison.Ordinal)) return button;
                if (name is not ("×" or "Закрыть" or "Игнорировать" or "В словарь")) fallback ??= button;
            }

            return fallback;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException
                                              or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Shorten(string text)
    {
        var oneLine = text.Replace("\r", " ").Replace("\n", " ");
        return oneLine.Length <= 96 ? oneLine : oneLine[..96] + "…";
    }

    private static void Shot(string directory, string name)
    {
        try
        {
            var bounds = ScreenBounds();
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bitmap.Size);
            var path = Path.Combine(directory, name + ".png");
            bitmap.Save(path, ImageFormat.Png);
            Console.WriteLine($"  shot   : {path}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"  screenshot failed: {exception.GetType().Name}");
        }
    }

    private static Rectangle ScreenBounds()
    {
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78);
        var height = GetSystemMetrics(79);
        return new Rectangle(left, top, width, height);
    }

    private static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(120);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extraInfo);
}
