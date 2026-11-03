using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WriteLite.Resources;
using WriteLite.Services;
using WriteLite.Services.Ai;
using WriteLite.Services.Notes;
using WriteLite.Views.Notes;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using UserControl = System.Windows.Controls.UserControl;
using DragDropEffects = System.Windows.DragDropEffects;
using TextBox = System.Windows.Controls.TextBox;
using DragDrop = System.Windows.DragDrop;

namespace WriteLite.Views.Pages;

/// <summary>The folder the list shows.</summary>
public enum NotesFolderKind
{
    All,
    Pinned,
    Tasks,
    Calendar,
    Completed,
    Project,
    Trash,
}

/// <summary>
/// «Заметки»: folders, the notes of a folder grouped by date, and the open note side by side.
/// </summary>
/// <remarks>
/// <para>Modelled on Apple Notes because that is the layout people already know how to use: one
/// click opens a note beside the list, a new note is one key (Ctrl+N) away, and deleting puts the
/// note in «Недавно удалённые» for thirty days instead of asking «Вы уверены?». The note editor
/// is the same block editor as before — text, headings, checklists, pictures and drawings — now
/// embedded rather than navigated to.</para>
///
/// <para>Notes are grouped the way the eye scans them: pinned first, then «Сегодня», «Вчера»,
/// «Последние 7 дней», «Последние 30 дней», then by month. Each row shows the title, the date and
/// the first line of text, so a note can be found without opening it.</para>
/// </remarks>
public partial class NotesPage : UserControl
{
    private NotesService? _notes;
    private TextActionService? _ai;
    private Action? _changedHandler;
    private NotesFolderKind _folder = NotesFolderKind.All;
    private string? _projectId;
    private string _search = string.Empty;
    private NotesSort _sort = NotesSort.Modified;
    private string? _selectedId;
    private bool _refreshQueued;
    private bool _switching;

