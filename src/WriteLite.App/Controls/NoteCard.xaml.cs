using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WriteLite.Services;
using WriteLite.Services.Notes;
using Brush = System.Windows.Media.Brush;
using CheckBox = System.Windows.Controls.CheckBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using TextBlock = System.Windows.Controls.TextBlock;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Controls;

/// <summary>
/// One card on the notes board.
/// </summary>
/// <remarks>
/// The card renders a <see cref="Note"/> and raises intent; it never touches the
/// store. That keeps the board's rules — what completing does, what pinning means for
/// ordering — in <see cref="NotesService"/> where the tests can reach them, and leaves
/// the card responsible only for looking right and feeling right.
///
/// The satisfying part of a task card is finishing it, so that is where the motion
/// budget goes: the tick draws itself (see <c>WlNoteCheckBox</c>), and the card gives
/// one short acknowledging dip. Everything else is a hover.
/// </remarks>
public partial class NoteCard : UserControl
{
    /// <summary>Checklist lines shown on the face of a card before it says "and more".</summary>
    private const int VisibleChecklistItems = 4;

    private bool _suppressEvents;

    public NoteCard()
    {
        InitializeComponent();
    }

    public Note? Note { get; private set; }

    public event Action<Note>? Opened;

    public event Action<Note, bool>? CompletionChanged;

    public event Action<Note, bool>? PinChanged;

    public event Action<Note, NoteChecklistItem, bool>? ItemChanged;

    public event Action<Note>? DuplicateRequested;

    public event Action<Note>? DeleteRequested;

