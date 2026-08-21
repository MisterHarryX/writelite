using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using WriteLite.Services;
using WriteLite.Services.Reading;
using Brush = System.Windows.Media.Brush;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using TextBlock = System.Windows.Controls.TextBlock;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Views.Pages;

/// <summary>
/// Reading: the shelf, and the reader that opens off it.
/// </summary>
/// <remarks>
/// One page holding two views rather than two rail entries. The rail says «Чтение»
/// and stays lit whether the reader is choosing a book or in the middle of one, which
/// is how the section reads as a place rather than as two unrelated screens — and it
/// means going back to the library never has to fight the rail's selection.
/// </remarks>
public partial class ReadingPage : UserControl
{
    /// <summary>The formats the document engine can already import.</summary>
    private static readonly string[] SupportedExtensions = [".pdf", ".docx", ".odt", ".txt"];

    private ReadingLibraryService? _library;

    public ReadingPage()
    {
        InitializeComponent();

        Reader.CloseRequested += ShowLibrary;
        Reader.WordNavigationRequested += word => WordNavigationRequested?.Invoke(word);

        // The shelf is redrawn when it is on screen, and marked stale when it is not.
        // Every highlight, every scroll stop and every double-clicked word raises this,
        // and rebuilding a wall of project cards behind an open book is work nobody can
        // see — the shelf is redrawn from the store on the way back to it anyway.
        Reader.ProjectChanged += () =>
        {
            if (IsReading)
            {
                _libraryStale = true;
                return;
            }

            Dispatcher.BeginInvoke(new Action(RenderLibrary));
        };
    }

    private bool _libraryStale;

    /// <summary>Raised when a word from a book should open in the dictionary.</summary>
    public event Action<string>? WordNavigationRequested;

    /// <summary>True while a book is open, so the shell knows Escape belongs to the reader.</summary>
    public bool IsReading => Reader.Visibility == Visibility.Visible;

    public void Bind(ReadingLibraryService library, StudyCardDraftService? drafts)
    {
        _library = library;
        Reader.Bind(library, drafts);
        RenderLibrary();
    }

    /// <summary>Called when the page comes into view.</summary>
    public void Reload()
    {
        if (!IsReading)
        {
            RenderLibrary();
        }
    }

    /// <summary>
    /// Writes the reading position without putting the book away.
    /// </summary>
    /// <remarks>
    /// Called when the rail moves to another section. The book stays open, so coming
    /// back lands on the page that was being read rather than on the library — but the
    /// position is on disk from this moment, which is what makes a crash somewhere else
    /// in the application cost nothing here.
    /// </remarks>
    public void Suspend()
    {
        if (IsReading)
        {
            Reader.SavePositionNow();
        }
    }

    /// <summary>Closes the book and releases the document. Called when the application exits.</summary>
    public void Shutdown()
    {
        if (IsReading)
        {
            Reader.Close();
        }
    }

    // ── Library ──────────────────────────────────────────────────────────────

    private void RenderLibrary()
    {
        if (_library is null)
        {
            return;
        }

        var projects = _library.Projects;

        ProjectsPanel.Children.Clear();
        foreach (var project in projects)
        {
            ProjectsPanel.Children.Add(BuildProjectCard(project));
        }

        var empty = projects.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        LibraryHeader.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ProjectsPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        RenderContinue();
        RenderStatus(projects);
    }

    private void RenderContinue()
    {
        var candidate = _library?.ContinueReading;

        // Only offered once there is something to continue. A book at 0 % is not a
        // book you are in the middle of, and offering it would make the section's
        // first card meaningless on the first run.
        if (candidate is null || candidate.Progress <= 0.001)
        {
            ContinueSection.Visibility = Visibility.Collapsed;
            return;
        }

        ContinueSection.Visibility = Visibility.Visible;
        ContinueTitle.Text = candidate.Title;
        ContinueDetails.Text = candidate.Details();
        ContinueProgress.Width = 420 * Math.Clamp(candidate.Progress, 0, 1);
        ContinueCard.Tag = candidate.Id;

        AutomationProperties.SetName(ContinueCard, $"Продолжить чтение: {candidate.Title}");
    }