    public NotesPage()
    {
        InitializeComponent();
        EditorView.Embedded = true;
        EditorView.Closed += OnEditorClosed;
        EditorView.WordNavigationRequested += word => WordNavigationRequested?.Invoke(word);
        CalendarView.OpenRequested += note => OpenFromCalendar(note);
        BuildSmartFolders();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    public event Action<string>? WordNavigationRequested;

    /// <summary>The scope new notes are created in (kept for callers of the previous page).</summary>
    public NotesScope Scope => _folder == NotesFolderKind.Project && _projectId is not null
        ? NotesScope.Project(_projectId)
        : NotesScope.Everything;

    internal NoteEditorView Editor => EditorView;

    internal NotesFolderKind Folder => _folder;

    public void Bind(NotesService notes)
    {
        if (_notes is not null && _changedHandler is not null)
        {
            _notes.Changed -= _changedHandler;
        }

        _notes = notes;
        _changedHandler = QueueRefresh;
        _notes.Changed += _changedHandler;
        _notes.PurgeExpiredTrash();
        CalendarView.Bind(notes);
        Reload();
    }

    public void BindAi(TextActionService? ai) => _ai = ai;

    public void Reload() => Refresh();

    public void CommitPendingEdits() => EditorView.Commit();

    // ── Folders ────────────────────────────────────────────────────────────────

    private readonly Dictionary<NotesFolderKind, (RadioButton Button, TextBlock Count)> _smart = [];

    private void BuildSmartFolders()
    {
        AddSmart(NotesFolderKind.All, "Все заметки", "WlIconNotes", isChecked: true);
        AddSmart(NotesFolderKind.Pinned, "Закреплённые", "WlIconPin");
        AddSmart(NotesFolderKind.Tasks, "Задачи", "WlIconTask");
        AddSmart(NotesFolderKind.Calendar, "Календарь", "WlIconCalendar");
        AddSmart(NotesFolderKind.Completed, "Выполненные", "WlIconCheck");
    }

    private void AddSmart(NotesFolderKind kind, string title, string icon, bool isChecked = false)
    {
        var (button, count) = FolderButton(title, icon, kind.ToString());
        button.IsChecked = isChecked;
        _smart[kind] = (button, count);
        SmartFolders.Children.Add(button);
    }

    private (RadioButton Button, TextBlock Count) FolderButton(string title, string icon, string tag)
    {
        var count = new TextBlock { Style = (Style)FindResource("WlMonoLabel"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var path = new System.Windows.Shapes.Path { Style = (Style)FindResource("WlIconSm") };
        path.SetResourceReference(System.Windows.Shapes.Path.DataProperty, icon);
        var label = new TextBlock { Text = title, Margin = new Thickness(9, 0, 30, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(path);
        row.Children.Add(label);
        var grid = new Grid();
        grid.Children.Add(row);
        grid.Children.Add(count);
        var button = new RadioButton
        {
            GroupName = "NotesFolders",
            Style = (Style)FindResource("FolderItem"),
            Content = grid,
            Tag = tag,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, title);
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
            new System.Windows.Data.Binding("Foreground") { Source = button });
        button.Checked += Folder_Checked;
        return (button, count);
    }

    private void Folder_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        if (tag == "trash")
        {
            SelectFolder(NotesFolderKind.Trash, null);
        }
        else if (tag.StartsWith("project:", StringComparison.Ordinal))
        {
            SelectFolder(NotesFolderKind.Project, tag["project:".Length..]);
        }
        else if (Enum.TryParse<NotesFolderKind>(tag, out var kind))
        {
            SelectFolder(kind, null);
        }
    }

    internal void SelectFolder(NotesFolderKind kind, string? projectId)
    {
        if (_folder == kind && _projectId == projectId) return;
        _folder = kind;
        _projectId = projectId;
        var calendar = kind == NotesFolderKind.Calendar;
        CalendarView.Visibility = calendar ? Visibility.Visible : Visibility.Collapsed;
        ListPane.Visibility = calendar ? Visibility.Collapsed : Visibility.Visible;
        DetailPane.Visibility = calendar ? Visibility.Collapsed : Visibility.Visible;
        if (calendar)
        {
            EditorView.Commit();
            CalendarView.Refresh();
            return;
        }

        Refresh();
        ListScroll.ScrollToHome();
    }

    private void RenderFolders()
    {
        if (_notes is null) return;
        var all = _notes.All;
        var live = all.Where(n => !n.IsDeleted).ToList();
        _smart[NotesFolderKind.All].Count.Text = Count(live.Count);
        _smart[NotesFolderKind.Pinned].Count.Text = Count(live.Count(n => n.IsPinned));
        _smart[NotesFolderKind.Tasks].Count.Text = Count(live.Count(IsOpenTask));
        _smart[NotesFolderKind.Calendar].Count.Text = Count(live.Count(n => n.DueDate is { } d && d.Date == DateTime.Today && !n.IsCompleted));
        _smart[NotesFolderKind.Completed].Count.Text = Count(live.Count(n => n.IsCompleted));
        TrashCount.Text = Count(all.Count(n => n.IsDeleted));
        _smart[NotesFolderKind.Pinned].Button.Visibility = live.Any(n => n.IsPinned) || _folder == NotesFolderKind.Pinned
            ? Visibility.Visible
            : Visibility.Collapsed;

        FolderList.Children.Clear();
        foreach (var project in _notes.Projects)
        {
            var (button, count) = FolderButton(project.DisplayName, "WlIconFolder", "project:" + project.Id);
            count.Text = Count(live.Count(n => n.ProjectId == project.Id));
            button.IsChecked = _folder == NotesFolderKind.Project && _projectId == project.Id;
            button.ContextMenu = FolderMenu(project);
            button.AllowDrop = true;
            button.DragOver += (_, e) => e.Effects = e.Data.GetDataPresent("WriteLiteNoteId") ? DragDropEffects.Move : DragDropEffects.None;
            button.Drop += (_, e) =>
            {
                if (e.Data.GetData("WriteLiteNoteId") is string id) _notes.MoveNote(id, project.Id);
            };
            FolderList.Children.Add(button);
        }
    }

    private static string Count(int n) => n == 0 ? string.Empty : n.ToString(CultureInfo.InvariantCulture);

    private static bool IsOpenTask(Note n) =>
        !n.IsCompleted && n.Kind is NoteKind.Task or NoteKind.Checklist or NoteKind.Goal;

    private ContextMenu FolderMenu(NoteProject project)
    {
        var menu = NewMenu();
        menu.Items.Add(MenuEntry("Переименовать", () => RenameFolder(project)));
        if (project.Bookmarks.Count > 0)
        {
            var links = new MenuItem { Header = "Ссылки", Style = TryFindResource("WlMenuItem") as Style };
            foreach (var link in project.Bookmarks)
            {
                links.Items.Add(MenuEntry(link.Title, () => OpenLink(link.Target)));
            }

            menu.Items.Add(links);
        }

        menu.Items.Add(MenuEntry("Добавить ссылку из буфера обмена", () => AddLinkFromClipboard(project)));
        menu.Items.Add(new Separator { Style = TryFindResource("WlMenuSeparator") as Style });
        menu.Items.Add(MenuEntry("Удалить папку (заметки останутся)", () =>
        {
            _notes?.DeleteProject(project.Id);
            if (_projectId == project.Id) _smart[NotesFolderKind.All].Button.IsChecked = true;
        }));
        return menu;
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        NewFolderRow.Visibility = Visibility.Visible;
        NewFolderBox.Text = string.Empty;
        NewFolderBox.Focus();
    }

    private void NewFolderBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitNewFolder();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            NewFolderRow.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void NewFolderBox_LostFocus(object sender, RoutedEventArgs e) => CommitNewFolder();

    private void CommitNewFolder()
    {
        if (NewFolderRow.Visibility != Visibility.Visible) return;
        NewFolderRow.Visibility = Visibility.Collapsed;
        var name = (NewFolderBox.Text ?? string.Empty).Trim();
        if (name.Length == 0 || _notes is null) return;
        var project = _notes.CreateProject(name);
        RenderFolders();
        foreach (var child in FolderList.Children)
        {
            if (child is RadioButton { Tag: string tag } button && tag == "project:" + project.Id) button.IsChecked = true;
        }
    }

    private void RenameFolder(NoteProject project)
    {
        var box = new TextBox { Style = (Style)FindResource("WlSmallInput"), Text = project.Name, Margin = new Thickness(0, 0, 0, 6) };
        var index = FolderList.Children.Cast<UIElement>()
            .TakeWhile(c => c is not RadioButton { Tag: string t } || t != "project:" + project.Id).Count();
        FolderList.Children.Insert(Math.Min(index, FolderList.Children.Count), box);
        box.Focus();
        box.SelectAll();
        void Commit()
        {
            if (!FolderList.Children.Contains(box)) return;
            FolderList.Children.Remove(box);
            var name = (box.Text ?? string.Empty).Trim();
            if (name.Length > 0) _notes?.RenameProject(project.Id, name);
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Commit();
            if (e.Key == Key.Escape) FolderList.Children.Remove(box);
        };
        box.LostFocus += (_, _) => Commit();
    }

    private void AddLinkFromClipboard(NoteProject project)
    {
        string raw;
        try
        {
            raw = System.Windows.Clipboard.GetText().Trim();
        }
        catch
        {
            raw = string.Empty;
        }

        var (title, target) = ParseLink(raw);
        if (raw.Length == 0 || !IsOpenableTarget(target))
        {
            MessageBox.Show(Window.GetWindow(this), Strings.Projects_BadLink, Strings.Nav_Notes, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _notes?.AddProjectBookmark(project.Id, title, target);
    }

    // ── The list ───────────────────────────────────────────────────────────────

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _refreshQueued = false;
            Refresh();
        }));
    }

    private void Refresh()
    {
        if (_notes is null) return;
        RenderFolders();
        if (_folder == NotesFolderKind.Calendar)
        {
            CalendarView.Refresh();
            return;
        }

        var notes = Visible().ToList();
        FolderTitle.Text = _folder switch
        {
            NotesFolderKind.Pinned => "Закреплённые",
            NotesFolderKind.Tasks => "Задачи",
            NotesFolderKind.Completed => "Выполненные",
            NotesFolderKind.Trash => "Недавно удалённые",
            NotesFolderKind.Project => _notes.FindProject(_projectId)?.DisplayName ?? "Папка",
            _ => "Все заметки",
        };
        StatusText.Text = $"{notes.Count} {RussianPlural.Form(notes.Count, "заметка", "заметки", "заметок")}";
        TrashBar.Visibility = _folder == NotesFolderKind.Trash && notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NewNoteButton.IsEnabled = _folder != NotesFolderKind.Trash;

        NoteList.Children.Clear();
        string? section = null;
        foreach (var note in notes)
        {
            var heading = SectionOf(note);
            if (heading != section)
            {
                section = heading;
                NoteList.Children.Add(new TextBlock
                {
                    Text = heading,
                    Style = (Style)FindResource("WlMonoLabel"),
                    Margin = new Thickness(10, NoteList.Children.Count == 0 ? 6 : 16, 0, 6),
                });
            }

            NoteList.Children.Add(Row(note));
        }

        var empty = notes.Count == 0;
        ListEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ListEmptyTitle.Text = _search.Length > 0 ? "Ничего не найдено" : _folder == NotesFolderKind.Trash ? "Корзина пуста" : "Нет заметок";
        ListEmptyHint.Text = _search.Length > 0
            ? "Попробуйте другое слово."
            : _folder == NotesFolderKind.Trash ? "Удалённые заметки хранятся здесь 30 дней." : "Нажмите Ctrl+N, чтобы создать заметку.";

        // The open note left the folder (deleted, moved, completed): close it.
        if (_selectedId is not null && notes.All(n => n.Id != _selectedId) && !EditorView.IsOpen)
        {
            _selectedId = null;
        }

        UpdateDetail();
    }

    private IEnumerable<Note> Visible()
    {
        var all = _notes!.All;
        IEnumerable<Note> scope = _folder switch
        {
            NotesFolderKind.Trash => all.Where(n => n.IsDeleted),
            NotesFolderKind.Pinned => all.Where(n => !n.IsDeleted && n.IsPinned),
            NotesFolderKind.Tasks => all.Where(n => !n.IsDeleted && IsOpenTask(n)),
            NotesFolderKind.Completed => all.Where(n => !n.IsDeleted && n.IsCompleted),
            NotesFolderKind.Project => all.Where(n => !n.IsDeleted && n.ProjectId == _projectId),
            _ => all.Where(n => !n.IsDeleted),
        };

        if (_search.Length > 0)
        {
            scope = scope.Where(n => Matches(n, _search));
        }

        IOrderedEnumerable<Note> ordered = _folder == NotesFolderKind.Trash
            ? scope.OrderByDescending(n => n.DeletedAt)
            : scope.OrderByDescending(n => n.IsPinned);
        ordered = _sort switch
        {
            NotesSort.Created => ordered.ThenByDescending(n => n.CreatedAt),
            NotesSort.Title => ordered.ThenBy(n => n.DisplayTitle, StringComparer.CurrentCultureIgnoreCase),
            NotesSort.DueDate => ordered.ThenBy(n => n.DueDate ?? DateTimeOffset.MaxValue),
            _ => ordered.ThenByDescending(n => n.ModifiedAt),
        };
        return ordered;
    }

    private static bool Matches(Note note, string needle) =>
        Contains(note.Title, needle) || Contains(note.Text, needle) || Contains(note.Category, needle)
        || note.Items.Any(i => Contains(i.Text, needle))
        || (note.Content?.Any(b => Contains(b.Text, needle)) ?? false);

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);