    public void Bind(Note note)
    {
        Note = note ?? throw new ArgumentNullException(nameof(note));

        _suppressEvents = true;
        try
        {
            Render(note);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void Render(Note note)
    {
        TitleText.Text = note.DisplayTitle;
        TitleText.TextDecorations = note.IsCompleted ? TextDecorations.Strikethrough : null;
        TitleText.Foreground = (Brush)FindResource(note.IsCompleted ? "WlTextMuted" : "WlText");

        DoneBox.Visibility = note.IsCompletable ? Visibility.Visible : Visibility.Collapsed;
        DoneBox.IsChecked = note.IsCompleted;

        Shell.Opacity = note.IsCompleted ? 0.62 : 1;

        // The spine is the only place the kind is encoded as colour, and only pinning
        // promotes it to the accent. Four coloured cards would be a different product.
        Spine.Background = (Brush)FindResource(note.IsPinned ? "WlBrand" : "WlLineStrong");
        Spine.Visibility = note.IsPinned || note.Kind != NoteKind.Note ? Visibility.Visible : Visibility.Hidden;

        PinButton.ToolTip = note.IsPinned ? "Открепить" : "Закрепить";
        PinIcon.Stroke = (Brush)FindResource(note.IsPinned ? "WlBrand" : "WlTextMuted");

        RenderBody(note);
        RenderMeta(note);

        AutomationProperties.SetName(this, $"{KindName(note.Kind)}: {note.DisplayTitle}");
    }

    private void RenderBody(Note note)
    {
        ChecklistPanel.Children.Clear();

        var hasItems = note.Items.Count > 0 && note.Kind is NoteKind.Checklist or NoteKind.Goal;

        var body = note.Text.Trim();
        if (!string.IsNullOrEmpty(note.Title) || hasItems)
        {
            // The title already showed the first line when the note had no title of its
            // own; repeating it as the body would print the same sentence twice.
            BodyText.Text = body;
        }
        else
        {
            var lines = body.Split('\n');
            BodyText.Text = lines.Length > 1 ? string.Join('\n', lines[1..]).Trim() : string.Empty;
        }

        BodyText.Visibility = string.IsNullOrWhiteSpace(BodyText.Text) ? Visibility.Collapsed : Visibility.Visible;

        if (!hasItems)
        {
            ChecklistPanel.Visibility = Visibility.Collapsed;
            MoreItemsText.Visibility = Visibility.Collapsed;
            return;
        }

        ChecklistPanel.Visibility = Visibility.Visible;

        foreach (var item in note.Items.Take(VisibleChecklistItems))
        {
            ChecklistPanel.Children.Add(BuildChecklistRow(note, item));
        }

        var hidden = note.Items.Count - VisibleChecklistItems;
        MoreItemsText.Text = hidden > 0
            ? $"ещё {hidden} {RussianPlural.Form(hidden, "пункт", "пункта", "пунктов")}"
            : string.Empty;
        MoreItemsText.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement BuildChecklistRow(Note note, NoteChecklistItem item)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var box = new CheckBox
        {
            Style = (Style)FindResource("WlNoteCheckBox"),
            IsChecked = item.IsDone,
            Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Top,
            IsEnabled = !note.IsCompleted
        };

        AutomationProperties.SetName(box, item.Text);

        var text = new TextBlock
        {
            Text = item.Text,
            Style = (Style)FindResource(item.IsDone ? "WlChecklistTextDone" : "WlChecklistText")
        };

        void Toggle(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_suppressEvents || Note is null)
            {
                return;
            }

            var done = box.IsChecked == true;

            // The line restyles with the tick rather than after a reload, so the two
            // halves of one gesture happen together.
            text.Style = (Style)FindResource(done ? "WlChecklistTextDone" : "WlChecklistText");
            ItemChanged?.Invoke(Note, item, done);
        }

        box.Checked += Toggle;
        box.Unchecked += Toggle;

        // Nothing is attached to the preview event. A click on a checklist line must not
        // also open the card, and it does not: ButtonBase marks the bubbling mouse-up
        // handled once it has turned it into a Click. Swallowing the *preview* to achieve
        // the same thing marks the one shared Handled flag before ToggleButton has seen
        // the click, which does not stop the card opening so much as stop the line ever
        // being tickable.
        Grid.SetColumn(box, 0);
        Grid.SetColumn(text, 1);
        row.Children.Add(box);
        row.Children.Add(text);
        return row;
    }

    private void RenderMeta(Note note)
    {
        Controls.Type.SetTracked(KindText, KindName(note.Kind).ToUpperInvariant());

        var showProgress = note.Items.Count > 0;
        ProgressBadge.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        if (showProgress)
        {
            Controls.Type.SetTracked(ProgressText, $"{note.DoneCount}/{note.Items.Count}");
        }

        if (note.DueDate is { } due)
        {
            DueBadge.Visibility = Visibility.Visible;
            Controls.Type.SetTracked(DueText, FormatDue(due));

            // Overdue is the one status worth an accent, and only while the card is
            // still open — a finished task cannot be late any more.
            var overdue = !note.IsCompleted && due.Date < DateTimeOffset.Now.Date;
            DueBadge.Style = (Style)FindResource(overdue ? "WlNoteBadgeAccent" : "WlNoteBadge");
            DueText.Foreground = (Brush)FindResource(overdue ? "WlBrand" : "WlTextMuted");
        }
        else
        {
            DueBadge.Visibility = Visibility.Collapsed;
        }

        var hasCategory = !string.IsNullOrWhiteSpace(note.Category);
        CategoryBadge.Visibility = hasCategory ? Visibility.Visible : Visibility.Collapsed;
        if (hasCategory)
        {
            Controls.Type.SetTracked(CategoryText, note.Category!.ToUpperInvariant());
        }

        Controls.Type.SetTracked(
            ModifiedText,
            note.IsCompleted && note.CompletedAt is { } completed
                ? $"ВЫПОЛНЕНО {FormatWhen(completed)}"
                : $"ИЗМЕНЕНО {FormatWhen(note.ModifiedAt)}");
    }

    internal static string KindName(NoteKind kind) => kind switch
    {
        NoteKind.Task => "Задача",
        NoteKind.Checklist => "Список",
        NoteKind.Goal => "Цель",
        _ => "Заметка"
    };

    private static string FormatDue(DateTimeOffset due)
    {
        var days = (due.Date - DateTimeOffset.Now.Date).Days;
        return days switch
        {
            0 => "СЕГОДНЯ",
            1 => "ЗАВТРА",
            -1 => "ВЧЕРА",
            < 0 => $"ПРОСРОЧЕНО НА {-days} {RussianPlural.Form(-days, "ДЕНЬ", "ДНЯ", "ДНЕЙ")}",
            _ => due.ToString("d MMM").ToUpperInvariant()
        };
    }

    private static string FormatWhen(DateTimeOffset when)
    {
        var elapsed = DateTimeOffset.Now - when;
        return elapsed switch
        {
            { TotalMinutes: < 1 } => "ТОЛЬКО ЧТО",
            { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes} МИН НАЗАД",
            { TotalDays: < 1 } => $"{(int)elapsed.TotalHours} Ч НАЗАД",
            { TotalDays: < 7 } => $"{(int)elapsed.TotalDays} {RussianPlural.Form((int)elapsed.TotalDays, "ДЕНЬ", "ДНЯ", "ДНЕЙ")} НАЗАД",
            _ => when.ToString("d MMM yyyy").ToUpperInvariant()
        };
    }

    // ── Interaction ──────────────────────────────────────────────────────────

    private void Card_KeyDown(object sender, KeyEventArgs e)
    {
        if (Note is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter or Key.Space:
                e.Handled = true;
                Opened?.Invoke(Note);
                break;

            case Key.Delete:
                e.Handled = true;
                DeleteRequested?.Invoke(Note);
                break;
        }
    }

    private void Done_Changed(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_suppressEvents || Note is null)
        {
            return;
        }

        var completed = DoneBox.IsChecked == true;

        // The card restyles itself before anyone is told. Completion used to be a round
        // trip — raise the event, let the store fire Changed, let the page rebuild the
        // whole board — and the card carrying the tick was destroyed and replaced within
        // a dispatcher hop, so the tick the template draws over a fifth of a second was
        // never once seen. What the user did to this card is knowable here; the store
        // decides what it means for the board, not for this card's own appearance.
        ApplyCompletedAppearance(completed);

        if (completed)
        {
            AcknowledgeCompletion();
        }

        CompletionChanged?.Invoke(Note, completed);
    }

