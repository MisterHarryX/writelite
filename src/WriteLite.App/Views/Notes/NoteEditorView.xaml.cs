using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WriteLite.Resources;
using WriteLite.Services;
using WriteLite.Services.Ai;
using WriteLite.Services.Lexical;
using WriteLite.Services.Notes;
using Brush = System.Windows.Media.Brush;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using List = System.Windows.Documents.List;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using RichTextBox = System.Windows.Controls.RichTextBox;
using FontStyle = System.Windows.FontStyle;
using Separator = System.Windows.Controls.Separator;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Views.Notes;

/// <summary>
/// The note editor: a writing page inside the notes section, saved as you type.
/// </summary>
/// <remarks>
/// <para><b>Saving.</b> The editor holds its own copy of the note and hands a fresh clone to
/// <see cref="NotesService.Update"/> a moment after typing stops, on leaving, and when the
/// service is about to write its final save (<see cref="NotesService.Closing"/>). The
/// service stays the only thing that touches the file; the editor's debounce only decides
/// how often it is told.</para>
///
/// <para><b>The body</b> is a <see cref="RichTextBox"/> with embedded controls enabled, so
/// checklist boxes can be ticked and drawings drawn on in place. What it holds is converted
/// to and from <see cref="NoteBlock"/>s by <see cref="NoteDocumentMapper"/>; nothing about
/// the WPF element tree is ever persisted.</para>
/// </remarks>
public partial class NoteEditorView : UserControl
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(700);

    private readonly DispatcherTimer _saveTimer;
    private readonly EmbedFactory _embeds;

    private NotesService? _notes;
    private TextActionService? _ai;
    private Note _working = new();
    private bool _startedEmpty;
    private bool _loading;
    private bool _dirty;
    private bool _closed = true;
    private bool _syncingToolbar;

    public NoteEditorView()
    {
        InitializeComponent();

        _embeds = new EmbedFactory(this);
        _saveTimer = new DispatcherTimer { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) => Commit();

        DataObject.AddPastingHandler(Editor, OnPasting);
        Unloaded += (_, _) => Commit();
    }

    /// <summary>Raised once the editor has saved and wants to be taken off the page.</summary>
    public event Action<NoteEditorView>? Closed;

    /// <summary>Raised when a word from the note should be looked up in the dictionary.</summary>
    public event Action<string>? WordNavigationRequested;

    public string NoteId => _working.Id;

    public bool IsOpen => !_closed;

    /// <summary>The note as last handed to the store.</summary>
    public Note Working => _working;

    internal RichTextBox Body => Editor;

    // ── Opening and closing ──────────────────────────────────────────────────

    public void Open(NotesService notes, Note note, TextActionService? ai, string backLabel)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(note);

        if (!_closed)
        {
            Commit();
            Detach();
        }

        _notes = notes;
        _ai = ai;
        _working = note.Clone();
        _startedEmpty = IsBlank(note);
        _closed = false;
        _dirty = false;

        _notes.Closing += Commit;

        BackText.Text = backLabel;
        Render();

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            // A note opens at its top, the way it was written, with the caret at the start.
            PageScroll.ScrollToTop();
            if (string.IsNullOrWhiteSpace(_working.Title) && _startedEmpty)
            {
                TitleBox.Focus();
            }
            else
            {
                Editor.Focus();
                Editor.CaretPosition = Editor.Document.ContentStart;
                PageScroll.ScrollToTop();
            }
        }));
    }

    /// <summary>Saves, withdraws a note that was created and never written in, and leaves.</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _working.Items.RemoveAll(item => string.IsNullOrWhiteSpace(item.Text));
        _dirty = true;
        Commit();

        if (_startedEmpty && IsBlank(_working))
        {
            // A note created a moment ago and left without a word in it was a mis-click;
            // an empty card on the board is worse than no card.
            _notes?.Delete(_working.Id);
        }

        Detach();
        Closed?.Invoke(this);
    }

    private void Detach()
    {
        _saveTimer.Stop();
        if (_notes is not null)
        {
            _notes.Closing -= Commit;
        }

        _closed = true;
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    private void Render()
    {
        _loading = true;
        try
        {
            TitleBox.Text = _working.Title;
            TitlePlaceholder.Visibility = string.IsNullOrEmpty(_working.Title) ? Visibility.Visible : Visibility.Collapsed;
            CategoryBox.Text = _working.Category ?? string.Empty;
            DueBox.Text = FormatDue(_working);
            KindBox.SelectedIndex = _working.Kind switch
            {
                NoteKind.Task => 1,
                NoteKind.Checklist => 2,
                NoteKind.Goal => 3,
                NoteKind.Event => 4,
                _ => 0
            };
            CompletedBox.IsChecked = _working.IsCompleted;

            RenderProjects();
            RenderPin();
            RenderKindChrome();
            RenderItems();

            NoteDocumentMapper.Load(Editor.Document, NoteContent.BlocksOf(_working), _embeds);
            Editor.Document.FontSize = NoteDocumentMapper.BodyFontSize;
            Editor.Document.LineHeight = 24;
            RenderBookmarks();

            Controls.Type.SetTracked(
                CreatedText,
                string.Format(Strings.NoteEdit_CreatedModified, _working.CreatedAt, _working.ModifiedAt).ToUpperInvariant());
            Controls.Type.SetTracked(SaveStateText, Strings.NoteEditor_Saved);
        }
        finally
        {
            _loading = false;
        }

        // Opening a note is not an edit.
        Editor.CaretPosition = Editor.Document.ContentStart;
        _dirty = false;
    }

    private void RenderProjects()
    {
        ProjectBox.Items.Clear();
        ProjectBox.Items.Add(new ComboBoxItem { Content = Strings.Projects_None, Tag = null });

        var selected = 0;
        foreach (var project in _notes?.Projects ?? [])
        {
            ProjectBox.Items.Add(new ComboBoxItem { Content = project.DisplayName, Tag = project.Id });
            if (project.Id == _working.ProjectId)
            {
                selected = ProjectBox.Items.Count - 1;
            }
        }

        ProjectBox.SelectedIndex = selected;
    }

    private void RenderPin()
    {
        PinButton.ToolTip = _working.IsPinned ? Strings.NoteEdit_Unpin : Strings.NoteEdit_Pin;
        PinIcon.Stroke = (Brush)FindResource(_working.IsPinned ? "WlBrand" : "WlTextMuted");
    }

    private void RenderKindChrome()
    {
        Controls.Type.SetTracked(KindLabel, Controls.NoteCard.KindName(_working.Kind).ToUpperInvariant());

        var wantsItems = _working.Kind is NoteKind.Checklist or NoteKind.Goal;
        ChecklistSection.Visibility = wantsItems ? Visibility.Visible : Visibility.Collapsed;
        Controls.Type.SetTracked(
            ChecklistLabel,
            _working.Kind == NoteKind.Goal ? Strings.NoteEdit_TrackedSteps : Strings.NoteEdit_TrackedItems);
        CompletedBox.Visibility = _working.IsCompletable ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Saving ───────────────────────────────────────────────────────────────

    private void MarkDirty()
    {
        if (_loading || _closed)
        {
            return;
        }

        _dirty = true;
        Controls.Type.SetTracked(SaveStateText, Strings.NoteEditor_Saving);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Hands the current state to the store now, if anything changed.</summary>
    public void Commit()
    {
        _saveTimer.Stop();
        if (_closed || _notes is null || !_dirty)
        {
            return;
        }

        _dirty = false;

        try
        {
            var blocks = NoteDocumentMapper.Read(Editor.Document);
            _working.Content = blocks;
            _working.Text = NoteContent.ToPlainText(blocks);
            _working.Title = (TitleBox.Text ?? string.Empty).Trim();

            var category = (CategoryBox.Text ?? string.Empty).Trim();
            _working.Category = category.Length == 0 ? null : category;
            ParseDue(rewriteField: false);

            // A project deleted from the rail while this note was open is not a home.
            if (_working.ProjectId is not null && _notes.FindProject(_working.ProjectId) is null)
            {
                _working.ProjectId = null;
            }

            _notes.Update(_working.Clone());
            Controls.Type.SetTracked(
                SaveStateText,
                string.Format(Strings.NoteEditor_SavedAt, DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture)));
            RenderBookmarks();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // Reading the document failed half way; keep the edit pending rather than
            // overwrite the stored note with whatever was read before the failure.
            _dirty = true;
            CompatibilityLogger.Technical("note-editor-commit-failed", exception);
        }
    }

    private static bool IsBlank(Note note) =>
        string.IsNullOrWhiteSpace(note.Title)
        && string.IsNullOrWhiteSpace(note.Text)
        && note.Items.Count == 0
        && !(note.Content?.Any(block => !block.IsTextual || !string.IsNullOrEmpty(block.BookmarkId)) ?? false);

    // ── Details ──────────────────────────────────────────────────────────────

    private void Back_Click(object sender, RoutedEventArgs e) => Close();

    private void Title_TextChanged(object sender, TextChangedEventArgs e)
    {
        TitlePlaceholder.Visibility = string.IsNullOrEmpty(TitleBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        MarkDirty();
    }

    private void Title_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter in the title moves on to the body, as every notes app does.
        if (e.Key is Key.Enter or Key.Down)
        {
            e.Handled = true;
            Editor.Focus();
            Editor.CaretPosition = Editor.Document.ContentStart;
        }
    }

    private void Detail_TextChanged(object sender, TextChangedEventArgs e) => MarkDirty();

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsInitialized)
        {
            return;
        }

        _working.Kind = KindBox.SelectedIndex switch
        {
            1 => NoteKind.Task,
            2 => NoteKind.Checklist,
            3 => NoteKind.Goal,
            4 => NoteKind.Event,
            _ => NoteKind.Note
        };

        RenderKindChrome();
        MarkDirty();
    }

    private void Project_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProjectBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        _working.ProjectId = item.Tag as string;
        MarkDirty();
    }

    private void Completed_Click(object sender, RoutedEventArgs e)
    {
        var completed = _working.IsCompletable && CompletedBox.IsChecked == true;
        if (completed == _working.IsCompleted)
        {
            return;
        }

        _working.IsCompleted = completed;
        _working.CompletedAt = completed ? DateTimeOffset.Now : null;
        MarkDirty();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _working.IsPinned = !_working.IsPinned;
        RenderPin();
        MarkDirty();
    }

    private void Due_LostFocus(object sender, RoutedEventArgs e) => ParseDue(rewriteField: true);

    private static string FormatDue(Note note) => note.DueDate is not { } due
        ? string.Empty
        : note.IsAllDay || due.TimeOfDay == TimeSpan.Zero
            ? due.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
            : due.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Inside the three-pane notes page there is nowhere to go "back" to: the list is beside the
    /// note. The page sets this and the back button disappears.
    /// </summary>
    public bool Embedded
    {
        get => BackButton.Visibility != Visibility.Visible;
        set => BackButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>True while the open note is a fresh one with nothing written in it.</summary>
    public bool IsBlankNew => !_closed && _startedEmpty && IsBlank(_working);

    /// <summary>Reads the due date in the forms people type; anything unreadable clears it.</summary>
    private void ParseDue(bool rewriteField)
    {
        var raw = (DueBox.Text ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            _working.DueDate = null;
            return;
        }

        // A time after the date makes it an appointment: «12.11.2026 14:30».
        string[] withTime = ["dd.MM.yyyy HH:mm", "d.M.yyyy H:mm", "dd.MM.yy HH:mm", "d.M H:mm", "yyyy-MM-dd HH:mm"];
        if (DateTime.TryParseExact(raw, withTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timed))
        {
            _working.DueDate = new DateTimeOffset(timed);
            _working.IsAllDay = false;
            if (rewriteField) DueBox.Text = FormatDue(_working);
            return;
        }

        string[] formats = ["dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy", "dd/MM/yyyy", "yyyy-MM-dd", "d.M"];
        if (DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsed))
        {
            _working.DueDate = parsed.TimeOfDay == TimeSpan.Zero ? new DateTimeOffset(parsed.Date) : new DateTimeOffset(parsed);
            _working.IsAllDay = parsed.TimeOfDay == TimeSpan.Zero;
            if (rewriteField)
            {
                DueBox.Text = FormatDue(_working);
            }

            return;
        }

        if (rewriteField)
        {
            _working.DueDate = null;
            DueBox.Text = string.Empty;
        }
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            Style = TryFindResource("WlContextMenu") as Style,
            PlacementTarget = MoreButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        menu.Items.Add(Item(Strings.NoteCard_Duplicate, () =>
        {
            Commit();
            if (_notes?.Duplicate(_working.Id) is not null)
            {
                Controls.Type.SetTracked(SaveStateText, Strings.NoteEditor_Duplicated);
            }
        }));

        var move = new MenuItem { Header = Strings.Projects_MoveTo, Style = TryFindResource("WlMenuItem") as Style };
        move.Items.Add(Item(Strings.Projects_None, () => SelectProject(null), _working.ProjectId is null));
        foreach (var project in _notes?.Projects ?? [])
        {
            var id = project.Id;
            move.Items.Add(Item(project.DisplayName, () => SelectProject(id), project.Id == _working.ProjectId));
        }

        menu.Items.Add(move);
        menu.Items.Add(new Separator { Style = TryFindResource("WlMenuSeparator") as Style });
        menu.Items.Add(Item(Strings.NoteCard_Delete, DeleteNote));
        menu.IsOpen = true;
    }

    private void SelectProject(string? projectId)
    {
        foreach (var item in ProjectBox.Items.OfType<ComboBoxItem>())
        {
            if (Equals(item.Tag as string, projectId))
            {
                ProjectBox.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>
    /// Moves the note to «Недавно удалённые». No confirmation: nothing is lost for thirty days,
    /// and a question before every deletion trains people to click «Да» without reading.
    /// </summary>
    private void DeleteNote()
    {
        if (_notes is null) return;
        Commit();
        var notes = _notes;
        var id = _working.Id;
        Detach();
        notes.MoveToTrash(id);
        Closed?.Invoke(this);
    }

    private MenuItem Item(string header, Action action, bool isChecked = false)
    {
        var item = new MenuItem
        {
            Header = header,
            IsCheckable = false,
            IsChecked = isChecked,
            Style = TryFindResource("WlMenuItem") as Style
        };
        item.Click += (_, _) => action();
        return item;
    }

    // ── Checklist items (checklist and goal kinds) ───────────────────────────

    private void RenderItems()
    {
        ItemsPanel.Children.Clear();
        foreach (var item in _working.Items)
        {
            ItemsPanel.Children.Add(BuildItemRow(item));
        }

        RenderItemProgress();
    }

    private void RenderItemProgress() => Controls.Type.SetTracked(
        ChecklistProgress,
        _working.Items.Count == 0 ? string.Empty : $"{_working.DoneCount}/{_working.Items.Count}");

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
            Width = double.NaN,
            TextAlignment = TextAlignment.Left
        };
        AutomationProperties.SetName(text, Strings.NoteEdit_ItemTextAutomation);
        ApplyDoneStyling(text, item.IsDone);
        text.TextChanged += (_, _) =>
        {
            item.Text = text.Text;
            MarkDirty();
        };

        void Toggle(bool done)
        {
            item.IsDone = done;
            ApplyDoneStyling(text, done);
            RenderItemProgress();
            MarkDirty();
        }

        box.Checked += (_, _) => Toggle(true);
        box.Unchecked += (_, _) => Toggle(false);

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
            MarkDirty();
        };

        Grid.SetColumn(box, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(remove, 2);
        row.Children.Add(box);
        row.Children.Add(text);
        row.Children.Add(remove);
        return row;
    }

    private void ApplyDoneStyling(TextBox text, bool done)
    {
        text.Foreground = (Brush)FindResource(done ? "WlTextMuted" : "WlText");
        text.TextDecorations = done ? TextDecorations.Strikethrough : null;
    }

    private void AddItem_Click(object sender, RoutedEventArgs e) => AddItem();

    private void NewItem_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.SubmitPressed())
        {
            AddItem();
        }
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
        MarkDirty();
        NewItemBox.Focus();
    }

    // ── Body: change tracking and toolbar state ──────────────────────────────

    private void Editor_TextChanged(object sender, TextChangedEventArgs e) => MarkDirty();

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _syncingToolbar = true;
        try
        {
            var selection = Editor.Selection;
            BoldToggle.IsChecked = selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight weight
                                   && weight.ToOpenTypeWeight() >= 600
                                   && CurrentKind() is not (NoteBlockKind.Heading1 or NoteBlockKind.Heading2 or NoteBlockKind.Heading3);
            ItalicToggle.IsChecked = selection.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle style && style != FontStyles.Normal;
            var decorations = selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
            UnderlineToggle.IsChecked = decorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true;
            StrikeToggle.IsChecked = decorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true;
            HighlightToggle.IsChecked = selection.GetPropertyValue(TextElement.BackgroundProperty) is SolidColorBrush { Color.A: > 0 };

            BlockStyleBox.SelectedIndex = CurrentKind() switch
            {
                NoteBlockKind.Heading1 => 1,
                NoteBlockKind.Heading2 => 2,
                NoteBlockKind.Heading3 => 3,
                NoteBlockKind.Quote => 4,
                _ => 0
            };
        }
        finally
        {
            _syncingToolbar = false;
        }
    }

    private NoteBlockKind CurrentKind() =>
        Editor.CaretPosition.Paragraph?.Tag is NoteBlockTag tag ? tag.Kind : NoteBlockKind.Paragraph;

    // ── Body: formatting commands ────────────────────────────────────────────

    private void Bold_Click(object sender, RoutedEventArgs e) => Execute(EditingCommands.ToggleBold);

    private void Italic_Click(object sender, RoutedEventArgs e) => Execute(EditingCommands.ToggleItalic);

    private void Underline_Click(object sender, RoutedEventArgs e) => Execute(EditingCommands.ToggleUnderline);

    private void Execute(RoutedUICommand command)
    {
        command.Execute(null, Editor);
        Editor.Focus();
    }

    private void Strike_Click(object sender, RoutedEventArgs e)
    {
        var selection = Editor.Selection;
        var current = selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        var struck = current?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true;
        selection.ApplyPropertyValue(Inline.TextDecorationsProperty, struck ? null : TextDecorations.Strikethrough);
        Editor.Focus();
        MarkDirty();
    }

    private void Highlight_Click(object sender, RoutedEventArgs e)
    {
        var selection = Editor.Selection;
        var marked = selection.GetPropertyValue(TextElement.BackgroundProperty) is SolidColorBrush { Color.A: > 0 };
        selection.ApplyPropertyValue(TextElement.BackgroundProperty, marked ? null : NoteDocumentMapper.HighlightBrush);
        Editor.Focus();
        MarkDirty();
    }

    private void Bullets_Click(object sender, RoutedEventArgs e) => ToggleList(EditingCommands.ToggleBullets);

    private void Numbers_Click(object sender, RoutedEventArgs e) => ToggleList(EditingCommands.ToggleNumbering);

    private void ToggleList(RoutedUICommand command)
    {
        // A checklist line or heading becoming a list item drops its tick box and size
        // first; otherwise the list would carry a stray box and a 25-point bullet.
        foreach (var paragraph in SelectedParagraphs())
        {
            RemoveCheck(paragraph);
            SetKind(paragraph, NoteBlockKind.Paragraph);
        }

        Execute(command);
        MarkDirty();
    }

    private void BlockStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingToolbar || _loading || !IsInitialized)
        {
            return;
        }

        var kind = BlockStyleBox.SelectedIndex switch
        {
            1 => NoteBlockKind.Heading1,
            2 => NoteBlockKind.Heading2,
            3 => NoteBlockKind.Heading3,
            4 => NoteBlockKind.Quote,
            _ => NoteBlockKind.Paragraph
        };

        Editor.BeginChange();
        try
        {
            foreach (var paragraph in SelectedParagraphs().Where(p => p.Parent is not ListItem))
            {
                RemoveCheck(paragraph);
                SetKind(paragraph, kind);
            }
        }
        finally
        {
            Editor.EndChange();
        }

        Editor.Focus();
        MarkDirty();
    }

    private void Checklist_Click(object sender, RoutedEventArgs e)
    {
        var paragraphs = SelectedParagraphs().Where(p => p.Parent is not ListItem).ToList();
        if (paragraphs.Count == 0)
        {
            return;
        }

        var allChecks = paragraphs.All(p => NoteDocumentMapper.FindCheck(p) is not null);

        Editor.BeginChange();
        try
        {
            foreach (var paragraph in paragraphs)
            {
                if (allChecks)
                {
                    RemoveCheck(paragraph);
                }
                else
                {
                    AddCheck(paragraph);
                }
            }
        }
        finally
        {
            Editor.EndChange();
        }

        Editor.Focus();
        MarkDirty();
    }

    private IEnumerable<Paragraph> SelectedParagraphs()
    {
        var start = Editor.Selection.Start;
        var end = Editor.Selection.End;

        var all = NoteDocumentMapper.Paragraphs(Editor.Document.Blocks).ToList();
        var chosen = all
            .Where(p => p.ContentEnd.CompareTo(start) >= 0 && p.ContentStart.CompareTo(end) <= 0)
            .ToList();

        if (chosen.Count == 0 && Editor.CaretPosition.Paragraph is { } caret)
        {
            chosen.Add(caret);
        }

        return chosen;
    }

    private static void SetKind(Paragraph paragraph, NoteBlockKind kind)
    {
        var tag = paragraph.Tag as NoteBlockTag ?? new NoteBlockTag();
        tag.Kind = kind;
        paragraph.Tag = tag;
        NoteDocumentMapper.ApplyKindStyle(paragraph, kind);
        NoteDocumentMapper.ApplyBookmarkMarker(paragraph);
    }

    private void AddCheck(Paragraph paragraph)
    {
        if (NoteDocumentMapper.FindCheck(paragraph) is not null)
        {
            return;
        }

        SetKind(paragraph, NoteBlockKind.Check);
        var box = _embeds.CreateCheck(false);
        var inline = NoteDocumentMapper.CheckInline(box);
        if (paragraph.Inlines.FirstInline is { } first)
        {
            paragraph.Inlines.InsertBefore(first, inline);
        }
        else
        {
            paragraph.Inlines.Add(inline);
        }
    }

    private static void RemoveCheck(Paragraph paragraph)
    {
        if (paragraph.Inlines.FirstInline is InlineUIContainer { Child: CheckBox } box)
        {
            paragraph.Inlines.Remove(box);
            SetKind(paragraph, NoteBlockKind.Paragraph);
            NoteDocumentMapper.ApplyCheckStyle(paragraph, false);
        }
    }

    // ── Body: inserting blocks ───────────────────────────────────────────────

    private void Divider_Click(object sender, RoutedEventArgs e) =>
        InsertBlockAtCaret(NoteDocumentMapper.BuildBlock(new NoteBlock { Kind = NoteBlockKind.Divider }, _embeds));

    private void InsertSketch_Click(object sender, RoutedEventArgs e) =>
        InsertBlockAtCaret(NoteDocumentMapper.BuildBlock(
            new NoteBlock { Kind = NoteBlockKind.Sketch, Height = NoteSketchSurface.DefaultHeight },
            _embeds));

    private void InsertImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Strings.NoteEditor_InsertImageTooltip,
            Filter = Strings.NoteEditor_ImageFilter + "|*.png;*.jpg;*.jpeg;*.gif;*.bmp",
            Multiselect = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            foreach (var file in dialog.FileNames)
            {
                InsertImageFile(file);
            }
        }
    }

    internal bool InsertImageFile(string path)
    {
        if (_notes is null || !NoteAssetStore.SupportedExtensions.Contains(Path.GetExtension(path)))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > NoteAssetStore.MaxAssetBytes)
            {
                throw new InvalidOperationException(Strings.NoteEditor_ImageTooLarge);
            }

            var id = _notes.Assets.Import(_working.Id, File.ReadAllBytes(path), Path.GetExtension(path));
            InsertImageBlock(id);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ShowProblem(exception is InvalidOperationException ? exception.Message : Strings.NoteEditor_ImageFailed);
            return false;
        }
    }

    private bool InsertImageBitmap(BitmapSource bitmap)
    {
        if (_notes is null)
        {
            return false;
        }

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);

            var id = _notes.Assets.Import(_working.Id, stream.ToArray(), ".png");
            InsertImageBlock(id);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            ShowProblem(Strings.NoteEditor_ImageFailed);
            return false;
        }
    }

    private void InsertImageBlock(string assetId) =>
        InsertBlockAtCaret(NoteDocumentMapper.BuildBlock(new NoteBlock { Kind = NoteBlockKind.Image, AssetId = assetId }, _embeds));

    /// <summary>
    /// Puts a block on its own line at the caret, leaving a paragraph after it to keep typing in.
    /// </summary>
    private void InsertBlockAtCaret(Block block)
    {
        var document = Editor.Document;
        var anchor = Editor.CaretPosition.Paragraph is { } paragraph ? TopLevel(paragraph) : document.Blocks.LastBlock;

        Editor.BeginChange();
        try
        {
            if (anchor is null)
            {
                document.Blocks.Add(block);
            }
            else if (anchor is Paragraph empty && NoteDocumentMapper.FindCheck(empty) is null
                     && string.IsNullOrWhiteSpace(new TextRange(empty.ContentStart, empty.ContentEnd).Text))
            {
                // On an empty line the block takes the line's place in front of it, and the
                // empty line stays as the place to continue typing.
                document.Blocks.InsertBefore(anchor, block);
            }
            else
            {
                document.Blocks.InsertAfter(anchor, block);
            }

            if (block.NextBlock is not Paragraph)
            {
                var next = NoteDocumentMapper.BuildParagraph(NoteBlock.Paragraph(string.Empty), _embeds);
                document.Blocks.InsertAfter(block, next);
            }

            if (block.NextBlock is Paragraph after)
            {
                Editor.CaretPosition = after.ContentStart;
            }
        }
        finally
        {
            Editor.EndChange();
        }

        Editor.Focus();
        MarkDirty();
    }

    /// <summary>The block directly in the document that contains a paragraph — the list, for a list item.</summary>
    private static Block TopLevel(Block block)
    {
        var current = block;
        while (current.Parent is not FlowDocument)
        {
            switch (current.Parent)
            {
                case ListItem item when item.List is not null:
                    current = item.List;
                    break;
                case Block parent:
                    current = parent;
                    break;
                default:
                    return current;
            }
        }

        return current;
    }

    private void RemoveBlock(Block block)
    {
        Editor.BeginChange();
        try
        {
            block.SiblingBlocks?.Remove(block);
            if (Editor.Document.Blocks.Count == 0)
            {
                Editor.Document.Blocks.Add(NoteDocumentMapper.BuildParagraph(NoteBlock.Paragraph(string.Empty), _embeds));
            }
        }
        finally
        {
            Editor.EndChange();
        }

        MarkDirty();
    }

    // ── Body: paste and drop ─────────────────────────────────────────────────

    /// <summary>
    /// Pictures pasted or dropped into a note are stored with it.
    /// </summary>
    /// <remarks>
    /// Only when the clipboard holds a picture and no text. Word and browsers put a bitmap
    /// rendering beside the text they copy; taking the bitmap whenever one is present would
    /// paste a screenshot of a paragraph instead of the paragraph.
    /// </remarks>
    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        var data = e.DataObject;

        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
            && files.Any(file => NoteAssetStore.SupportedExtensions.Contains(Path.GetExtension(file))))
        {
            e.CancelCommand();
            foreach (var file in files)
            {
                InsertImageFile(file);
            }

            return;
        }

        var hasText = data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text)
                      || data.GetDataPresent(DataFormats.Rtf);
        if (!hasText && data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            e.CancelCommand();
            InsertImageBitmap(bitmap);
            return;
        }

        if (!hasText && Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
        {
            e.CancelCommand();
            InsertImageBitmap(image);
        }
    }

    private void Editor_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void Editor_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        e.Handled = true;
        if (Editor.GetPositionFromPoint(e.GetPosition(Editor), snapToText: true) is { } position)
        {
            Editor.CaretPosition = position;
        }

        foreach (var file in files.Where(file => NoteAssetStore.SupportedExtensions.Contains(Path.GetExtension(file))))
        {
            InsertImageFile(file);
        }
    }

    // ── Body: keyboard ───────────────────────────────────────────────────────

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _dirty = true;
            Commit();
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = HandleEnter();
            return;
        }

        if (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None && Editor.Selection.IsEmpty
            && Editor.CaretPosition.Paragraph is { } paragraph
            && NoteDocumentMapper.FindCheck(paragraph) is not null
            && Editor.CaretPosition.CompareTo(NoteDocumentMapper.TextStart(paragraph)) <= 0)
        {
            // Backspace at the start of a checklist line takes the box away, not the line.
            e.Handled = true;
            RemoveCheck(paragraph);
            Editor.CaretPosition = paragraph.ContentStart;
            MarkDirty();
        }
    }

    /// <summary>
    /// Splits a paragraph the way a notes app does, which is not quite what WPF does.
    /// </summary>
    /// <remarks>
    /// WPF copies every local value of a paragraph into both halves of a split — its tag
    /// included — so a bookmark would be duplicated on every Enter, a heading would spawn
    /// headings, and a checklist line would spawn a line without a box. Lists are left to
    /// WPF, which already continues them correctly.
    /// </remarks>
    private bool HandleEnter()
    {
        var paragraph = Editor.CaretPosition.Paragraph;
        if (paragraph is null || paragraph.Parent is ListItem)
        {
            return false;
        }

        var tag = paragraph.Tag as NoteBlockTag;
        var check = NoteDocumentMapper.FindCheck(paragraph);
        if (tag is null && check is null)
        {
            return false;
        }

        Editor.BeginChange();
        try
        {
            if (!Editor.Selection.IsEmpty)
            {
                Editor.Selection.Text = string.Empty;
            }

            var text = NoteDocumentMapper.TextOf(paragraph);

            // Enter on an empty checklist line ends the checklist, like an empty list item.
            if (check is not null && string.IsNullOrWhiteSpace(text))
            {
                RemoveCheck(paragraph);
                return true;
            }

            var position = Editor.CaretPosition;
            var textStart = NoteDocumentMapper.TextStart(paragraph);
            if (position.CompareTo(textStart) < 0)
            {
                position = textStart;
            }

            var kind = tag?.Kind ?? NoteBlockKind.Paragraph;
            var continues = check is not null ? NoteBlockKind.Check : kind == NoteBlockKind.Quote ? NoteBlockKind.Quote : NoteBlockKind.Paragraph;

            // Enter at the very start of a heading pushes it down instead of demoting it.
            if (position.CompareTo(textStart) == 0 && text.Length > 0 && check is null)
            {
                var before = NoteDocumentMapper.BuildParagraph(NoteBlock.Paragraph(string.Empty, continues), _embeds);
                paragraph.SiblingBlocks!.InsertBefore(paragraph, before);
                Editor.CaretPosition = NoteDocumentMapper.TextStart(paragraph);
                return true;
            }

            var next = position.InsertParagraphBreak();
            if (next.Paragraph is not { } created)
            {
                return true;
            }

            if (created.Inlines.FirstInline is InlineUIContainer { Child: CheckBox } stray)
            {
                created.Inlines.Remove(stray);
            }

            created.Tag = new NoteBlockTag { Kind = continues };
            NoteDocumentMapper.ApplyKindStyle(created, continues);
            NoteDocumentMapper.ApplyBookmarkMarker(created);

            if (continues == NoteBlockKind.Check)
            {
                var box = NoteDocumentMapper.CheckInline(_embeds.CreateCheck(false));
                if (created.Inlines.FirstInline is { } first)
                {
                    created.Inlines.InsertBefore(first, box);
                }
                else
                {
                    created.Inlines.Add(box);
                }

                NoteDocumentMapper.ApplyCheckStyle(created, false);
            }

            Editor.CaretPosition = NoteDocumentMapper.TextStart(created);
            return true;
        }
        finally
        {
            Editor.EndChange();
            MarkDirty();
        }
    }

    /// <summary>
    /// Markdown-style shortcuts at the start of a line: «- » and «* » start a list, «1. » a
    /// numbered one, «[] » a checklist line, «# » to «### » headings, «&gt; » a quote.
    /// </summary>
    private void Editor_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text != " " || !Editor.Selection.IsEmpty || Editor.CaretPosition.Paragraph is not { } paragraph
            || paragraph.Parent is ListItem || NoteDocumentMapper.FindCheck(paragraph) is not null)
        {
            return;
        }

        var typed = new TextRange(paragraph.ContentStart, Editor.CaretPosition).Text;
        Action? apply = typed switch
        {
            "-" or "*" or "•" => () => Execute(EditingCommands.ToggleBullets),
            "1." or "1)" => () => Execute(EditingCommands.ToggleNumbering),
            "[]" or "[ ]" => () => AddCheck(paragraph),
            "[x]" or "[х]" => () =>
            {
                AddCheck(paragraph);
                if (NoteDocumentMapper.FindCheck(paragraph) is { } box)
                {
                    box.IsChecked = true;
                }
            },
            "#" => () => SetKind(paragraph, NoteBlockKind.Heading1),
            "##" => () => SetKind(paragraph, NoteBlockKind.Heading2),
            "###" => () => SetKind(paragraph, NoteBlockKind.Heading3),
            ">" => () => SetKind(paragraph, NoteBlockKind.Quote),
            _ => null
        };

        if (apply is null)
        {
            return;
        }

        e.Handled = true;
        Editor.BeginChange();
        try
        {
            new TextRange(paragraph.ContentStart, Editor.CaretPosition).Text = string.Empty;
            apply();
        }
        finally
        {
            Editor.EndChange();
        }

        MarkDirty();
    }

    // ── Bookmarks ────────────────────────────────────────────────────────────

    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.CaretPosition.Paragraph is { } paragraph)
        {
            ToggleBookmark(paragraph);
        }
    }

    private void ToggleBookmark(Block block)
    {
        var tag = block.Tag as NoteBlockTag ?? new NoteBlockTag();
        block.Tag = tag;

        if (string.IsNullOrEmpty(tag.BookmarkId))
        {
            tag.BookmarkId = Guid.NewGuid().ToString("N");
            tag.BookmarkName = null;
        }
        else
        {
            tag.BookmarkId = null;
            tag.BookmarkName = null;
        }

        NoteDocumentMapper.ApplyBookmarkMarker(block);
        _dirty = true;
        Commit();
        Editor.Focus();
    }

    private void RenderBookmarks()
    {
        BookmarksList.Children.Clear();

        var marks = NoteContent.Bookmarks(new Note
        {
            Id = _working.Id,
            Title = _working.Title,
            Content = _working.Content ?? NoteDocumentMapper.Read(Editor.Document)
        }).ToList();

        BookmarksPanel.Visibility = marks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var mark in marks)
        {
            var button = new System.Windows.Controls.Button
            {
                Style = TryFindResource("WlNavButton") as Style,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                Margin = new Thickness(-8, 0, 0, 2),
                Content = new TextBlock { Text = mark.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                ToolTip = mark.Name
            };
            AutomationProperties.SetName(button, string.Format(Strings.NoteEditor_GoToBookmark, mark.Name));

            var id = mark.BookmarkId;
            button.Click += (_, _) => RevealBookmark(id);
            button.ContextMenu = BookmarkMenu(id, mark.Name);
            BookmarksList.Children.Add(button);
        }
    }

    private ContextMenu BookmarkMenu(string bookmarkId, string name)
    {
        var menu = new ContextMenu { Style = TryFindResource("WlContextMenu") as Style };
        menu.Items.Add(Item(Strings.NoteEditor_RenameBookmark, () =>
        {
            var prompt = new TextPromptWindow(Strings.NoteEditor_RenameBookmark, name) { Owner = Window.GetWindow(this) };
            if (prompt.ShowDialog() == true && FindBookmarkBlock(bookmarkId) is { Tag: NoteBlockTag tag })
            {
                tag.BookmarkName = string.IsNullOrWhiteSpace(prompt.Value) ? null : prompt.Value;
                _dirty = true;
                Commit();
            }
        }));
        menu.Items.Add(Item(Strings.NoteEditor_RemoveBookmark, () =>
        {
            if (FindBookmarkBlock(bookmarkId) is { } block)
            {
                ToggleBookmark(block);
            }
        }));
        return menu;
    }

    private Block? FindBookmarkBlock(string bookmarkId) =>
        NoteDocumentMapper.BookmarkableBlocks(Editor.Document.Blocks)
            .FirstOrDefault(block => block.Tag is NoteBlockTag tag && tag.BookmarkId == bookmarkId);

    /// <summary>Scrolls to a bookmarked block and puts the caret on it.</summary>
    public void RevealBookmark(string bookmarkId)
    {
        if (FindBookmarkBlock(bookmarkId) is not { } block)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            Editor.Focus();
            Editor.CaretPosition = block is Paragraph paragraph ? NoteDocumentMapper.TextStart(paragraph) : block.ContentStart;

            var rect = block.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (!rect.IsEmpty && Editor.IsVisible && PageScroll.IsVisible)
            {
                var top = Editor.TranslatePoint(new System.Windows.Point(0, rect.Top), PageScroll).Y;
                PageScroll.ScrollToVerticalOffset(Math.Max(0, PageScroll.VerticalOffset + top - 80));
            }
        }));
    }

    // ── AI ───────────────────────────────────────────────────────────────────

    private void Ai_Click(object sender, RoutedEventArgs e)
    {
        var menu = AiActionRunner.BuildMenu(this, ScopeLabel(), RunAi);
        menu.PlacementTarget = AiButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private bool HasTextSelection => !Editor.Selection.IsEmpty && !string.IsNullOrWhiteSpace(Editor.Selection.Text);

    private string ScopeLabel() => HasTextSelection ? Strings.AiAction_ScopeSelection : Strings.AiAction_ScopeNote;

    /// <summary>
    /// Runs an AI action on the selection, or on the whole note when nothing is selected.
    /// </summary>
    internal void RunAi(RewriteOperation operation)
    {
        if (_ai is null)
        {
            ShowProblem(WriteAiStatus.DescribeWithHint(WriteAiState.Unavailable));
            return;
        }

        var document = Editor.Document;
        var selected = HasTextSelection;
        var pieces = (selected
                ? RichTextPieces.FromSelection(document, Editor.Selection.Start, Editor.Selection.End)
                : RichTextPieces.WholeDocument(document))
            .Where(piece => piece.Text.Trim().Length > 0)
            .ToList();

        if (pieces.Count == 0)
        {
            ShowProblem(Strings.NoteEditor_NothingToProcess);
            return;
        }

        Commit();
        var outcome = AiActionRunner.Run(
            Window.GetWindow(this),
            _ai,
            new TextActionRequest(operation, pieces.Select(piece => piece.Text).ToList()));

        if (outcome is null)
        {
            return;
        }

        ApplyAi(outcome, pieces);
    }

    private void ApplyAi(AiActionOutcome outcome, IReadOnlyList<RichTextPiece> pieces)
    {
        var result = outcome.Result;

        Editor.BeginChange();
        var stale = 0;
        try
        {
            if (outcome.Outcome == AiPreviewOutcome.Replace && !result.IsAdvisory)
            {
                for (var index = 0; index < pieces.Count && index < result.Suggestions.Count; index++)
                {
                    if (!RichTextPieces.Apply(pieces[index], result.Suggestions[index]))
                    {
                        stale++;
                    }
                }
            }
            else
            {
                InsertBelow(pieces[^1].Paragraph, AnswerBlocks(result));
            }
        }
        finally
        {
            Editor.EndChange();
        }

        if (stale > 0)
        {
            ShowProblem(Strings.AiAction_TextChanged);
        }

        Editor.Focus();
        MarkDirty();
    }

    /// <summary>An answer as note blocks: key points as a list under a heading, an explanation as paragraphs.</summary>
    private static List<NoteBlock> AnswerBlocks(TextActionResult result)
    {
        var blocks = new List<NoteBlock>();
        var lines = result.Suggestion.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        switch (result.Operation)
        {
            case RewriteOperation.KeyPoints:
                blocks.Add(NoteBlock.Paragraph(Strings.AiAction_KeyPointsHeading, NoteBlockKind.Heading3));
                blocks.AddRange(lines.Select(line => NoteBlock.Paragraph(line.TrimStart('—', '–', '-', '•', ' '), NoteBlockKind.Bullet)));
                break;

            case RewriteOperation.Explain:
                blocks.Add(NoteBlock.Paragraph(Strings.AiAction_ExplainHeading, NoteBlockKind.Heading3));
                blocks.AddRange(lines.Select(line => NoteBlock.Paragraph(line)));
                break;

            default:
                blocks.AddRange(lines.Select(line => NoteBlock.Paragraph(line)));
                break;
        }

        return blocks;
    }

    private void InsertBelow(Paragraph after, IReadOnlyList<NoteBlock> blocks)
    {
        var scratch = new FlowDocument();
        NoteDocumentMapper.Load(scratch, blocks, _embeds);

        Block anchor = TopLevel(after);
        foreach (var block in scratch.Blocks.ToList())
        {
            scratch.Blocks.Remove(block);
            Editor.Document.Blocks.InsertAfter(anchor, block);
            anchor = block;
        }

        Editor.CaretPosition = anchor.ContentEnd;
    }

    // ── Context menu ─────────────────────────────────────────────────────────

    private void Editor_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = new ContextMenu { Style = TryFindResource("WlContextMenu") as Style };

        var word = WordNavigation.Normalize(Editor.Selection.Text);
        if (word is not null)
        {
            menu.Items.Add(Item(string.Format(Strings.EditorAi_OpenInDictionary, word), () =>
            {
                Commit();
                WordNavigationRequested?.Invoke(word);
            }));
            menu.Items.Add(new Separator { Style = TryFindResource("WlMenuSeparator") as Style });
        }

        var ai = new MenuItem { Header = "WriteAI", Style = TryFindResource("WlMenuItem") as Style };
        foreach (var operation in AiActionRunner.CoreActions)
        {
            var captured = operation;
            ai.Items.Add(Item(AiActionRunner.Label(operation), () => RunAi(captured)));
        }

        menu.Items.Add(ai);
        menu.Items.Add(Item(Strings.NoteEditor_BookmarkTooltip, () => Bookmark_Click(this, new RoutedEventArgs())));
        menu.Items.Add(new Separator { Style = TryFindResource("WlMenuSeparator") as Style });
        menu.Items.Add(CommandItem(Strings.EditorAi_Cut, ApplicationCommands.Cut));
        menu.Items.Add(CommandItem(Strings.EditorAi_Copy, ApplicationCommands.Copy));
        menu.Items.Add(CommandItem(Strings.EditorAi_Paste, ApplicationCommands.Paste));

        Editor.ContextMenu = menu;
    }

    private MenuItem CommandItem(string header, RoutedUICommand command) => new()
    {
        Header = header,
        Command = command,
        CommandTarget = Editor,
        Style = TryFindResource("WlMenuItem") as Style
    };

    private void ShowProblem(string message) =>
        MessageBox.Show(Window.GetWindow(this), message, Strings.Nav_Notes, MessageBoxButton.OK, MessageBoxImage.Information);

    // ── Embedded parts ───────────────────────────────────────────────────────

    /// <summary>Builds pictures, drawings and tick boxes, and wires each back to the autosave.</summary>
    private sealed class EmbedFactory(NoteEditorView view) : INoteEmbedFactory
    {
        public UIElement CreateImage(NoteBlock block) => view.BuildImage(block);

        public NoteSketchSurface CreateSketch(NoteBlock block)
        {
            var sketch = new NoteSketchSurface(block.Ink, block.Height);
            sketch.Changed += view.MarkDirty;
            sketch.RemoveRequested += surface =>
            {
                if (LogicalTreeHelper.GetParent(surface) is BlockUIContainer container)
                {
                    view.RemoveBlock(container);
                }
            };
            return sketch;
        }

        public CheckBox CreateCheck(bool isChecked)
        {
            var box = new CheckBox
            {
                IsChecked = isChecked,
                Margin = new Thickness(0, 0, 8, -3),
                Focusable = false,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            box.SetResourceReference(StyleProperty, "WlNoteCheckBox");
            AutomationProperties.SetName(box, Strings.NoteEditor_CheckAutomation);

            void Toggled(object sender, RoutedEventArgs e)
            {
                if (LogicalTreeHelper.GetParent(box) is InlineUIContainer { Parent: Paragraph paragraph })
                {
                    NoteDocumentMapper.ApplyCheckStyle(paragraph, box.IsChecked == true);
                }

                view.MarkDirty();
            }

            box.Checked += Toggled;
            box.Unchecked += Toggled;
            return box;
        }
    }

    private FrameworkElement BuildImage(NoteBlock block)
    {
        var path = _notes?.Assets.PathOf(_working.Id, block.AssetId);
        if (path is null || LoadBitmap(path) is not { } bitmap)
        {
            var missing = new Border
            {
                Padding = new Thickness(14, 10, 14, 10),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = Strings.NoteEditor_ImageMissing }
            };
            missing.SetResourceReference(Border.BorderBrushProperty, "WlLine");
            missing.SetResourceReference(Border.BackgroundProperty, "WlBgSoft");
            ((TextBlock)missing.Child).SetResourceReference(TextBlock.ForegroundProperty, "WlTextMuted");
            return missing;
        }

        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            MaxHeight = 760,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = Strings.NoteEditor_ImageTooltip
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(image, Strings.NoteEditor_ImageAutomation);

        if (block.Width is double width)
        {
            image.Width = width;
        }

        var frame = new Border { Child = image, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };

        image.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                OpenExternally(path);
            }
        };

        frame.ContextMenuOpening += (_, _) => frame.ContextMenu = ImageMenu(frame, image, path);
        frame.ContextMenu = ImageMenu(frame, image, path);
        return frame;
    }

    private ContextMenu ImageMenu(FrameworkElement frame, Image image, string path)
    {
        var menu = new ContextMenu { Style = TryFindResource("WlContextMenu") as Style };

        void Resize(double? width)
        {
            if (LogicalTreeHelper.GetParent(frame) is BlockUIContainer { Tag: NoteBlockTag tag })
            {
                tag.Width = width;
            }

            if (width is double value)
            {
                image.Width = value;
            }
            else
            {
                image.ClearValue(WidthProperty);
            }

            MarkDirty();
        }

        menu.Items.Add(Item(Strings.NoteEditor_ImageSmall, () => Resize(240)));
        menu.Items.Add(Item(Strings.NoteEditor_ImageMedium, () => Resize(440)));
        menu.Items.Add(Item(Strings.NoteEditor_ImageFull, () => Resize(null)));
        menu.Items.Add(new Separator { Style = TryFindResource("WlMenuSeparator") as Style });
        menu.Items.Add(Item(Strings.NoteEditor_ImageOpen, () => OpenExternally(path)));
        menu.Items.Add(Item(Strings.EditorAi_Copy, () =>
        {
            try
            {
                if (image.Source is BitmapSource source)
                {
                    Clipboard.SetImage(source);
                }
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // The clipboard is held by another process; nothing to report beyond not copying.
            }
        }));
        menu.Items.Add(Item(Strings.NoteEditor_BookmarkTooltip, () =>
        {
            if (LogicalTreeHelper.GetParent(frame) is BlockUIContainer container)
            {
                ToggleBookmark(container);
            }
        }));
        menu.Items.Add(new Separator { Style = TryFindResource("WlMenuSeparator") as Style });
        menu.Items.Add(Item(Strings.NoteEditor_ImageRemove, () =>
        {
            if (LogicalTreeHelper.GetParent(frame) is BlockUIContainer container)
            {
                RemoveBlock(container);
            }
        }));
        return menu;
    }

    /// <summary>
    /// Loads a picture without locking its file, decoded no larger than it will be shown.
    /// </summary>
    private static BitmapSource? LoadBitmap(string path)
    {
        try
        {
            int pixelWidth;
            using (var stream = File.OpenRead(path))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                pixelWidth = decoder.Frames[0].PixelWidth;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            if (pixelWidth > 2000)
            {
                bitmap.DecodePixelWidth = 2000;
            }

            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or UnauthorizedAccessException
                                              or ArgumentException or InvalidOperationException
                                              or System.Runtime.InteropServices.COMException)
        {
            CompatibilityLogger.Technical("note-image-load-failed", exception.GetType().Name);
            return null;
        }
    }

    private static void OpenExternally(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            CompatibilityLogger.Technical("note-image-open-failed", exception.GetType().Name);
        }
    }
}