    private void RenderStatus(IReadOnlyList<ReadingProjectSummary> projects)
    {
        if (projects.Count == 0)
        {
            Controls.Type.SetTracked(StatusText, "НЕТ ПРОЕКТОВ ЧТЕНИЯ");
            return;
        }

        var annotations = projects.Sum(project => project.AnnotationCount);
        var cards = projects.Sum(project => project.CardCount);

        Controls.Type.SetTracked(
            StatusText,
            $"{projects.Count} {RussianPlural.Form(projects.Count, "ПРОЕКТ", "ПРОЕКТА", "ПРОЕКТОВ")} · " +
            $"{annotations} {RussianPlural.Form(annotations, "ПОМЕТКА", "ПОМЕТКИ", "ПОМЕТОК")} · " +
            $"{cards} {RussianPlural.Form(cards, "КАРТОЧКА", "КАРТОЧКИ", "КАРТОЧЕК")}");
    }

    private FrameworkElement BuildProjectCard(ReadingProjectSummary project)
    {
        var card = new Border
        {
            Style = (Style)FindResource("WlReadingCard"),
            Margin = new Thickness(0, 0, 16, 16),
            MinWidth = 268,
            MaxWidth = 340,
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = project.Id
        };

        AutomationProperties.SetName(card, $"Проект чтения: {project.Title}");

        var body = new StackPanel();

        var format = new TextBlock { Style = (Style)FindResource("WlMonoLabel") };
        Controls.Type.SetTracked(format, project.SourceFormat);
        format.Foreground = (Brush)FindResource("WlTextMuted");
        body.Children.Add(format);

        body.Children.Add(new TextBlock
        {
            Text = project.Title,
            Style = (Style)FindResource("WlCardTitle"),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 48,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 9, 0, 0)
        });

        var missing = !ReadingLibraryService.SourceExists(project);
        if (missing)
        {
            var warning = new TextBlock
            {
                Text = "Файл не найден — пометки сохранены",
                Style = (Style)FindResource("WlCaption"),
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };

            warning.Foreground = (Brush)FindResource("WlWarning");
            body.Children.Add(warning);
        }

        body.Children.Add(new TextBlock
        {
            Text = project.Details(),
            Style = (Style)FindResource("WlCaption"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        });

        var track = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        track.Children.Add(new Border { Style = (Style)FindResource("WlProgressTrack") });

        var fill = new Border
        {
            Style = (Style)FindResource("WlProgressFill"),
            Width = 0
        };

        // The fill is sized once the card has been measured, because a WrapPanel does
        // not decide how wide its children are until then.
        track.SizeChanged += (_, args) => fill.Width = args.NewSize.Width * Math.Clamp(project.Progress, 0, 1);
        track.Children.Add(fill);
        body.Children.Add(track);

        card.Child = body;
        card.MouseLeftButtonUp += (_, _) => OpenProject(project.Id);
        card.ContextMenuOpening += (_, args) =>
        {
            args.Handled = true;
            card.ContextMenu = BuildProjectMenu(project);
            card.ContextMenu.IsOpen = true;
        };

        return card;
    }

