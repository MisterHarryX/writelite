using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WriteLite.Resources;
using WriteLite.Services;
using WriteLite.Services.Lexical;
using WriteLite.Services.Notes;
using Brush = System.Windows.Media.Brush;
using CheckBox = System.Windows.Controls.CheckBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace WriteLite.Views;

/// <summary>
/// The full editor for one card.
/// </summary>
/// <remarks>
/// Works on a copy. Everything typed here lands on a clone of the note, and only
/// <see cref="Result"/> — handed back when the user saves — is written to the store.
/// Cancelling therefore genuinely cancels, including the checklist items added and
/// removed along the way, which is not true of an editor that mutates the live object
/// and relies on a reload to undo it.
/// </remarks>
public partial class NoteEditorWindow : Window
{
    private readonly Note _working;
    private readonly bool _startedEmpty;

    public NoteEditorWindow(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        InitializeComponent();

        _working = Clone(note);
        _startedEmpty = IsBlank(note);

        Render();

        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.CaretIndex = TitleBox.Text.Length;
        };

        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>The edited note. Only meaningful when the dialog returned true.</summary>
    public Note Result => _working;

    /// <summary>True when the user asked to delete the card from inside the editor.</summary>
    public bool WasDeleted { get; private set; }

    /// <summary>
    /// True when the card was created a moment ago and closed without anything typed
    /// into it, so the board can withdraw it rather than leave an empty card behind.
    /// </summary>
    public bool IsAbandoned { get; private set; }

    /// <summary>Raised when the user asks to look a word from the note up in the dictionary.</summary>
    public event Action<string>? WordNavigationRequested;

    // ── Rendering ────────────────────────────────────────────────────────────

    private void Render()
    {
        TitleBox.Text = _working.Title;
        TextBoxBody.Text = _working.Text;
        CategoryBox.Text = _working.Category ?? string.Empty;
        DueBox.Text = _working.DueDate?.ToString("dd.MM.yyyy") ?? string.Empty;

        (_working.Kind switch
        {
            NoteKind.Task => KindTask,
            NoteKind.Checklist => KindChecklist,
            NoteKind.Goal => KindGoal,
            _ => KindNote
        }).IsChecked = true;

        CompletedBox.IsChecked = _working.IsCompleted;
        CompletedBox.Visibility = _working.IsCompletable ? Visibility.Visible : Visibility.Collapsed;

        RenderPin();
        RenderKindChrome();
        RenderItems();

        Controls.Type.SetTracked(
            CreatedText,
            string.Format(Strings.NoteEdit_CreatedModified, _working.CreatedAt, _working.ModifiedAt).ToUpperInvariant());
    }

    private void RenderPin()
    {
        PinButton.ToolTip = _working.IsPinned ? Strings.NoteEdit_Unpin : Strings.NoteEdit_Pin;
        PinIcon.Stroke = (Brush)FindResource(_working.IsPinned ? "WlBrand" : "WlTextMuted");
    }

    private void RenderKindChrome()
    {
        Controls.Type.SetTracked(KindLabel, Controls.NoteCard.KindName(_working.Kind).ToUpperInvariant());

        // A plain note has no items and no completion; a goal calls its items steps.
        var wantsItems = _working.Kind is NoteKind.Checklist or NoteKind.Goal;
        ChecklistSection.Visibility = wantsItems ? Visibility.Visible : Visibility.Collapsed;
        Controls.Type.SetTracked(ChecklistLabel, _working.Kind == NoteKind.Goal ? Strings.NoteEdit_TrackedSteps : Strings.NoteEdit_TrackedItems);

        CompletedBox.Visibility = _working.IsCompletable ? Visibility.Visible : Visibility.Collapsed;
        Title = Controls.NoteCard.KindName(_working.Kind);
    }

    private void RenderItems()
    {
        ItemsPanel.Children.Clear();

        foreach (var item in _working.Items)
        {
            ItemsPanel.Children.Add(BuildItemRow(item));
        }

        Controls.Type.SetTracked(
            ChecklistProgress,
            _working.Items.Count == 0 ? string.Empty : $"{_working.DoneCount}/{_working.Items.Count}");
    }

    private FrameworkElement BuildItemRow(NoteChecklistItem item)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var box = new CheckBox
        {
            Style = (Style)FindResource("WlNoteCheckBox"),
            IsChecked = item.IsDone,
            Margin = new Thickness(0, 4, 10, 0),
            VerticalAlignment = VerticalAlignment.Top
        };