    /// <summary>
    /// The instant local half of completing a card: strike, dim, and disable the checklist.
    /// </summary>
    /// <remarks>
    /// Deliberately only the parts of <see cref="Render"/> that completion changes.
    /// Re-rendering the whole card would rebuild the checklist rows underneath the
    /// cursor, and the point of this method is that nothing the user is touching moves.
    /// </remarks>
    internal void ApplyCompletedAppearance(bool completed)
    {
        TitleText.TextDecorations = completed ? TextDecorations.Strikethrough : null;
        TitleText.Foreground = (Brush)FindResource(completed ? "WlTextMuted" : "WlText");
        Shell.Opacity = completed ? 0.62 : 1;

        foreach (var box in ChecklistPanel.Children.OfType<Grid>()
                     .SelectMany(row => row.Children.OfType<CheckBox>()))
        {
            box.IsEnabled = !completed;
        }
    }

    /// <summary>
    /// The card's half of the completion gesture: one short dip, then settle.
    /// </summary>
    /// <remarks>
    /// Scale only, so nothing re-measures — the same rule the rest of the product
    /// follows — and skipped outright when Windows says animation is off. About a
    /// fifth of a second: long enough to be felt, short enough that ticking four
    /// things in a row never queues up.
    /// </remarks>
    private void AcknowledgeCompletion()
    {
        if (!Motion.IsEnabled)
        {
            return;
        }

        var dip = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        dip.KeyFrames.Add(new EasingDoubleKeyFrame(0.975, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80)))
        {
            EasingFunction = Motion.Editorial
        });
        dip.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(230)))
        {
            EasingFunction = Motion.Editorial
        });

        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, dip);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, dip);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Note is not null)
        {
            PinChanged?.Invoke(Note, !Note.IsPinned);
        }
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Note is null)
        {
            return;
        }

        var menu = new ContextMenu
        {
            Style = TryFindResource("WlContextMenu") as Style,
            PlacementTarget = MenuButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        menu.Items.Add(Item("Открыть", () => Opened?.Invoke(Note)));
        menu.Items.Add(Item(Note.IsPinned ? "Открепить" : "Закрепить", () => PinChanged?.Invoke(Note, !Note.IsPinned)));

        if (Note.IsCompletable)
        {
            menu.Items.Add(Item(
                Note.IsCompleted ? "Вернуть в работу" : "Отметить выполненной",
                () => CompletionChanged?.Invoke(Note, !Note.IsCompleted)));
        }

        menu.Items.Add(Item("Дублировать", () => DuplicateRequested?.Invoke(Note)));
        menu.Items.Add(new Separator { Style = TryFindResource("WlMenuSeparator") as Style });
        menu.Items.Add(Item("Удалить", () => DeleteRequested?.Invoke(Note)));

        menu.IsOpen = true;
    }

    private MenuItem Item(string header, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            Style = TryFindResource("WlMenuItem") as Style
        };

        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// Opens the card, unless a control inside it already dealt with the click.
    /// </summary>
    /// <remarks>
    /// The controls on the card — the completion box, the pin, the overflow menu — each
    /// used to swallow <c>PreviewMouseLeftButtonUp</c> so their clicks would not also
    /// open the card. That is the wrong event to use: preview and bubble share one
    /// <c>Handled</c> flag, and setting it during the tunnel is setting it before
    /// <c>ButtonBase</c> has had the chance to turn the click into a <c>Click</c>. The
    /// result was three controls that could not be operated with a mouse at all, while
    /// the card underneath was never in danger of opening in the first place: a button
    /// marks the bubbling mouse-up handled the moment it has processed it.
    /// </remarks>
    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (Note is not null)
        {
            Opened?.Invoke(Note);
        }
    }
}