    private string SectionOf(Note note)
    {
        if (note.IsPinned && _folder != NotesFolderKind.Trash) return "ЗАКРЕПЛЁННЫЕ";
        if (_sort == NotesSort.Title) return "ПО НАЗВАНИЮ";
        if (_sort == NotesSort.DueDate) return note.DueDate is { } due ? due.ToString("MMMM yyyy", Ru).ToUpper(Ru) : "БЕЗ СРОКА";
        var stamp = (_folder == NotesFolderKind.Trash ? note.DeletedAt : _sort == NotesSort.Created ? note.CreatedAt : note.ModifiedAt)
                    ?? note.ModifiedAt;
        var days = (DateTime.Today - stamp.LocalDateTime.Date).TotalDays;
        return days switch
        {
            <= 0 => "СЕГОДНЯ",
            <= 1 => "ВЧЕРА",
            <= 7 => "ПОСЛЕДНИЕ 7 ДНЕЙ",
            <= 30 => "ПОСЛЕДНИЕ 30 ДНЕЙ",
            _ => stamp.LocalDateTime.Year == DateTime.Today.Year
                ? stamp.ToString("MMMM", Ru).ToUpper(Ru)
                : stamp.ToString("yyyy", Ru),
        };
    }

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private FrameworkElement Row(Note note)
    {
        var selected = note.Id == _selectedId;
        var title = new TextBlock
        {
            Text = note.DisplayTitle,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("WlText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (note.IsCompleted) title.TextDecorations = TextDecorations.Strikethrough;

        var when = new TextBlock
        {
            Text = WhenLabel(note),
            FontSize = 12,
            Foreground = Brush(selected ? "WlTextSecondary" : "WlTextMuted"),
            Margin = new Thickness(0, 0, 8, 0),
        };
        var preview = new TextBlock
        {
            Text = Preview(note),
            FontSize = 12,
            Foreground = Brush("WlTextMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var meta = new DockPanel { Margin = new Thickness(0, 3, 0, 0), LastChildFill = true };
        meta.Children.Add(when);
        meta.Children.Add(preview);

        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(meta);
        if (Badge(note) is { } badge) text.Children.Add(badge);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        if (Thumbnail(note) is { } thumb)
        {
            Grid.SetColumn(thumb, 1);
            grid.Children.Add(thumb);
        }

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(12, 9, 10, 10),
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = note.Id,
            Background = selected ? Brush("WlBrandSoft") : System.Windows.Media.Brushes.Transparent,
        };
        System.Windows.Automation.AutomationProperties.SetName(row, note.DisplayTitle);
        row.MouseEnter += (_, _) => { if (note.Id != _selectedId) row.Background = Brush("WlHover"); };
        row.MouseLeave += (_, _) => { if (note.Id != _selectedId) row.Background = System.Windows.Media.Brushes.Transparent; };
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 1) Select(note);
        };
        row.MouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && _folder != NotesFolderKind.Trash)
            {
                DragDrop.DoDragDrop(row, new System.Windows.DataObject("WriteLiteNoteId", note.Id), DragDropEffects.Move);
            }
        };
        row.ContextMenu = RowMenu(note);
        return row;
    }

    private string WhenLabel(Note note)
    {
        if (_folder == NotesFolderKind.Trash && note.DeletedAt is { } deleted)
        {
            var left = Math.Max(0, (int)Math.Ceiling((NotesService.TrashRetention - (DateTimeOffset.Now - deleted)).TotalDays));
            return $"{left} {RussianPlural.Form(left, "день", "дня", "дней")}";
        }

        var stamp = note.ModifiedAt.LocalDateTime;
        if (stamp.Date == DateTime.Today) return stamp.ToString("HH:mm", Ru);
        if (stamp.Date == DateTime.Today.AddDays(-1)) return "Вчера";
        if ((DateTime.Today - stamp.Date).TotalDays < 7) return stamp.ToString("dddd", Ru);
        return stamp.ToString(stamp.Year == DateTime.Today.Year ? "d MMM" : "dd.MM.yyyy", Ru);
    }

    private static string Preview(Note note)
    {
        var body = note.Content is { Count: > 0 }
            ? string.Join(" ", note.Content.Select(b => b.Text).Where(t => !string.IsNullOrWhiteSpace(t)))
            : note.Text;
        if (note.Items.Count > 0 && string.IsNullOrWhiteSpace(body))
        {
            body = string.Join(", ", note.Items.Select(i => i.Text));
        }

        body = body.Replace('\n', ' ').Trim();
        var title = note.DisplayTitle;
        if (body.StartsWith(title, StringComparison.Ordinal)) body = body[title.Length..].TrimStart(' ', '.', ':', '—');
        if (body.Length == 0)
        {
            var sketches = note.Content?.Count(b => b.Kind == NoteBlockKind.Sketch) ?? 0;
            var images = note.Content?.Count(b => b.Kind == NoteBlockKind.Image) ?? 0;
            return sketches > 0 ? "Рисунок" : images > 0 ? "Изображение" : "Нет текста";
        }

        return body.Length > 140 ? body[..140] : body;
    }

    private FrameworkElement? Badge(Note note)
    {
        string? text = null;
        var accent = false;
        if (note.Kind == NoteKind.Event && note.DueDate is { } at)
        {
            text = "◷ " + (note.IsAllDay ? at.ToString("d MMMM", Ru) : at.ToString("d MMMM, HH:mm", Ru));
        }
        else if (note.Items.Count > 0)
        {
            text = $"☑ {note.DoneCount} из {note.Items.Count}";
        }
        else if (note.DueDate is { } due && !note.IsCompleted)
        {
            accent = due.Date < DateTime.Today;
            text = (accent ? "Просрочено · " : "Срок · ") + due.ToString("d MMMM", Ru);
        }

        if (text is null) return null;
        return new TextBlock
        {
            Text = text,
            FontSize = 11,
            Margin = new Thickness(0, 5, 0, 0),
            Foreground = Brush(accent ? "WlBrand" : "WlTextSecondary"),
        };
    }

    private FrameworkElement? Thumbnail(Note note)
    {
        var image = note.Content?.FirstOrDefault(b => b.Kind == NoteBlockKind.Image && b.AssetId is not null);
        if (image?.AssetId is null || _notes is null) return null;
        try
        {
            var path = _notes.Assets.PathOf(note.Id, image.AssetId);
            if (path is null || !File.Exists(path)) return null;
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 96;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            return new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(10, 0, 0, 0),
                ClipToBounds = true,
                Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill },
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        catch
        {
            return null;
        }
    }

    private ContextMenu RowMenu(Note note)
    {
        var menu = NewMenu();
        if (note.IsDeleted)
        {
            menu.Items.Add(MenuEntry("Восстановить", () => _notes?.Restore(note.Id)));
            menu.Items.Add(MenuEntry("Удалить навсегда", () =>
            {
                if (note.Id == _selectedId) CloseEditorQuietly();
                _notes?.Delete(note.Id);
            }));
            return menu;
        }

        menu.Items.Add(MenuEntry(note.IsPinned ? "Открепить" : "Закрепить", () => _notes?.SetPinned(note.Id, !note.IsPinned)));
        if (note.IsCompletable)
        {
            menu.Items.Add(MenuEntry(note.IsCompleted ? "Вернуть в работу" : "Выполнено", () => _notes?.SetCompleted(note.Id, !note.IsCompleted)));
        }

        menu.Items.Add(MenuEntry("Дублировать", () => _notes?.Duplicate(note.Id)));
        if (_notes is not null && _notes.Projects.Count > 0)
        {
            var move = new MenuItem { Header = "Переместить в папку", Style = TryFindResource("WlMenuItem") as Style };
            move.Items.Add(MenuEntry("Без папки", () => _notes.MoveNote(note.Id, null), note.ProjectId is null));
            foreach (var project in _notes.Projects)
            {
                move.Items.Add(MenuEntry(project.DisplayName, () => _notes.MoveNote(note.Id, project.Id), note.ProjectId == project.Id));
            }

            menu.Items.Add(move);
        }

        menu.Items.Add(new Separator { Style = TryFindResource("WlMenuSeparator") as Style });
        menu.Items.Add(MenuEntry("Удалить", () => Trash(note)));
        return menu;
    }

    // ── Selection and the editor ───────────────────────────────────────────────

    private void Select(Note note)
    {
        if (_notes is null) return;
        if (note.IsDeleted)
        {
            // A deleted note is shown read-only by restoring it on purpose, not by opening it.
            _selectedId = note.Id;
            CloseEditorQuietly();
            EmptyTitle.Text = "Заметка в корзине";
            HighlightSelection();
            return;
        }

        Open(note);
    }

    public void Open(Note note)
    {
        if (_notes is null) return;
        var current = _notes.Find(note.Id) ?? note;
        if (EditorView.IsOpen && EditorView.NoteId == current.Id)
        {
            HighlightSelection();
            return;
        }

        _switching = true;
        try
        {
            // Leaving a fresh, empty note discards it — the same as Apple Notes.
            if (EditorView.IsOpen && EditorView.IsBlankNew)
            {
                EditorView.Close();
            }

            _selectedId = current.Id;
            EditorView.Open(_notes, current, _ai, string.Empty);
            EditorView.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _switching = false;
        }

        HighlightSelection();
    }

    private void OpenFromCalendar(Note note)
    {
        _smart[NotesFolderKind.All].Button.IsChecked = true;
        Open(note);
    }

    private void HighlightSelection()
    {
        foreach (var child in NoteList.Children)
        {
            if (child is Border { Tag: string id } row)
            {
                row.Background = id == _selectedId ? Brush("WlBrandSoft") : System.Windows.Media.Brushes.Transparent;
            }
        }
    }

    private void UpdateDetail()
    {
        var open = EditorView.IsOpen && _selectedId is not null;
        EditorView.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        if (!open) EmptyTitle.Text = _folder == NotesFolderKind.Trash ? "Недавно удалённые" : "Выберите заметку";
    }

    private void OnEditorClosed(NoteEditorView editor)
    {
        if (_switching) return;
        _selectedId = null;
        UpdateDetail();
        Refresh();
    }

    private void CloseEditorQuietly()
    {
        if (!EditorView.IsOpen) return;
        _switching = true;
        try
        {
            EditorView.Close();
        }
        finally
        {
            _switching = false;
        }

        UpdateDetail();
    }

    private void Trash(Note note)
    {
        if (_notes is null) return;
        if (note.Id == _selectedId)
        {
            CloseEditorQuietly();
            _selectedId = null;
        }

        _notes.MoveToTrash(note.Id);
    }

    private void NewNote_Click(object sender, RoutedEventArgs e) => CreateNote();

    public void CreateNote(NoteKind kind = NoteKind.Note)
    {
        if (_notes is null) return;
        if (_folder is NotesFolderKind.Trash or NotesFolderKind.Calendar or NotesFolderKind.Completed)
        {
            _smart[NotesFolderKind.All].Button.IsChecked = true;
        }

        if (_folder == NotesFolderKind.Tasks && kind == NoteKind.Note) kind = NoteKind.Checklist;
        _search = string.Empty;
        SearchBox.Text = string.Empty;
        var note = _notes.Create(kind, projectId: _folder == NotesFolderKind.Project ? _projectId : null);
        if (_folder == NotesFolderKind.Pinned) _notes.SetPinned(note.Id, true);
        Refresh();
        Open(note);
    }

    private void EmptyTrash_Click(object sender, RoutedEventArgs e)
    {
        if (_notes is null) return;
        var confirm = MessageBox.Show(Window.GetWindow(this),
            "Удалить все заметки из корзины навсегда?", Strings.Nav_Notes, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm == MessageBoxResult.Yes) _notes.EmptyTrash();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _search = (SearchBox.Text ?? string.Empty).Trim();
        Refresh();
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu();
        menu.PlacementTarget = SortButton;
        menu.Items.Add(MenuEntry("По дате изменения", () => SetSort(NotesSort.Modified), _sort == NotesSort.Modified));
        menu.Items.Add(MenuEntry("По дате создания", () => SetSort(NotesSort.Created), _sort == NotesSort.Created));
        menu.Items.Add(MenuEntry("По сроку", () => SetSort(NotesSort.DueDate), _sort == NotesSort.DueDate));
        menu.Items.Add(MenuEntry("По названию", () => SetSort(NotesSort.Title), _sort == NotesSort.Title));
        menu.IsOpen = true;
    }

    private void SetSort(NotesSort sort)
    {
        _sort = sort;
        Refresh();
    }

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.N)
        {
            CreateNote();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && !EditorView.IsKeyboardFocusWithin && !SearchBox.IsKeyboardFocusWithin
                 && _selectedId is not null && _notes?.Find(_selectedId) is { } note)
        {
            if (note.IsDeleted) _notes.Delete(note.Id);
            else Trash(note);
            e.Handled = true;
        }
    }

    private void ApplyResponsiveLayout()
    {
        // Narrow windows give the note the room: the folder column folds away first.
        FolderColumn.Width = ActualWidth < 900 ? new GridLength(0) : new GridLength(224);
        ListColumn.Width = ActualWidth < 700 ? new GridLength(240) : new GridLength(320);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? System.Windows.Media.Brushes.Gray;

    private ContextMenu NewMenu() => new()
    {
        Style = TryFindResource("WlContextMenu") as Style,
    };

    private MenuItem MenuEntry(string header, Action action, bool isChecked = false)
    {
        var item = new MenuItem
        {
            Header = header,
            IsChecked = isChecked,
            Style = TryFindResource("WlMenuItem") as Style,
        };
        item.Click += (_, _) => action();
        return item;
    }

    internal static (string Title, string Target) ParseLink(string raw)
    {
        foreach (var separator in new[] { " — ", " - ", " | " })
        {
            var at = raw.LastIndexOf(separator, StringComparison.Ordinal);
            if (at > 0)
            {
                var title = raw[..at].Trim();
                var target = raw[(at + separator.Length)..].Trim();
                if (title.Length > 0 && target.Length > 0)
                {
                    return (title, NormalizeTarget(target));
                }
            }
        }

        var normalized = NormalizeTarget(raw);
        return (normalized, normalized);
    }

    private static string NormalizeTarget(string target) =>
        target.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + target : target.Trim('"');

    internal static bool IsOpenableTarget(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            return uri.Scheme is "http" or "https" or "mailto" || (uri.IsFile && (File.Exists(uri.LocalPath) || Directory.Exists(uri.LocalPath)));
        }

        return false;
    }

    /// <summary>Opens a folder link; a program or script opens its folder instead of running.</summary>
    private void OpenLink(string target)
    {
        try
        {
            if (!IsOpenableTarget(target)) return;
            var uri = new Uri(target, UriKind.Absolute);
            if (uri.IsFile && File.Exists(uri.LocalPath)
                && Path.GetExtension(uri.LocalPath).ToLowerInvariant() is ".exe" or ".bat" or ".cmd" or ".ps1" or ".vbs" or ".js" or ".msi" or ".lnk" or ".com" or ".scr")
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{uri.LocalPath}\"") { UseShellExecute = true });
                return;
            }

            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("notes-link-open-failed", $"type={ex.GetType().Name}");
        }
    }
}
