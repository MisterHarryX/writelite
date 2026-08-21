using System.Diagnostics;
using System.Windows.Automation;

namespace WriteLite.UiaStress;

/// <summary>
/// The seven things that were reported as broken, done to the running application
/// harder and faster than a person could do them.
/// </summary>
internal static class Scenarios
{
    private const int VkF11 = 0x7A;
    private const int VkEscape = 0x1B;

    private static readonly string[] Sections =
    [
        "Главная", "Редактор", "Чтение", "Заметки", "Конвертер",
        "Словарь", "Менеджер слов", "WriteAI", "Настройки", "Диагностика"
    ];

    public static IReadOnlyList<(string Letter, string Name, Action<Ui> Run)> All =>
    [
        ("A", "Navigation torture (120 switches)", NavigationTorture),
        ("B", "Notes: create and toggle repeatedly", NotesToggling),
        ("C", "Reader typography under load", ReaderTypography),
        ("D", "Reader selection and context menu", ReaderActions),
        ("E", "Continuous resize", Resize),
        ("F", "Fullscreen in and out", FullScreen),
        ("G", "AI card with the runtime unavailable", AiCardFailure)
    ];

    // ── A ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Switches section as fast as UI Automation can drive it, 120 times.
    /// </summary>
    /// <remarks>
    /// The shell holds one instance of every page and swaps the host's content, so this
    /// is really a test of what each page does on the way in: reloads that read from
    /// disk, constructors that do real work, subscriptions that are added and never
    /// removed. Anything proportional to the number of visits shows up here as a stall
    /// that grows.
    /// </remarks>
    private static void NavigationTorture(Ui ui)
    {
        var stalls = new List<double>();

        for (var round = 0; round < 12; round++)
        {
            foreach (var section in Sections)
            {
                Ui.Invoke(ui.Require(section));
                stalls.Add(ui.Ping(10_000).TotalMilliseconds);
            }
        }

        var first = stalls.Take(20).Average();
        var last = stalls.TakeLast(20).Average();

        Console.WriteLine(
            $"    {stalls.Count} switches · first 20 avg {first:0.0} ms · last 20 avg {last:0.0} ms · " +
            $"max {stalls.Max():0.0} ms");

        if (last > Math.Max(50, first * 4))
        {
            throw new StressException(
                $"Navigation got {last / Math.Max(first, 0.01):0.0}× slower over the run " +
                $"({first:0.0} ms → {last:0.0} ms): the pages are accumulating work per visit.");
        }
    }

    // ── B ────────────────────────────────────────────────────────────────────

