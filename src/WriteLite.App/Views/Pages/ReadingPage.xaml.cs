using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using WriteLite.Resources;
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

    /// <summary>
    /// Cover pictures. Owned by the page because the shelf is the only place they are used.
    /// </summary>
    private readonly BookCoverStore _covers = new();

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

    /// <summary>Turns back one page, for the shell shortcut. Does nothing with no book open.</summary>
    public void GoToPreviousPage() => Reader.GoToPreviousPage();

    /// <summary>Turns forward one page, for the shell shortcut.</summary>
    public void GoToNextPage() => Reader.GoToNextPage();

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

        AutomationProperties.SetName(ContinueCard, string.Format(Strings.Reading_ContinueAutomation, candidate.Title));
    }

    private void RenderStatus(IReadOnlyList<ReadingProjectSummary> projects)
    {
        if (projects.Count == 0)
        {
            Controls.Type.SetTracked(StatusText, Strings.Reading_NoProjectsTracked);
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

        AutomationProperties.SetName(card, string.Format(Strings.Reading_ProjectAutomation, project.Title));

        var body = new StackPanel();

        if (BuildCover(project) is { } cover)
        {
            body.Children.Add(cover);
        }

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
                Text = Strings.Reading_FileMissing,
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

        menu.Items.Add(Item(Strings.Reading_MenuOpen, () => OpenProject(project.Id)));
        menu.Items.Add(Item(Strings.Reading_MenuRename, () => Rename(project)));
        menu.Items.Add(Item(Strings.Reading_MenuRelocate, () => Relocate(project)));
        menu.Items.Add(new Separator { Style = TryFindResource("WlMenuSeparator") as Style });
        menu.Items.Add(Item(
            _covers.Has(project.Id) ? Strings.Reading_MenuReplaceCover : Strings.Reading_MenuChooseCover,
            () => ChooseCover(project)));

        if (_covers.Has(project.Id))
        {
            menu.Items.Add(Item(Strings.Reading_MenuRemoveCover, () => RemoveCover(project)));
        }

        menu.Items.Add(new Separator { Style = TryFindResource("WlMenuSeparator") as Style });
        menu.Items.Add(Item(Strings.Reading_MenuDeleteProject, () => DeleteProject(project)));

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

    // ── Covers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The picture at the top of a shelf card, or nothing when this book has no cover.
    /// </summary>
    /// <remarks>
    /// <para>A fixed 3:2 frame with <see cref="Stretch.UniformToFill"/> and a clip. Covers
    /// come in every proportion there is — a scanned dust jacket is tall, a screenshot is
    /// wide — and letting each card size itself to its own picture would make the shelf a
    /// ragged wall of different heights. Filling the frame and cropping keeps the grid, and
    /// crops from the centre, which is where the title of a book cover is.</para>
    ///
    /// <para>The version is appended as a fragment so that a replaced cover is a different
    /// URI to WPF's image cache. The file path never changes, so without it the shelf would
    /// go on drawing the previous picture for the rest of the session.</para>
    /// </remarks>
    private FrameworkElement? BuildCover(ReadingProjectSummary project)
    {
        if (_covers.Load(project.Id) is not { } image)
        {
            return null;
        }

        var frame = new Border
        {
            Height = 150,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 12),
            ClipToBounds = true,
            Background = (Brush)FindResource("WlRaised"),
            Child = new System.Windows.Controls.Image
            {
                Source = image,
                Stretch = Stretch.UniformToFill,
                // The picture is decorative: the card already carries the title, and a
                // screen reader announcing "image" after it adds nothing.
                Focusable = false
            }
        };

        AutomationProperties.SetName(frame, string.Empty);
        frame.SetValue(AutomationProperties.IsOffscreenBehaviorProperty, IsOffscreenBehavior.Onscreen);
        return frame;
    }

    private void ChooseCover(ReadingProjectSummary project)
    {
        if (_library is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = Strings.Reading_CoverDialogTitle,
            Filter = Strings.Reading_CoverFilter,
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (_library.Load(project.Id) is not { } stored)
        {
            return;
        }

        var result = _covers.Set(project.Id, dialog.FileName, stored.CoverVersion);
        if (!result.Succeeded)
        {
            MessageBox.Show(
                result.Message,
                Strings.Reading_Cover,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        stored.CoverVersion = result.Version;
        _library.Save(stored);
        RenderLibrary();
    }

    private void RemoveCover(ReadingProjectSummary project)
    {
        if (_library is null || !_covers.Remove(project.Id))
        {
            return;
        }

        if (_library.Load(project.Id) is { } stored)
        {
            // The counter is not reset. It is a cache key, and a book that gets a new cover
            // after this one must not reuse a URI the image cache has already seen.
            _library.Save(stored);
        }

        RenderLibrary();
    }

    // ── Opening ──────────────────────────────────────────────────────────────

    private void OpenBook_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Reading_OpenBook,
            Filter = Strings.Reading_OpenFilter,
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
                Strings.Reading_UnsupportedMessage,
                Strings.Nav_Reading,
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
            CompatibilityLogger.Technical("reading-openfile-failed", exception);
            MessageBox.Show(
                Strings.Reading_OpenFailedMessage,
                Strings.Nav_Reading,
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
        var prompt = new TextPromptWindow(Strings.Reading_RenamePrompt, project.Title)
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
            Title = Strings.Reading_RelocateTitle,
            Filter = Strings.Reading_RelocateFilter,
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
            string.Format(Strings.Reading_DeleteConfirm, project.Title),
            Strings.Nav_Reading,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm == MessageBoxResult.Yes)
        {
            _library?.Delete(project.Id);

            // The cover belongs to the project, so it goes with it. Left behind it would be
            // an orphan under the data directory that nothing could ever name again.
            _covers.Remove(project.Id);
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
