using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WriteLite.Services.Notes;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Views.Notes;

/// <summary>
/// The notes calendar: a month of events and dated tasks, and the agenda of the chosen day.
/// </summary>
/// <remarks>
/// <para>Everything on it is a note: an event is a note of kind <see cref="NoteKind.Event"/> whose
/// <see cref="Note.DueDate"/> is when it happens, and a task with a due date appears on that day.
/// So the calendar needs no store of its own, an event can hold text, a checklist or a drawing like
/// any note, and clicking one opens it in the notes editor.</para>
///
/// <para>Adding is typed, not dialogued: pick a day, type «Встреча с Аней 15:30» and press Enter —
/// a trailing time becomes the event's time, no time means all day.</para>
/// </remarks>
public sealed class NotesCalendarView : UserControl
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly TextBlock _monthTitle;
    private readonly UniformGrid _days;
    private readonly TextBlock _agendaTitle;
    private readonly TextBlock _agendaSubtitle;
    private readonly StackPanel _agenda;
    private readonly TextBox _newBox;
    private NotesService? _notes;
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selected = DateTime.Today;

    public NotesCalendarView()
    {
        SetResourceReference(BackgroundProperty, "WlBgBase");

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });

        // ── Month ──────────────────────────────────────────────────────────────
        var month = new DockPanel { Margin = new Thickness(24, 18, 16, 16) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        nav.Children.Add(NavButton("‹", "Предыдущий месяц", () => ShowMonth(_month.AddMonths(-1))));
        nav.Children.Add(TextButton("Сегодня", () => { ShowMonth(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)); SelectDay(DateTime.Today); }));
        nav.Children.Add(NavButton("›", "Следующий месяц", () => ShowMonth(_month.AddMonths(1))));
        DockPanel.SetDock(nav, Dock.Right);
        header.Children.Add(nav);
        _monthTitle = new TextBlock { FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _monthTitle.SetResourceReference(TextBlock.ForegroundProperty, "WlText");
        header.Children.Add(_monthTitle);
        DockPanel.SetDock(header, Dock.Top);
        month.Children.Add(header);

        var weekdays = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var name in new[] { "ПН", "ВТ", "СР", "ЧТ", "ПТ", "СБ", "ВС" })
        {
            var label = new TextBlock { Text = name, Margin = new Thickness(8, 0, 0, 0) };
            label.SetResourceReference(StyleProperty, "WlMonoLabel");
            weekdays.Children.Add(label);
        }

        DockPanel.SetDock(weekdays, Dock.Top);
        month.Children.Add(weekdays);
        _days = new UniformGrid { Columns = 7, Rows = 6 };
        month.Children.Add(_days);
        root.Children.Add(month);

        // ── Agenda ─────────────────────────────────────────────────────────────
        var agendaBorder = new Border { BorderThickness = new Thickness(1, 0, 0, 0) };
        agendaBorder.SetResourceReference(Border.BorderBrushProperty, "WlLine");
        agendaBorder.SetResourceReference(BackgroundProperty, "WlBg");
        Grid.SetColumn(agendaBorder, 1);
        var agenda = new DockPanel { Margin = new Thickness(18, 20, 16, 16) };
        _agendaTitle = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        _agendaTitle.SetResourceReference(TextBlock.ForegroundProperty, "WlText");
        _agendaSubtitle = new TextBlock { Margin = new Thickness(0, 3, 0, 14) };
        _agendaSubtitle.SetResourceReference(StyleProperty, "WlCaption");
        var titles = new StackPanel();
        titles.Children.Add(_agendaTitle);
        titles.Children.Add(_agendaSubtitle);
        DockPanel.SetDock(titles, Dock.Top);
        agenda.Children.Add(titles);

        var add = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _newBox = new TextBox { Tag = "Новое событие, например «Созвон 15:30»" };
        _newBox.SetResourceReference(StyleProperty, "WlTextInput");
        _newBox.KeyDown += NewBox_KeyDown;
        System.Windows.Automation.AutomationProperties.SetName(_newBox, "Новое событие");
        var addButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var addEvent = new Button { Content = "Добавить событие", Padding = new Thickness(14, 0, 14, 0), MinHeight = 30 };
        addEvent.SetResourceReference(StyleProperty, "WlPrimaryButton");
        addEvent.Click += (_, _) => AddFromBox(NoteKind.Event);
        var addTask = new Button { Content = "Задача на день", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 0, 12, 0), MinHeight = 30 };
        addTask.SetResourceReference(StyleProperty, "WlSecondaryButton");
        addTask.Click += (_, _) => AddFromBox(NoteKind.Task);
        addButtons.Children.Add(addEvent);
        addButtons.Children.Add(addTask);
        add.Children.Add(_newBox);
        add.Children.Add(addButtons);
        DockPanel.SetDock(add, Dock.Bottom);
        agenda.Children.Add(add);

        _agenda = new StackPanel();
        var scroll = new ScrollViewer { Content = _agenda, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.SetResourceReference(StyleProperty, "WlScrollViewer");
        agenda.Children.Add(scroll);
        agendaBorder.Child = agenda;
        root.Children.Add(agendaBorder);

        Content = root;
        Focusable = true;
        PreviewKeyDown += OnKey;
    }

    /// <summary>The user opened a note from the calendar.</summary>
    public event Action<Note>? OpenRequested;

    public DateTime SelectedDay => _selected;

    public DateTime VisibleMonth => _month;

    public void Bind(NotesService notes)
    {
        _notes = notes;
        Refresh();
    }

    public void Refresh()
    {
        RenderMonth();
        RenderAgenda();
    }

    public void ShowMonth(DateTime month)
    {
        _month = new DateTime(month.Year, month.Month, 1);
        Refresh();
    }

    public void SelectDay(DateTime day)
    {
        _selected = day.Date;
        if (_selected.Year != _month.Year || _selected.Month != _month.Month)
        {
            _month = new DateTime(_selected.Year, _selected.Month, 1);
        }

        Refresh();
    }

    /// <summary>Notes on a day: events and dated tasks, timed ones first in time order.</summary>
    public IReadOnlyList<Note> ItemsOn(DateTime day) =>
        _notes is null
            ? []
            : _notes.All
                .Where(n => !n.IsDeleted && n.DueDate is { } d && d.LocalDateTime.Date == day.Date)
                .OrderBy(n => n.IsAllDay || n.DueDate!.Value.TimeOfDay == TimeSpan.Zero ? 0 : 1)
                .ThenBy(n => n.DueDate)
                .ThenBy(n => n.IsCompleted)
                .ToList();

    /// <summary>Creates an event or a task on the selected day from typed text («Созвон 15:30»).</summary>
    public Note? AddOnSelectedDay(string text, NoteKind kind)
    {
        if (_notes is null) return null;
        var (title, time) = SplitTime(text.Trim());
        if (title.Length == 0) return null;
        var note = _notes.Create(kind, title);
        var copy = note.Clone();
        copy.DueDate = time is { } t ? new DateTimeOffset(_selected + t) : new DateTimeOffset(_selected);
        copy.IsAllDay = time is null;
        _notes.Update(copy);
        return copy;
    }

    /// <summary>«Созвон с командой 15:30» → («Созвон с командой», 15:30); also «в 9», «9:00».</summary>
    public static (string Title, TimeSpan? Time) SplitTime(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"^(?<title>.*?)\s*(?:\bв\s+)?(?<h>[01]?\d|2[0-3])(?:[:.](?<m>[0-5]\d))?\s*$");
        if (match.Success && match.Groups["title"].Value.Trim().Length > 0
            && (match.Groups["m"].Success || text.Contains(" в ", StringComparison.OrdinalIgnoreCase)))
        {
            var hours = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minutes = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            return (match.Groups["title"].Value.Trim(), new TimeSpan(hours, minutes, 0));
        }

        return (text, null);
    }

    private void RenderMonth()
    {
        _monthTitle.Text = Ru.TextInfo.ToTitleCase(_month.ToString("MMMM yyyy", Ru));
        _days.Children.Clear();
        var offset = ((int)_month.DayOfWeek + 6) % 7;
        var first = _month.AddDays(-offset);
        for (var i = 0; i < 42; i++)
        {
            _days.Children.Add(DayCell(first.AddDays(i)));
        }
    }

    private FrameworkElement DayCell(DateTime day)
    {
        var inMonth = day.Month == _month.Month;
        var isToday = day == DateTime.Today;
        var isSelected = day == _selected;

        var number = new TextBlock
        {
            Text = day.Day.ToString(CultureInfo.InvariantCulture),
            FontSize = 13,
            FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        number.SetResourceReference(TextBlock.ForegroundProperty, isToday ? "WlOnBrand" : inMonth ? "WlText" : "WlTextSubtle");
        var badge = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Child = number,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        if (isToday) badge.SetResourceReference(Border.BackgroundProperty, "WlBrand");

        var stack = new StackPanel();
        stack.Children.Add(badge);
        var items = ItemsOn(day);
        foreach (var note in items.Take(3))
        {
            stack.Children.Add(Chip(note));
        }

        if (items.Count > 3)
        {
            var more = new TextBlock { Text = $"ещё {items.Count - 3}", FontSize = 10, Margin = new Thickness(4, 1, 0, 0) };
            more.SetResourceReference(TextBlock.ForegroundProperty, "WlTextMuted");
            stack.Children.Add(more);
        }

        var cell = new Border
        {
            Margin = new Thickness(2),
            Padding = new Thickness(5, 4, 4, 4),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = stack,
            Cursor = System.Windows.Input.Cursors.Hand,
            ClipToBounds = true,
            Opacity = inMonth ? 1 : 0.55,
        };
        cell.SetResourceReference(Border.BackgroundProperty, isSelected ? "WlBrandSoft" : "WlSurface");
        cell.SetResourceReference(Border.BorderBrushProperty, isSelected ? "WlBrand" : "WlLineSoft");
        System.Windows.Automation.AutomationProperties.SetName(cell, day.ToString("d MMMM", Ru));
        cell.MouseLeftButtonDown += (_, e) =>
        {
            SelectDay(day);
            if (e.ClickCount == 2) _newBox.Focus();
        };
        return cell;
    }

    private FrameworkElement Chip(Note note)
    {
        var isEvent = note.Kind == NoteKind.Event;
        var text = new TextBlock
        {
            Text = (isEvent ? string.Empty : note.IsCompleted ? "✓ " : "○ ") + note.DisplayTitle,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, note.IsCompleted ? "WlTextMuted" : "WlText");
        if (note.IsCompleted) text.TextDecorations = TextDecorations.Strikethrough;
        var chip = new Border
        {
            Margin = new Thickness(0, 2, 0, 0),
            Padding = new Thickness(5, 1, 4, 1),
            CornerRadius = new CornerRadius(4),
            Child = text,
            BorderThickness = new Thickness(2, 0, 0, 0),
        };
        chip.SetResourceReference(Border.BorderBrushProperty, isEvent ? "WlBrand" : "WlInfo");
        chip.SetResourceReference(Border.BackgroundProperty, isEvent ? "WlBrandDim" : "WlInfoSoft");
        return chip;
    }

    private void RenderAgenda()
    {
        _agendaTitle.Text = _selected == DateTime.Today
            ? "Сегодня, " + _selected.ToString("d MMMM", Ru)
            : Ru.TextInfo.ToTitleCase(_selected.ToString("dddd", Ru)) + ", " + _selected.ToString("d MMMM", Ru);
        var items = ItemsOn(_selected);
        _agendaSubtitle.Text = items.Count == 0
            ? "Ничего не запланировано"
            : $"{items.Count} {Services.RussianPlural.Form(items.Count, "запись", "записи", "записей")}";
        _agenda.Children.Clear();
        foreach (var note in items)
        {
            _agenda.Children.Add(AgendaRow(note));
        }
    }

    private FrameworkElement AgendaRow(Note note)
    {
        var timed = note.DueDate is { } due && !note.IsAllDay && due.TimeOfDay != TimeSpan.Zero;
        var time = new TextBlock
        {
            Text = timed ? note.DueDate!.Value.ToString("HH:mm", Ru) : "весь день",
            Width = 62,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        };
        time.SetResourceReference(TextBlock.ForegroundProperty, "WlTextSecondary");

        var title = new TextBlock
        {
            Text = note.DisplayTitle,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, note.IsCompleted ? "WlTextMuted" : "WlText");
        if (note.IsCompleted) title.TextDecorations = TextDecorations.Strikethrough;
        var kind = new TextBlock { Text = KindLabel(note), FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "WlTextMuted");
        var body = new StackPanel();
        body.Children.Add(title);
        body.Children.Add(kind);

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(time, Dock.Left);
        row.Children.Add(time);
        if (note.IsCompletable)
        {
            var done = new CheckBox { IsChecked = note.IsCompleted, Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            done.SetResourceReference(StyleProperty, "WlCheckBox");
            done.Click += (_, _) => _notes?.SetCompleted(note.Id, done.IsChecked == true);
            DockPanel.SetDock(done, Dock.Left);
            row.Children.Add(done);
        }

        row.Children.Add(body);
        var border = new Border
        {
            Child = row,
            Padding = new Thickness(8, 8, 8, 8),
            CornerRadius = new CornerRadius(8),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = System.Windows.Media.Brushes.Transparent,
        };
        border.MouseEnter += (_, _) => border.SetResourceReference(Border.BackgroundProperty, "WlHover");
        border.MouseLeave += (_, _) => border.Background = System.Windows.Media.Brushes.Transparent;
        border.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindParent<CheckBox>(source) is not null) return;
            OpenRequested?.Invoke(note);
        };
        return border;
    }

    private static string KindLabel(Note note) => note.Kind switch
    {
        NoteKind.Event => "Событие",
        NoteKind.Task => "Задача",
        NoteKind.Checklist => $"Список · {note.DoneCount} из {note.Items.Count}",
        NoteKind.Goal => "Цель",
        _ => "Заметка",
    };

    private void NewBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddFromBox((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? NoteKind.Task : NoteKind.Event);
        e.Handled = true;
    }

    private void AddFromBox(NoteKind kind)
    {
        var text = _newBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            _newBox.Focus();
            return;
        }

        if (AddOnSelectedDay(text, kind) is not null)
        {
            _newBox.Text = string.Empty;
            Refresh();
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_newBox.IsKeyboardFocusWithin) return;
        var delta = e.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            Key.Up => -7,
            Key.Down => 7,
            _ => 0,
        };
        if (delta == 0) return;
        SelectDay(_selected.AddDays(delta));
        e.Handled = true;
    }

    private Button NavButton(string glyph, string name, Action action)
    {
        var button = new Button { Content = glyph, Width = 32, Height = 30, FontSize = 18 };
        button.SetResourceReference(StyleProperty, "WlIconButton");
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    private Button TextButton(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(4, 0, 4, 0) };
        button.SetResourceReference(StyleProperty, "WlTextButton");
        button.Click += (_, _) => action();
        return button;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match) return match;
        }

        return null;
    }
}