    private static void NotesToggling(Ui ui)
    {
        Ui.Invoke(ui.Require("Заметки"));
        ui.Ping();
        Thread.Sleep(400);

        // Counted before anything is created, so the check at the end is about what this
        // run did rather than about what previous runs left on the board.
        var alreadyThere = CountCheckBoxes(ui);
        Ui.Invoke(ui.Require("Выполненные"));
        Thread.Sleep(300);
        alreadyThere += CountCheckBoxes(ui);
        Ui.Invoke(ui.Require("Все"));
        Thread.Sleep(300);

        // A task to tick. Created through the header menu, the way a person would.
        Ui.Invoke(ui.Require("Создать новую заметку"));
        Thread.Sleep(500);
        Ui.Invoke(ui.Require("Задача"));
        Thread.Sleep(900);

        // The editor opens on the new card; give it a title and save.
        TypeInto(ui, "Название заметки", "Стресс-тест");
        Thread.Sleep(200);
        Ui.Invoke(ui.Require("Сохранить"));
        Thread.Sleep(800);
        ui.Ping();

        var stalls = new List<double>();
        var ticks = 0;

        for (var round = 0; round < 50; round++)
        {
            // A completed task leaves the active board, so ticking it off and putting it
            // back means moving between «Все» and «Выполненные». That alternation is
            // exactly the path that used to leave invisible ghost cards stacked up.
            var tab = round % 2 == 0 ? "Все" : "Выполненные";
            Ui.Invoke(ui.Require(tab));
            Thread.Sleep(120);

            var box = FindCheckBox(ui);
            if (box is null)
            {
                continue;
            }

            // Clicked with the mouse, not toggled through the automation pattern.
            // TogglePattern.Toggle() calls the control's own toggle directly and so
            // proves nothing about whether a click reaches it — which is exactly how a
            // completion box that swallowed its own mouse-up passed every automated
            // check while being impossible to tick by hand.
            var before = ToggleState(box);
            var rect = box.Current.BoundingRectangle;

            if (rect.IsEmpty || rect.Width < 4)
            {
                continue;
            }

            ui.Focus();
            Input.ClickScreen((int)(rect.Left + rect.Width / 2), (int)(rect.Top + rect.Height / 2));
            stalls.Add(ui.Ping(10_000).TotalMilliseconds);
            Thread.Sleep(120);

            if (ToggleState(box) == before)
            {
                var point = new System.Windows.Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
                var hit = AutomationElement.FromPoint(point);
                throw new StressException(
                    $"Clicking the completion box left it {before}. The card's checkbox " +
                    $"does not respond to a mouse click. Hit at {point.X:0},{point.Y:0}: " +
                    $"{hit.Current.ControlType.ProgrammaticName} '{hit.Current.Name}' " +
                    $"class={hit.Current.ClassName}.");
            }

            ticks++;
        }

        if (ticks < 20)
        {
            throw new StressException($"Only {ticks} of 50 toggles found a checkbox to press.");
        }

        Console.WriteLine(
            $"    {ticks} toggles · avg {stalls.Average():0.0} ms · max {stalls.Max():0.0} ms");

        if (stalls.Max() > 1_000)
        {
            throw new StressException($"Ticking a task blocked the window for {stalls.Max():0} ms.");
        }

        // One card, not a pile of ghosts.
        Ui.Invoke(ui.Require("Все"));
        Thread.Sleep(400);
        Ui.Invoke(ui.Require("Выполненные"));
        Thread.Sleep(400);

        var boards = CountCheckBoxes(ui);
        Ui.Invoke(ui.Require("Все"));
        Thread.Sleep(400);
        boards += CountCheckBoxes(ui);

        var added = boards - alreadyThere;
        Console.WriteLine(
            $"    task cards across both tabs: {boards} (was {alreadyThere} before this run, so +{added})");

        // One task was created, so one card is expected. A fade that is still finishing
        // can leave a second on screen for a moment; anything beyond that is the board
        // stacking ghosts, which is the defect this scenario exists to catch.
        if (added > 2)
        {
            throw new StressException(
                $"Creating and toggling one task added {added} cards to the board — " +
                "it is accumulating duplicates.");
        }

        ui.Ping();
    }