    private ContextMenu BuildProjectMenu(ReadingProjectSummary project)
    {
        var menu = new ContextMenu { Style = TryFindResource("WlContextMenu") as Style };

        menu.Items.Add(Item("Открыть", () => OpenProject(project.Id)));
        menu.Items.Add(Item("Переименовать…", () => Rename(project)));
        menu.Items.Add(Item("Указать файл заново…", () => Relocate(project)));
        menu.Items.Add(new Separator { Style = TryFindResource("WlMenuSeparator") as Style });
        menu.Items.Add(Item("Удалить проект", () => DeleteProject(project)));

        return menu;
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

    // ── Opening ──────────────────────────────────────────────────────────────

    private void OpenBook_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Открыть книгу",
            Filter = "Документы (*.pdf;*.docx;*.odt;*.txt)|*.pdf;*.docx;*.odt;*.txt|" +
                     "PDF (*.pdf)|*.pdf|Word (*.docx)|*.docx|OpenDocument (*.odt)|*.odt|Текст (*.txt)|*.txt",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            OpenFile(dialog.FileName);
        }
    }

    private void Continue_Click(object sender, MouseButtonEventArgs e)
    {
        if (ContinueCard.Tag is string id)
        {
            OpenProject(id);
        }
    }

    /// <summary>
    /// Opens a file as a reading project, reusing the existing one for this book.
    /// </summary>
    /// <remarks>
    /// Identity comes from the file's content, so opening the same book a second time
    /// — from a different folder, under a new name — returns to the project that
    /// already holds the marks rather than starting an empty one beside it.
    /// </remarks>
    private void OpenFile(string path)
    {
        if (_library is null)
        {
            return;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            MessageBox.Show(
                "WriteLite пока открывает PDF, DOCX, ODT и TXT.",
                "Чтение",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var project = _library.OpenOrCreate(path);
            RenderLibrary();
            ShowReader(project);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            CompatibilityLogger.Technical("reading-openfile-failed", $"type={exception.GetType().Name}");
            MessageBox.Show(
                "Не удалось открыть файл. Возможно, он занят другой программой или недоступен.",
                "Чтение",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OpenProject(string projectId)
    {
        if (_library?.Load(projectId) is not { } project)
        {
            return;
        }

        project.LastOpenedAt = DateTimeOffset.Now;
        _library.Save(project);
        ShowReader(project);
    }

    private void ShowReader(ReadingProject project)
    {
        LibraryView.Visibility = Visibility.Collapsed;
        Reader.Visibility = Visibility.Visible;
        Motion.Reveal(Reader, offset: 8);

        _ = Reader.OpenAsync(project);
    }

    private void ShowLibrary()
    {
        Reader.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Visible;
        RenderLibrary();
        Motion.Reveal(LibraryView, offset: 6);
    }

    /// <summary>Closes the book if one is open. Returns false when there was nothing to close.</summary>
    public bool TryCloseReader()
    {
        if (!IsReading)
        {
            return false;
        }

        Reader.Close();
        ShowLibrary();
        return true;
    }

    // ── Project management ───────────────────────────────────────────────────

    private void Rename(ReadingProjectSummary project)
    {
        var prompt = new TextPromptWindow("Название проекта", project.Title)
        {
            Owner = Window.GetWindow(this)
        };

        if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.Value))
        {
            _library?.Rename(project.Id, prompt.Value);
            RenderLibrary();
        }
    }

    private void Relocate(ReadingProjectSummary summary)
    {
        if (_library?.Load(summary.Id) is not { } project)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Указать файл книги",
            Filter = "Документы (*.pdf;*.docx;*.odt;*.txt)|*.pdf;*.docx;*.odt;*.txt|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        project.SourcePath = dialog.FileName;
        project.SourceFileName = Path.GetFileName(dialog.FileName);
        _library.Save(project);
        RenderLibrary();
    }

    private void DeleteProject(ReadingProjectSummary project)
    {
        var confirm = MessageBox.Show(
            $"Удалить проект «{project.Title}»?\n\n" +
            "Пометки, закладки и карточки будут удалены. Сам файл книги останется на месте.",
            "Чтение",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm == MessageBoxResult.Yes)
        {
            _library?.Delete(project.Id);
            RenderLibrary();
        }
    }

    // ── Drag and drop ────────────────────────────────────────────────────────

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedBook(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Page_Drop(object sender, DragEventArgs e)
    {
        if (DroppedBook(e) is { } path)
        {
            e.Handled = true;
            OpenFile(path);
        }
    }

    private static string? DroppedBook(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            || e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths)
        {
            return null;
        }

        return paths.FirstOrDefault(path =>
            SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()));
    }
}