        AutomationProperties.SetName(box, item.Text);

        var text = new TextBox
        {
            Style = (Style)FindResource("WlSmallInput"),
            Text = item.Text,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false
        };

        AutomationProperties.SetName(text, Strings.NoteEdit_ItemTextAutomation);
        text.TextChanged += (_, _) => item.Text = text.Text;
        ApplyDoneStyling(text, item.IsDone);

        box.Checked += (_, _) =>
        {
            item.IsDone = true;
            ApplyDoneStyling(text, true);
            RenderProgressOnly();
        };

        box.Unchecked += (_, _) =>
        {
            item.IsDone = false;
            ApplyDoneStyling(text, false);
            RenderProgressOnly();
        };

        var remove = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("WlIconButton"),
            Width = 30,
            Height = 30,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = Strings.NoteEdit_RemoveItem,
            VerticalAlignment = VerticalAlignment.Top,
            Content = new System.Windows.Shapes.Path
            {
                Style = (Style)FindResource("WlIconSm"),
                Data = (Geometry)FindResource("WlIconTrash"),
                Stroke = (Brush)FindResource("WlTextMuted")
            }
        };

        AutomationProperties.SetName(remove, string.Format(Strings.NoteEdit_RemoveItemAutomation, item.Text));
        remove.Click += (_, _) =>
        {
            _working.Items.Remove(item);
            RenderItems();
        };

        Grid.SetColumn(box, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(remove, 2);
        row.Children.Add(box);
        row.Children.Add(text);
        row.Children.Add(remove);
        return row;
    }

    private void RenderProgressOnly() => Controls.Type.SetTracked(
        ChecklistProgress,
        _working.Items.Count == 0 ? string.Empty : $"{_working.DoneCount}/{_working.Items.Count}");

    private void ApplyDoneStyling(TextBox text, bool done)
    {
        text.Foreground = (Brush)FindResource(done ? "WlTextMuted" : "WlText");
        text.TextDecorations = done ? TextDecorations.Strikethrough : null;
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    private void Kind_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        _working.Kind = sender switch
        {
            _ when ReferenceEquals(sender, KindTask) => NoteKind.Task,
            _ when ReferenceEquals(sender, KindChecklist) => NoteKind.Checklist,
            _ when ReferenceEquals(sender, KindGoal) => NoteKind.Goal,
            _ => NoteKind.Note
        };

        RenderKindChrome();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _working.IsPinned = !_working.IsPinned;
        RenderPin();
    }

    private void AddItem_Click(object sender, RoutedEventArgs e) => AddItem();

    private void NewItem_KeyDown(object sender, KeyEventArgs e)
    {
        if (!e.SubmitPressed()) return;

        AddItem();
    }

    private void AddItem()
    {
        var text = (NewItemBox.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        _working.Items.Add(new NoteChecklistItem { Text = text });
        NewItemBox.Text = string.Empty;
        RenderItems();

        // Focus stays in the input so a list can be typed in one pass.
        NewItemBox.Focus();
    }

    private void DueShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        if (tag == "clear")
        {
            DueBox.Text = string.Empty;
            _working.DueDate = null;
            return;
        }

        var days = int.Parse(tag, CultureInfo.InvariantCulture);
        var due = DateTimeOffset.Now.Date.AddDays(days);
        _working.DueDate = new DateTimeOffset(due);
        DueBox.Text = due.ToString("dd.MM.yyyy");
    }

    private void Due_LostFocus(object sender, RoutedEventArgs e) => ParseDue();

    /// <summary>
    /// Reads the due-date field, accepting the forms people actually type.
    /// </summary>
    /// <remarks>
    /// Nothing is reported as invalid. A field that cannot be understood simply clears
    /// the date and shows what was stored, because an error dialog over a date is a
    /// worse outcome than the shortcut buttons sitting right underneath it.
    /// </remarks>
    private void ParseDue()
    {
        var raw = (DueBox.Text ?? string.Empty).Trim();

        if (raw.Length == 0)
        {
            _working.DueDate = null;
            return;
        }

        string[] formats = ["dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy", "dd/MM/yyyy", "yyyy-MM-dd"];

        if (DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsed))
        {
            _working.DueDate = new DateTimeOffset(parsed.Date);
            DueBox.Text = parsed.ToString("dd.MM.yyyy");
            return;
        }

        _working.DueDate = null;
        DueBox.Text = string.Empty;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ParseDue();

        _working.Title = (TitleBox.Text ?? string.Empty).Trim();
        _working.Text = TextBoxBody.Text ?? string.Empty;

        var category = (CategoryBox.Text ?? string.Empty).Trim();
        _working.Category = category.Length == 0 ? null : category;

        var completed = _working.IsCompletable && CompletedBox.IsChecked == true;
        if (completed != _working.IsCompleted)
        {
            _working.IsCompleted = completed;
            _working.CompletedAt = completed ? DateTimeOffset.Now : null;
        }

        // Items whose text was emptied are dropped rather than saved blank.
        _working.Items.RemoveAll(item => string.IsNullOrWhiteSpace(item.Text));

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // Only a card that was created empty and is still empty gets withdrawn.
        // Anything typed is kept even when the dialog is dismissed with Escape.
        IsAbandoned = _startedEmpty && IsBlank(Snapshot());
        DialogResult = false;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            Strings.NoteEdit_DeleteConfirm,
            Strings.Nav_Notes,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        WasDeleted = true;
        DialogResult = false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel_Click(this, new RoutedEventArgs());
            return;
        }

        // Ctrl+Enter saves from anywhere, including from inside the multi-line body
        // where plain Enter has to keep inserting a line.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Save_Click(this, new RoutedEventArgs());
        }
    }

    // ── Dictionary ───────────────────────────────────────────────────────────

    /// <summary>
    /// Adds «Открыть в словаре» to the note body's menu.
    /// </summary>
    /// <remarks>
    /// The same normalisation the editor and the reader use, so a word selected with
    /// its full stop attached still opens an article. Notes are a writing surface too;
    /// a lookup should not require going somewhere else to retype the word.
    /// </remarks>
    private void Body_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = new ContextMenu { Style = TryFindResource("WlContextMenu") as Style };

        var word = WordNavigation.Normalize(TextBoxBody.SelectedText);
        if (word is not null)
        {
            var lookup = new MenuItem
            {
                Header = string.Format(Strings.EditorAi_OpenInDictionary, word),
                Style = TryFindResource("WlMenuItem") as Style
            };

            lookup.Click += (_, _) =>
            {
                // The board owns the navigation; closing first means the dictionary is
                // not opened behind a modal dialog the user then has to dismiss.
                WordNavigationRequested?.Invoke(word);
                Save_Click(this, new RoutedEventArgs());
            };

            menu.Items.Add(lookup);
            menu.Items.Add(new Separator { Style = TryFindResource("WlMenuSeparator") as Style });
        }

        menu.Items.Add(Command(Strings.EditorAi_Cut, ApplicationCommands.Cut));
        menu.Items.Add(Command(Strings.EditorAi_Copy, ApplicationCommands.Copy));
        menu.Items.Add(Command(Strings.EditorAi_Paste, ApplicationCommands.Paste));

        TextBoxBody.ContextMenu = menu;
    }

    private MenuItem Command(string header, RoutedUICommand command) => new()
    {
        Header = header,
        Command = command,
        CommandTarget = TextBoxBody,
        Style = TryFindResource("WlMenuItem") as Style
    };

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private Note Snapshot()
    {
        var snapshot = Clone(_working);
        snapshot.Title = (TitleBox.Text ?? string.Empty).Trim();
        snapshot.Text = TextBoxBody.Text ?? string.Empty;
        return snapshot;
    }

    private static bool IsBlank(Note note) =>
        string.IsNullOrWhiteSpace(note.Title)
        && string.IsNullOrWhiteSpace(note.Text)
        && note.Items.Count == 0;

    private static Note Clone(Note source) => new()
    {
        Id = source.Id,
        Kind = source.Kind,
        Title = source.Title,
        Text = source.Text,
        Items = source.Items.Select(item => new NoteChecklistItem
        {
            Id = item.Id,
            Text = item.Text,
            IsDone = item.IsDone
        }).ToList(),
        Category = source.Category,
        IsPinned = source.IsPinned,
        IsCompleted = source.IsCompleted,
        CompletedAt = source.CompletedAt,
        DueDate = source.DueDate,
        CreatedAt = source.CreatedAt,
        ModifiedAt = source.ModifiedAt,
        Order = source.Order
    };
}