    /// <summary>Whether a checkbox is ticked, or null when it has gone.</summary>
    private static ToggleState? ToggleState(AutomationElement box)
    {
        try
        {
            return box.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern)
                ? ((TogglePattern)pattern).Current.ToggleState
                : null;
        }
        catch (ElementNotAvailableException)
        {
            // The card left the board, which is itself proof the tick registered.
            return null;
        }
    }

    private static int CountCheckBoxes(Ui ui)
    {
        try
        {
            return ui.Window.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox)).Count;
        }
        catch (ElementNotAvailableException)
        {
            return 0;
        }
    }

    private static AutomationElement? FindCheckBox(Ui ui)
    {
        try
        {
            return ui.Window.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    // ── C ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hammers the type controls: 16 → 30 → 18 → 28 → 20 → 25, repeatedly.
    /// </summary>
    /// <remarks>
    /// The reported crash. Each press used to re-read the source file and rebuild the
    /// whole rendered document on the dispatcher, with nothing cancelling the previous
    /// attempt, so a burst of presses queued a burst of full re-imports.
    /// </remarks>
    private static void ReaderTypography(Ui ui)
    {
        if (!OpenBook(ui))
        {
            Console.WriteLine("    no reading project available — skipped");
            return;
        }

        var larger = ui.Require("Увеличить шрифт");
        var smaller = ui.Require("Уменьшить шрифт");
        var stalls = new List<double>();

        for (var round = 0; round < 6; round++)
        {
            for (var press = 0; press < 12; press++)
            {
                Ui.Invoke(larger);
                stalls.Add(ui.Ping(10_000).TotalMilliseconds);
            }

            for (var press = 0; press < 12; press++)
            {
                Ui.Invoke(smaller);
                stalls.Add(ui.Ping(10_000).TotalMilliseconds);
            }
        }

        Console.WriteLine(
            $"    {stalls.Count} type changes · avg {stalls.Average():0.0} ms · max {stalls.Max():0.0} ms");

        if (stalls.Max() > 1_000)
        {
            throw new StressException(
                $"A type change blocked the window for {stalls.Max():0} ms. " +
                "Changing a reading preference must not do work proportional to the book.");
        }
    }

    // ── D ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Selects text with the mouse, right-clicks it, and uses the menu.
    /// </summary>
    /// <remarks>
    /// Driven with real input on purpose: the two defects here — the framework's own
    /// context menu winning the first right-click, and the selection being gone by the
    /// time a menu item is clicked — are both consequences of how the gesture reaches
    /// the control, and neither is reproducible by calling the handler.
    /// </remarks>
    private static void ReaderActions(Ui ui)
    {
        if (!OpenBook(ui))
        {
            Console.WriteLine("    no reading project available — skipped");
            return;
        }

        for (var round = 0; round < 10; round++)
        {
            var menu = SelectAndOpenMenu(ui)
                       ?? throw new StressException(
                           $"Right-click {round + 1} produced no context menu over a selection.");

            var headers = MenuHeaders(menu);

            // The legacy menu is the failure: WriteLite's menu never contains «Вставить»,
            // and the framework's editor menu never contains «Выделить».
            if (headers.Contains("Вставить") || headers.Contains("Paste"))
            {
                throw new StressException(
                    $"Right-click {round + 1} showed the framework's Cut/Copy/Paste menu: " +
                    string.Join(", ", headers));
            }

            if (!headers.Contains("Выделить"))
            {
                throw new StressException(
                    $"Right-click {round + 1} did not offer the WriteLite actions: " +
                    string.Join(", ", headers));
            }

            // Use the menu, on the selection it was opened over.
            var bookmark = ui.ByName("Добавить закладку", menu, 1_500);
            if (bookmark is not null)
            {
                Ui.Invoke(bookmark);
            }
            else
            {
                ui.SendKey(VkEscape);
            }

            Thread.Sleep(250);
            ui.Ping();
        }

        Console.WriteLine("    10 select → right-click → act cycles, WriteLite menu every time");
    }

    private static AutomationElement? FindContextMenu(Ui ui)
    {
        var deadline = Stopwatch.StartNew();

        while (deadline.ElapsedMilliseconds < 2_000)
        {
            foreach (var window in ui.Windows())
            {
                try
                {
                    var menu = window.FindFirst(
                        TreeScope.Subtree,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu));

                    if (menu is not null)
                    {
                        return menu;
                    }
                }
                catch (ElementNotAvailableException)
                {
                    // Popup closed while being read.
                }
            }

            Thread.Sleep(80);
        }

        return null;
    }

    private static HashSet<string> MenuHeaders(AutomationElement menu)
    {
        var headers = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var items = menu.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));

            for (var index = 0; index < items.Count; index++)
            {
                headers.Add(items[index].Current.Name);
            }
        }
        catch (ElementNotAvailableException)
        {
            // Nothing to read; the caller reports an empty set as a failure.
        }

        return headers;
    }

    // ── E ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drags the window through every size between cramped and large, continuously.
    /// </summary>
    /// <remarks>
    /// Checked at each size rather than at the end: the complaint was that parts of the
    /// interface disappear *while* resizing, and a layout that repairs itself once the
    /// drag stops is still broken for the whole drag.
    /// </remarks>
    private static void Resize(Ui ui)
    {
        Ui.Invoke(ui.Require("Чтение"));
        ui.Ping();

        var original = ui.Bounds();
        var sizes = new (int W, int H)[]
        {
            (900, 640), (860, 600), (1400, 900), (830, 570), (1200, 800),
            (1000, 620), (1600, 1000), (825, 565), (1240, 780)
        };

        try
        {
            foreach (var (width, height) in sizes)
            {
                for (var step = 0; step < 10; step++)
                {
                    ui.Resize(width + (step * 6), height + (step * 4));
                    Thread.Sleep(35);
                    ui.Ping(8_000);
                }

                // The rail and the status strip are the two things reported as vanishing.
                if (ui.ByName("Главная", timeoutMs: 1_500) is null)
                {
                    throw new StressException($"The navigation rail disappeared at {width}×{height}.");
                }
            }
        }
        finally
        {
            ui.Resize(original.Width == 0 ? 1240 : original.Width, original.Height == 0 ? 780 : original.Height);
            Thread.Sleep(300);
        }

        Console.WriteLine($"    {sizes.Length * 10} resize steps, rail visible throughout");
    }

    // ── F ────────────────────────────────────────────────────────────────────

    private static void FullScreen(Ui ui)
    {
        var before = ui.Bounds();

        for (var round = 0; round < 12; round++)
        {
            ui.SendKey(VkF11);
            Thread.Sleep(500);
            ui.Ping();

            var full = ui.Bounds();
            if (full.Width <= before.Width && round == 0)
            {
                throw new StressException(
                    $"F11 did not enlarge the window ({before.Width}×{before.Height} → {full.Width}×{full.Height}).");
            }

            if (ui.ByName("Главная", timeoutMs: 2_000) is null)
            {
                throw new StressException($"The rail is unreachable in fullscreen (round {round + 1}).");
            }

            ui.SendKey(VkF11);
            Thread.Sleep(500);
            ui.Ping();

            if (ui.ByName("Главная", timeoutMs: 2_000) is null)
            {
                throw new StressException($"The rail is unreachable after leaving fullscreen (round {round + 1}).");
            }
        }

        var after = ui.Bounds();
        Console.WriteLine($"    12 cycles · {before.Width}×{before.Height} → {after.Width}×{after.Height}");

        if (Math.Abs(after.Width - before.Width) > 24 || Math.Abs(after.Height - before.Height) > 24)
        {
            throw new StressException(
                $"The window did not come back to its own size: {before.Width}×{before.Height} " +
                $"became {after.Width}×{after.Height}.");
        }
    }

    // ── G ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks for an AI-drafted card while nothing is serving the local model.
    /// </summary>
    /// <remarks>
    /// The model pack on disk is enough to make the runtime report itself available, so
    /// this is the normal state of a machine where the server is not running — not an
    /// exotic failure. The application must say so and stay usable, which is the whole
    /// of the requirement.
    /// </remarks>
    private static void AiCardFailure(Ui ui)
    {
        // The runtime is taken away deliberately, and before anything else, so that
        // killing it cannot disturb the focus the selection gesture depends on. This is
        // not an exotic condition: the model pack sitting on disk is enough to make the
        // backend report itself available, so "pack present, nothing serving" is the
        // ordinary state of a machine where the server has not started or has died. It
        // is the state in which a card used to be fabricated from the rule fallback and
        // badged as WriteLite AI.
        var stopped = StopLocalModel();
        Console.WriteLine(stopped
            ? "    local model runtime stopped for this scenario"
            : "    no local model runtime was running");

        if (!OpenBook(ui))
        {
            Console.WriteLine("    no reading project available — skipped");
            return;
        }

        // Reached the way the report describes it: select a passage, right-click, pick
        // «Создать карточку с помощью WriteLite AI».
        var menu = SelectAndOpenMenu(ui)
                   ?? throw new StressException("Could not raise the reader's context menu over a selection.");

        var ai = ui.ByName("Создать карточку с помощью WriteLite AI", menu, 2_000);
        if (ai is null)
        {
            ui.SendKey(VkEscape);
            Console.WriteLine("    no local model is bound — the AI action is correctly absent");
        }
        else
        {
            Ui.Invoke(ai);
            Thread.Sleep(1_500);
            Ui.Invoke(ai);

            // The whole requirement, in one loop: the window keeps answering while the
            // model is being waited on, and it does so from another process's point of
            // view — which is the only point of view that can tell.
            var stalls = new List<double>();
            for (var tick = 0; tick < 60; tick++)
            {
                stalls.Add(ui.Ping(5_000).TotalMilliseconds);
                Thread.Sleep(250);
            }

            Console.WriteLine(
                $"    responsive throughout 15 s of AI wait · max stall {stalls.Max():0.0} ms");

            // And it must say what happened rather than going quiet.
            var said = ui.ByName("МОДЕЛЬ НЕ ОТВЕЧАЕТ · НАПИШИТЕ ВРУЧНУЮ", timeoutMs: 60_000)
                       ?? ui.ByName("ЧЕРНОВИК ОТ WRITELITE AI", timeoutMs: 1_000)
                       ?? ui.ByName("ОТВЕТ НЕ РАЗОБРАН · ПОПРОБУЙТЕ СНОВА", timeoutMs: 1_000)
                       ?? ui.ByName("НЕ УДАЛОСЬ СОСТАВИТЬ КАРТОЧКУ", timeoutMs: 1_000)
                       ?? ui.ByName("ЛОКАЛЬНАЯ МОДЕЛЬ НЕ ПОДКЛЮЧЕНА", timeoutMs: 1_000);

            if (said is null)
            {
                throw new StressException(
                    "The card editor never said what became of the AI request. A failure the " +
                    "reader cannot read is a failure twice.");
            }

            Console.WriteLine($"    editor reported: «{said.Current.Name}»");
        }

        // Out of the editor, whatever happened. Nothing is saved.
        var cancel = ui.ByName("Отмена", timeoutMs: 2_000);
        if (cancel is not null)
        {
            Ui.Invoke(cancel);
        }
        else
        {
            ui.SendKey(VkEscape);
        }

        Thread.Sleep(600);
        ui.Ping();

        if (!ui.IsAlive)
        {
            throw new StressException("WriteLite died asking the model for a card.");
        }
    }

    /// <summary>
    /// Drags across a passage and right-clicks it, retrying down the page until a
    /// selection actually takes.
    /// </summary>
    /// <remarks>
    /// A drag at a fixed offset can land in the gap between two paragraphs, where there
    /// is nothing to select. Retrying at different heights is about the driver being
    /// reliable, not about the application being slow — the menu is checked for the
    /// selection-dependent items, so a menu raised over nothing is not accepted.
    /// </remarks>
    private static AutomationElement? SelectAndOpenMenu(Ui ui)
    {
        var box = ui.VisibleBounds(ui.Require("Текст книги"));

        if (box.IsEmpty || box.Width < 120 || box.Height < 120)
        {
            throw new StressException(
                $"The reading canvas has no visible area ({box.Width:0}×{box.Height:0}) — the layout has collapsed.");
        }

        var left = (int)(box.Left + 40);
        var right = (int)(box.Left + Math.Min(box.Width - 40, 340));

        foreach (var fraction in new[] { 0.2, 0.35, 0.5, 0.65, 0.8, 0.28 })
        {
            var y = (int)(box.Top + (box.Height * fraction));
            if (y > box.Bottom - 20 || y < box.Top + 10)
            {
                continue;
            }

            var offset = (int)(box.Height * fraction);

            ui.Focus();
            Input.Drag(left, y, right, y);
            Thread.Sleep(200);

            Input.RightClickScreen((left + right) / 2, y);
            Thread.Sleep(500);

            var menu = FindContextMenu(ui);
            if (menu is null)
            {
                continue;
            }

            // «Создать карточку» is only enabled when something is selected, so this is
            // the menu's own answer to "did the drag take".
            var card = ui.ByName("Создать карточку", menu, 800);

            if (card is not null && card.Current.IsEnabled)
            {
                return menu;
            }

            // Dismissed with a click rather than Escape. Escape is the shell's "close
            // the book" key, and if the popup has already gone the keystroke reaches the
            // window and puts the reader back in the library — which would make the next
            // attempt fail for an entirely unrelated reason.
            Input.ClickScreen(left, (int)(box.Top + 8));
            Thread.Sleep(300);
        }

        return null;
    }

    /// <summary>Stops whatever is serving the local model, so the next request cannot be answered.</summary>
    private static bool StopLocalModel()
    {
        var stopped = false;

        foreach (var server in Process.GetProcessesByName("llama-server"))
        {
            using (server)
            {
                try
                {
                    server.Kill(entireProcessTree: true);
                    server.WaitForExit(5_000);
                    stopped = true;
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
            }
        }

        if (stopped)
        {
            Thread.Sleep(1_000);
        }

        return stopped;
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>Opens a book if the library has one. Returns false when there is nothing to open.</summary>
    private static bool OpenBook(Ui ui)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Ui.Invoke(ui.Require("Чтение"));
            ui.Ping();
            Thread.Sleep(900);

            // Already reading?
            if (ui.ByName("Текст книги", timeoutMs: 800) is not null)
            {
                return true;
            }

            foreach (var (x, y) in CardTargets(ui))
            {
                Input.ClickScreen(x, y);

                // A large book is genuinely slow to import the first time; that is work
                // this pass moved off the dispatcher, not work it removed.
                if (ui.ByName("Текст книги", timeoutMs: 45_000) is not null)
                {
                    Thread.Sleep(1_500);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Screen points that open a reading project.
    /// </summary>
    /// <remarks>
    /// A library card is a Border with a click handler, and WPF gives a plain Border no
    /// automation peer, so the card itself is not in the tree at all — the
    /// <c>AutomationProperties.Name</c> it carries never reaches a client. (Worth fixing
    /// on its own account: a card that cannot be reached by keyboard or read by a screen
    /// reader is an accessibility defect. Recorded in FUTURE_WORK.md.)
    ///
    /// What is in the tree is the counts line inside each card, and a click on it lands
    /// on the card underneath — the same gesture a person makes.
    /// </remarks>
    private static List<(int X, int Y)> CardTargets(Ui ui)
    {
        var targets = new List<(int X, int Y)>();

        AutomationElementCollection found;
        try
        {
            found = ui.Window.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        }
        catch (ElementNotAvailableException)
        {
            return targets;
        }

        for (var index = 0; index < found.Count; index++)
        {
            try
            {
                var name = found[index].Current.Name;
                var rect = found[index].Current.BoundingRectangle;

                if (name.Contains("прочитано", StringComparison.Ordinal)
                    && !rect.IsEmpty && rect.Width >= 10 && rect.Height >= 8)
                {
                    targets.Add(((int)(rect.Left + rect.Width / 2), (int)(rect.Top + rect.Height / 2)));
                }
            }
            catch (ElementNotAvailableException)
            {
                // Redrawn between the find and the read.
            }
        }

        return targets;
    }

    private static void TypeInto(Ui ui, string fieldName, string text)
    {
        var field = ui.ByName(fieldName, timeoutMs: 3_000);
        if (field is null)
        {
            return;
        }

        if (field.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
        {
            ((ValuePattern)pattern).SetValue(text);
        }
    }
}
