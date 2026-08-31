using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WriteLite.Documents;
using WriteLite.Services;
using WriteLite.Services.Lexical;
using WriteLite.Services.Reading;
using Brush = System.Windows.Media.Brush;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using TextBlock = System.Windows.Controls.TextBlock;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Views.Pages;

/// <summary>
/// The reading surface: one book, its marks, and the place it stopped.
/// </summary>
/// <remarks>
/// Calmer than the editor by construction. The editor exists to change a document, so
/// it earns a formatting toolbar and a live issue list; a reader changes nothing, so
/// the only controls are the ones that affect legibility — size, leading, measure —
/// and the only bright thing on screen is a mark the reader made.
///
/// Everything durable is a character offset into <see cref="ReadingDocument.Text"/>.
/// The rendered <see cref="FlowDocument"/> is rebuilt whenever the typography changes
/// and thrown away when the book closes, so no <see cref="TextPointer"/> is ever
/// stored; marks are re-placed through <see cref="ReadingAnchorResolver"/> on every
/// load, which is what makes them survive both a restart and a changed source file.
/// </remarks>
public partial class ReaderView : UserControl
{
    /// <summary>Quiet period before a scroll counts as a new reading position.</summary>
    private static readonly TimeSpan PositionSaveDelay = TimeSpan.FromMilliseconds(700);

    /// <summary>Width of the marks panel when it is shown.</summary>
    private const double PanelWidth = 340;

    /// <summary>Narrowest the reading column may become before the panel gives way to it.</summary>
    private const double MinimumTextWidth = 460;

    private readonly DocumentSerializer _serializer = new();
    private readonly System.Windows.Threading.DispatcherTimer _positionTimer = new();
    private readonly System.Windows.Threading.DispatcherTimer _announceTimer = new();

    private ReadingLibraryService? _library;
    private StudyCardDraftService? _drafts;
    private ReadingProject? _project;
    private ReadingDocument? _document;
    private CancellationTokenSource? _loadCancellation;
    private PanelTab _tab = PanelTab.Annotations;
    private bool _suppressTypographyEvents;
    private bool _suppressPanelEvents;
    private bool _restoringPosition;
    private bool _hasRoomForPanel = true;

    /// <summary>
    /// The book's division into numbered pages, rebuilt only when the text changes.
    /// </summary>
    /// <remarks>
    /// Deliberately not rebuilt on a typography change. The division is a function of the
    /// document's characters and nothing else, which is the property that makes a page number
    /// worth showing at all — see <see cref="ReadingPagination"/>.
    /// </remarks>
    private ReadingPagination _pagination = ReadingPagination.Empty;

    /// <summary>Page shown in the box, so typing in it is not fought by every scroll event.</summary>
    private int _renderedPage;

    /// <summary>The character to come back to after a run of typography changes.</summary>
    private int? _typographyAnchor;

    private bool _typographyRestorePending;

    public ReaderView()
    {
        InitializeComponent();

        _positionTimer.Interval = PositionSaveDelay;
        _positionTimer.Tick += (_, _) =>
        {
            _positionTimer.Stop();
            SavePosition();
        };

        // One timer for the status line rather than a fire-and-forget delay per
        // message: an acknowledgement queued before the reader left the page used to
        // come back two seconds later and redraw a summary for a book that was closed.
        _announceTimer.Interval = TimeSpan.FromSeconds(2);
        _announceTimer.Tick += (_, _) =>
        {
            _announceTimer.Stop();
            RenderSummary();
        };

        // Assigned up front, and only ever refilled. A RichTextBox with no ContextMenu
        // gets the framework's own editor menu — TextBoxBase registers a class handler
        // for ContextMenuOpening, which runs before this control's instance handler —
        // so building the menu inside the handler produced Windows' Cut/Copy/Paste on
        // the first right-click and WriteLite's menu only from the second one onwards.
        Canvas.ContextMenu = new ContextMenu { Style = TryFindResource("WlContextMenu") as Style };
    }

    /// <summary>Raised when the reader wants to go back to the library.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised when a word from the book should open in the dictionary.</summary>
    public event Action<string>? WordNavigationRequested;

    /// <summary>Raised after any change worth redrawing the library card for.</summary>
    public event Action? ProjectChanged;

    public ReadingProject? Project => _project;

    public void Bind(ReadingLibraryService library, StudyCardDraftService? drafts)
    {
        _library = library;
        _drafts = drafts;
    }

    // ── Opening ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a project, restoring everything the reader left behind.
    /// </summary>
    /// <remarks>
    /// Import runs off the dispatcher: a two-hundred-page PDF is seconds of work and
    /// the shell has a responsiveness watchdog watching for exactly that. Layout has
    /// to happen on the UI thread, so the model crosses back and the
    /// <see cref="FlowDocument"/> is built here.
    /// </remarks>
    public async Task OpenAsync(ReadingProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;

        _project = project;
        _document = null;
        _typographyAnchor = null;

        TitleText.Text = project.Title;
        AutomationProperties.SetName(this, $"Чтение: {project.Title}");
        RenderTypographyControls(project.Typography);
        RenderSummary();

        ErrorState.Visibility = Visibility.Collapsed;
        Canvas.Document = new FlowDocument();
        LoadingHint.Text = project.SourceFileName;
        Motion.Reveal(LoadingState, offset: 0);
        SetActionsEnabled(false);

        if (!File.Exists(project.SourcePath))
        {
            ShowMissingSource();
            return;
        }

        try
        {
            var imported = await Task.Run(
                () => _serializer.LoadAsync(project.SourcePath, cancellationToken: token), token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            // The title of the document beats the filename, but only the first time:
            // a title the reader has renamed must not be overwritten on every open.
            if (string.IsNullOrWhiteSpace(project.Title) && imported.Document.Metadata.Title is { Length: > 0 } title)
            {
                project.Title = title;
                TitleText.Text = title;
            }

            var reading = ReadingDocument.Build(imported.Document, project.Typography);
            if (token.IsCancellationRequested)
            {
                return;
            }

            AdoptDocument(reading);
        }
        catch (OperationCanceledException)
        {
            // Another book is opening.
        }
        catch (DocumentFormatException exception)
        {
            ShowError("Не удалось открыть книгу", exception.UserMessage);
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("reading-open-failed", $"type={exception.GetType().Name}");
            ShowError(
                "Не удалось открыть книгу",
                "Файл повреждён или недоступен. Пометки и закладки проекта сохранены.");
        }
    }

    private void AdoptDocument(ReadingDocument reading)
    {
        _document = reading;
        _pagination = ReadingPagination.For(reading);
        _renderedPage = 0;
        Canvas.Document = reading.Flow;
        LoadingState.Visibility = Visibility.Collapsed;
        ErrorState.Visibility = Visibility.Collapsed;
        SetActionsEnabled(true);

        if (_project is not null)
        {
            _project.TextLength = reading.Length;
        }

        ApplyMarks();
        RenderPanel();
        RenderSummary();

        // Layout has to finish before a character rectangle exists, so restoring the
        // position waits a beat rather than scrolling to zero and looking like the
        // book forgot where it was. Guarded on the document it was queued for: opening
        // a second book inside that beat must not scroll the new one to the old one's
        // place, and closing the reader must not scroll anything at all.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (!ReferenceEquals(_document, reading))
                {
                    return;
                }

                RestorePosition();
                Motion.Reveal(Canvas, offset: 8);
            }));
    }

    private void ShowMissingSource()
    {
        LoadingState.Visibility = Visibility.Collapsed;
        SetActionsEnabled(false);
        ShowError(
            "Файл книги не найден",
            $"«{_project?.SourceFileName}» не удалось найти по прежнему пути. " +
            "Все пометки, закладки и карточки проекта сохранены — укажите файл заново, и они вернутся на свои места.");

        RelocateButton.Visibility = Visibility.Visible;
    }

    private void ShowError(string title, string hint)
    {
        LoadingState.Visibility = Visibility.Collapsed;
        ErrorTitle.Text = title;
        ErrorHint.Text = hint;
        RelocateButton.Visibility = Visibility.Collapsed;
        Motion.Reveal(ErrorState);
    }

    private void SetActionsEnabled(bool enabled)
    {
        HighlightButton.IsEnabled = enabled;
        AnnotateButton.IsEnabled = enabled;
        CardButton.IsEnabled = enabled;
        BookmarkButton.IsEnabled = enabled;

        PageBox.IsEnabled = enabled;
        if (!enabled)
        {
            // A book that failed to open has no pages, and offering navigation for it would
            // be the interface claiming a state the reader is not in.
            PreviousPageButton.IsEnabled = false;
            NextPageButton.IsEnabled = false;
            PageBox.Text = string.Empty;
            PageCountText.Text = string.Empty;
            _pagination = ReadingPagination.Empty;
            _renderedPage = 0;
        }
    }

    private async void Relocate_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || _library is null)
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

        _project.SourcePath = dialog.FileName;
        _project.SourceFileName = Path.GetFileName(dialog.FileName);
        _library.Save(_project);
        ProjectChanged?.Invoke();

        await OpenAsync(_project);
    }

    // ── Marks on the page ────────────────────────────────────────────────────

    /// <summary>
    /// Paints every stored highlight and annotation onto the rendered document.
    /// </summary>
    /// <remarks>
    /// Annotations are painted after highlights so that a passage carrying both shows
    /// the annotation's tint — the one with the reader's words attached is the one
    /// worth finding again.
    /// </remarks>
    private void ApplyMarks()
    {
        if (_document is null || _project is null)
        {
            return;
        }

        foreach (var highlight in _project.Highlights)
        {
            Paint(highlight.Anchor, highlight.Tint);
        }

        foreach (var annotation in _project.Annotations)
        {
            Paint(annotation.Anchor, annotation.Tint);
        }
    }

    private void Paint(ReadingAnchor anchor, HighlightTint tint)
    {
        if (_document is null)
        {
            return;
        }

        var resolution = ReadingAnchorResolver.Resolve(_document.Text, anchor);
        if (!resolution.IsPlaced)
        {
            return;
        }

        // A shifted anchor is corrected in place, so the next save records where the
        // passage actually is rather than making every load search for it again.
        if (resolution.Status == AnchorStatus.Shifted)
        {
            anchor.Start = resolution.Start;
            anchor.Length = resolution.Length;
        }

        _document.RangeFor(resolution.Start, resolution.Length)
            ?.ApplyPropertyValue(TextElement.BackgroundProperty, TintBrush(tint));
    }

    private Brush TintBrush(HighlightTint tint) => (Brush)FindResource(tint switch
    {
        HighlightTint.Sage => "WlMarkSage",
        HighlightTint.Coral => "WlMarkCoral",
        HighlightTint.Neutral => "WlMarkNeutral",
        _ => "WlMarkAmber"
    });

    private Brush TintDot(HighlightTint tint) => (Brush)FindResource(tint switch
    {
        HighlightTint.Sage => "WlMarkSageSolid",
        HighlightTint.Coral => "WlMarkCoralSolid",
        HighlightTint.Neutral => "WlMarkNeutralSolid",
        _ => "WlMarkAmberSolid"
    });

    // ── Selection ────────────────────────────────────────────────────────────

    /// <summary>
    /// What was selected, frozen at the moment it was read.
    /// </summary>
    /// <remarks>
    /// Every action the reader can start from a selection runs against one of these
    /// rather than against <c>Canvas.Selection</c>. A live <see cref="TextSelection"/>
    /// belongs to the control that owns the caret, and opening a context menu moves
    /// focus to the menu's popup — which is why «Выделить» and «Создать карточку» could
    /// be pressed on visibly selected text and do nothing at all. Plain numbers and a
    /// string cannot be invalidated by focus, by a relayout, or by the reader clicking
    /// somewhere else while a modal editor is open.
    /// </remarks>
    private sealed record ReaderSelection(
        string ProjectId,
        string Text,
        int Start,
        int End,
        int BlockIndex,
        int ReaderPosition)
    {
        public int Length => End - Start;

        /// <summary>A fresh anchor for this selection. Never shared between two stored marks.</summary>
        public ReadingAnchor ToAnchor() => new()
        {
            Start = Start,
            Length = Length,
            Quote = Text,
            BlockIndex = BlockIndex
        };
    }

    /// <summary>Freezes whatever is selected right now, or null when nothing usable is.</summary>
    private ReaderSelection? CaptureSelection()
    {
        if (_document is null || _project is null)
        {
            return null;
        }

        var selection = Canvas.Selection;
        if (selection is null || selection.IsEmpty)
        {
            return null;
        }

        var start = _document.OffsetOf(selection.Start);
        var end = _document.OffsetOf(selection.End);

        // A backwards drag hands back its ends in the order they were made.
        if (end < start)
        {
            (start, end) = (end, start);
        }

        start = Math.Clamp(start, 0, _document.Length);
        end = Math.Clamp(end, 0, _document.Length);

        if (end <= start)
        {
            return null;
        }

        var quote = _document.Text[start..end];
        if (string.IsNullOrWhiteSpace(quote))
        {
            return null;
        }

        return new ReaderSelection(
            _project.Id,
            quote,
            start,
            end,
            BlockIndexAt(start),
            _project.Position);
    }

    private int BlockIndexAt(int offset)
    {
        if (_document is null)
        {
            return 0;
        }

        var index = 0;
        for (var i = 0; i < _document.BlockStarts.Count; i++)
        {
            if (_document.BlockStarts[i] <= offset)
            {
                index = i;
            }
            else
            {
                break;
            }
        }

        return index;
    }

    /// <summary>
    /// Checks a snapshot is still usable, and says so in the status bar when it is not.
    /// </summary>
    /// <remarks>
    /// A snapshot belonging to a different project is refused rather than applied to the
    /// book that happens to be open: closing one book and opening another while a menu
    /// was up must not put a highlight at those offsets in the new one.
    /// </remarks>
    private bool Usable([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] ReaderSelection? selection)
    {
        if (selection is not null && selection.ProjectId == _project?.Id)
        {
            return true;
        }

        Announce("ВЫДЕЛИТЕ ФРАГМЕНТ ТЕКСТА");
        return false;
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    // The toolbar reads the selection as it stands; the context menu hands over the
    // snapshot it took when it opened. Both arrive here as a plain value, and neither
    // path reaches for a live TextSelection after focus has moved on — which is what
    // made these actions silently do nothing when started from the menu.

    private void Highlight_Click(object sender, RoutedEventArgs e) =>
        AddHighlight(HighlightTint.Amber, CaptureSelection());

    private void AddHighlight(HighlightTint tint, ReaderSelection? selection)
    {
        if (_project is null || !Usable(selection))
        {
            return;
        }

        var anchor = selection.ToAnchor();

        // Highlighting the same passage twice recolours it rather than stacking two
        // marks the reader would then have to delete one at a time.
        var existing = _project.Highlights.FirstOrDefault(mark =>
            mark.Anchor.Start == anchor.Start && mark.Anchor.Length == anchor.Length);

        if (existing is not null)
        {
            existing.Tint = tint;
        }
        else
        {
            _project.Highlights.Add(new Highlight { Anchor = anchor, Tint = tint });
        }

        Paint(anchor, tint);
        Commit("ФРАГМЕНТ ВЫДЕЛЕН");
    }

    private void Annotate_Click(object sender, RoutedEventArgs e) => AddAnnotation(CaptureSelection());

    private void AddAnnotation(ReaderSelection? selection)
    {
        if (_project is null || !Usable(selection))
        {
            return;
        }

        var editor = new AnnotationEditorWindow(selection.Text) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        // Re-checked after the modal: the reader may have closed the book behind it.
        if (_project is null || _project.Id != selection.ProjectId)
        {
            return;
        }

        var anchor = selection.ToAnchor();

        _project.Annotations.Add(new Annotation
        {
            Anchor = anchor,
            Note = editor.Note,
            Tint = editor.Tint,
            Tag = editor.TagText
        });

        Paint(anchor, editor.Tint);
        ShowPanelTab(PanelTab.Annotations);
        Commit("ПОМЕТКА СОХРАНЕНА");
    }

    private void Card_Click(object sender, RoutedEventArgs e) =>
        CreateCard(CaptureSelection(), useAi: false);

    private void CreateCard(ReaderSelection? selection, bool useAi)
    {
        if (_project is null || !Usable(selection))
        {
            return;
        }

        var editor = new StudyCardEditorWindow(selection.Text, _drafts) { Owner = Window.GetWindow(this) };

        if (useAi)
        {
            editor.StartWithAiDraft();
        }

        if (editor.ShowDialog() != true)
        {
            return;
        }

        if (_project is null || _project.Id != selection.ProjectId)
        {
            return;
        }

        _project.Cards.Add(new StudyCard
        {
            Front = editor.Front,
            Back = editor.Back,
            Source = selection.ToAnchor(),
            DraftedByAi = editor.UsedAi
        });

        ShowPanelTab(PanelTab.Cards);
        Commit("КАРТОЧКА СОЗДАНА");
    }

    private void Bookmark_Click(object sender, RoutedEventArgs e) => AddBookmark(CaptureSelection());

    private void AddBookmark(ReaderSelection? selection)
    {
        if (_project is null || _document is null)
        {
            return;
        }

        // A bookmark is about where the reader is, so it works with nothing selected —
        // but a selection is a more precise statement of where than the viewport is.
        var offset = selection is not null && selection.ProjectId == _project.Id
            ? selection.Start
            : VisiblePosition();

        _project.Bookmarks.Add(new Bookmark
        {
            Offset = offset,
            Fraction = ReadingAnchorResolver.Fraction(offset, _document.Length),
            Preview = _document.Preview(offset)
        });

        ShowPanelTab(PanelTab.Bookmarks);
        Commit("ЗАКЛАДКА ДОБАВЛЕНА");
    }

    /// <summary>
    /// The one ending shared by every action that changes the project.
    /// </summary>
    /// <remarks>
    /// Panel, counts, disk and acknowledgement in one place, in that order. The panel
    /// used to be redrawn as a side effect of <c>TabX.IsChecked = true</c>, which fires
    /// <c>Checked</c> only when the tab actually changes — so adding a second annotation
    /// while the annotations tab was already selected updated nothing, and the reader
    /// had to leave the section and come back to see their own note. Refreshing here
    /// rather than through a toggle's event is what makes "it appears immediately" a
    /// property of the action instead of an accident of which tab was open.
    /// </remarks>
    private void Commit(string? announcement = null)
    {
        RenderPanel();
        RenderSummary();
        SaveSettings();

        if (announcement is not null)
        {
            Announce(announcement);
        }
    }

    /// <summary>Selects a panel tab and renders it, whether or not the selection changed.</summary>
    private void ShowPanelTab(PanelTab tab)
    {
        _tab = tab;

        _suppressPanelEvents = true;
        try
        {
            TabAnnotations.IsChecked = tab == PanelTab.Annotations;
            TabBookmarks.IsChecked = tab == PanelTab.Bookmarks;
            TabCards.IsChecked = tab == PanelTab.Cards;
        }
        finally
        {
            _suppressPanelEvents = false;
        }
    }

    /// <summary>
    /// Confirms an action in the status bar rather than with a popup.
    /// </summary>
    /// <remarks>
    /// A reader is reading. A toast over the text would be the single most intrusive
    /// thing on the surface, so the acknowledgement goes where the counts already
    /// live and is replaced by them a moment later.
    /// </remarks>
    private void Announce(string message)
    {
        Controls.Type.SetTracked(SummaryText, message);
        Motion.Reveal(SummaryText, offset: 0);

        // Restarted rather than queued, so two actions in a row leave one pending
        // restore rather than two, and closing the book cancels it outright.
        _announceTimer.Stop();
        _announceTimer.Start();
    }

    // ── Panel ────────────────────────────────────────────────────────────────

    private void PanelTab_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _suppressPanelEvents)
        {
            return;
        }

        _tab = sender switch
        {
            _ when ReferenceEquals(sender, TabBookmarks) => PanelTab.Bookmarks,
            _ when ReferenceEquals(sender, TabCards) => PanelTab.Cards,
            _ => PanelTab.Annotations
        };

        RenderPanel();
    }

    private void PanelToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        ApplyPanelVisibility(reveal: PanelToggle.IsChecked == true);
    }

    /// <summary>
    /// Steps the marks panel aside when there is not enough room for it and the text.
    /// </summary>
    /// <remarks>
    /// The same rule the navigation rail follows, for the same reason. The panel was a
    /// fixed 340 px column beside a star column with no floor, so narrowing the window
    /// took the width out of the reading canvas until the book itself was a few
    /// characters wide and then gone — the panel was the last thing standing on a
    /// surface whose entire job is to show text. Below the threshold the panel yields;
    /// the toggle's state is untouched, so widening the window brings it back exactly
    /// as the reader left it.
    /// </remarks>
    private void Reader_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || !IsInitialized)
        {
            return;
        }

        var roomForPanel = e.NewSize.Width >= PanelWidth + MinimumTextWidth;
        if (roomForPanel == _hasRoomForPanel)
        {
            return;
        }

        _hasRoomForPanel = roomForPanel;
        ApplyPanelVisibility(reveal: false);
    }

    private void ApplyPanelVisibility(bool reveal)
    {
        var open = PanelToggle.IsChecked == true && _hasRoomForPanel;

        Panel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PanelColumn.Width = open ? new GridLength(PanelWidth) : new GridLength(0);

        if (open && reveal)
        {
            Motion.Reveal(Panel, offset: 0);
        }
    }

    private void RenderPanel()
    {
        MarksList.Children.Clear();

        if (_project is null)
        {
            return;
        }

        var rendered = _tab switch
        {
            PanelTab.Bookmarks => RenderBookmarks(),
            PanelTab.Cards => RenderCards(),
            _ => RenderAnnotations()
        };

        PanelEmpty.Visibility = rendered == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (rendered == 0)
        {
            (PanelEmptyTitle.Text, PanelEmptyHint.Text) = _tab switch
            {
                PanelTab.Bookmarks => ("Закладок пока нет",
                    "Нажмите «Закладка», чтобы запомнить место. WriteLite и так помнит, где вы остановились."),
                PanelTab.Cards => ("Карточек пока нет",
                    "Выделите фрагмент и нажмите «Карточка», чтобы превратить его в вопрос и ответ."),
                _ => ("Пометок пока нет",
                    "Выделите фрагмент и нажмите «Пометка», чтобы записать свою мысль рядом с текстом.")
            };
        }

        Motion.RevealSequence(MarksList.Children.OfType<FrameworkElement>(), maxSteps: 4);
    }

    private int RenderAnnotations()
    {
        var annotations = _project!.Annotations.OrderBy(item => item.Anchor.Start).ToList();

        foreach (var annotation in annotations)
        {
            var row = NewRow(() => GoTo(annotation.Anchor));

            var body = new StackPanel();

            body.Children.Add(Header(
                TintDot(annotation.Tint),
                annotation.CreatedAt,
                () =>
                {
                    _project.Annotations.Remove(annotation);
                    Unpaint(annotation.Anchor);
                    Commit();
                },
                "Удалить пометку"));

            var quote = new Border { Style = (Style)FindResource("WlQuoteBlock"), Margin = new Thickness(0, 8, 0, 0) };
            quote.Child = new TextBlock
            {
                Text = Shorten(annotation.Anchor.Quote, 190),
                Style = (Style)FindResource("WlQuoteText")
            };
            body.Children.Add(quote);

            if (!string.IsNullOrWhiteSpace(annotation.Note))
            {
                body.Children.Add(new TextBlock
                {
                    Text = annotation.Note,
                    Style = (Style)FindResource("WlNoteText"),
                    Margin = new Thickness(0, 10, 0, 0)
                });
            }

            if (!string.IsNullOrWhiteSpace(annotation.Tag))
            {
                body.Children.Add(Badge(annotation.Tag!));
            }

            if (IsDetached(annotation.Anchor))
            {
                body.Children.Add(DetachedNote());
            }

            row.Child = body;
            MarksList.Children.Add(row);
        }

        return annotations.Count;
    }

    private int RenderBookmarks()
    {
        var bookmarks = _project!.Bookmarks.OrderBy(item => item.Offset).ToList();

        foreach (var bookmark in bookmarks)
        {
            var row = NewRow(() => GoToOffset(bookmark.Offset));
            var body = new StackPanel();

            body.Children.Add(Header(
                (Brush)FindResource("WlBrand"),
                bookmark.CreatedAt,
                () =>
                {
                    _project.Bookmarks.Remove(bookmark);
                    Commit();
                },
                "Удалить закладку",
                $"{(int)Math.Round(bookmark.Fraction * 100)}%"));

            body.Children.Add(new TextBlock
            {
                Text = bookmark.DisplayTitle,
                Style = (Style)FindResource("WlNoteText"),
                Margin = new Thickness(0, 8, 0, 0)
            });

            row.Child = body;
            MarksList.Children.Add(row);
        }

        return bookmarks.Count;
    }

    private int RenderCards()
    {
        var cards = _project!.Cards.OrderByDescending(item => item.CreatedAt).ToList();

        foreach (var card in cards)
        {
            var row = NewRow(() =>
            {
                if (card.Source is { } source)
                {
                    GoTo(source);
                }
            });

            var body = new StackPanel();

            body.Children.Add(Header(
                (Brush)FindResource("WlTextMuted"),
                card.CreatedAt,
                () =>
                {
                    _project.Cards.Remove(card);
                    Commit();
                },
                "Удалить карточку",
                card.DraftedByAi ? "AI" : null));

            body.Children.Add(new TextBlock
            {
                Text = card.Front,
                Style = (Style)FindResource("WlNoteText"),
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(0, 8, 0, 0)
            });

            body.Children.Add(new TextBlock
            {
                Text = Shorten(card.Back, 220),
                Style = (Style)FindResource("WlQuoteText"),
                FontStyle = FontStyles.Normal,
                Margin = new Thickness(0, 6, 0, 0)
            });

            var edit = new System.Windows.Controls.Button
            {
                Style = (Style)FindResource("WlTextButton"),
                Content = "Изменить",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Margin = new Thickness(-6, 6, 0, 0)
            };

            edit.Click += (_, args) =>
            {
                args.Handled = true;
                EditCard(card);
            };

            body.Children.Add(edit);

            row.Child = body;
            MarksList.Children.Add(row);
        }

        return cards.Count;
    }

    private void EditCard(StudyCard card)
    {
        var editor = new StudyCardEditorWindow(card.Source?.Quote ?? string.Empty, _drafts)
        {
            Owner = Window.GetWindow(this)
        };

        editor.Prefill(card.Front, card.Back);

        if (editor.ShowDialog() != true)
        {
            return;
        }

        card.Front = editor.Front;
        card.Back = editor.Back;
        card.ModifiedAt = DateTimeOffset.Now;
        card.DraftedByAi = card.DraftedByAi || editor.UsedAi;

        Commit();
    }

    private Border NewRow(Action activate)
    {
        var row = new Border { Style = (Style)FindResource("WlMarkRow") };
        row.MouseLeftButtonUp += (_, _) => activate();
        return row;
    }

    private FrameworkElement Header(
        Brush dot,
        DateTimeOffset when,
        Action delete,
        string deleteTooltip,
        string? badge = null)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var mark = new System.Windows.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = dot,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0)
        };

        var meta = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var date = new TextBlock { Style = (Style)FindResource("WlMonoLabel") };
        Controls.Type.SetTracked(date, when.ToString("d MMM yyyy").ToUpperInvariant());
        date.Foreground = (Brush)FindResource("WlTextMuted");
        meta.Children.Add(date);

        if (badge is not null)
        {
            var chip = new Border { Style = (Style)FindResource("WlNoteBadge"), Margin = new Thickness(8, 0, 0, 0) };
            var chipText = new TextBlock { Style = (Style)FindResource("WlMonoLabel") };
            Controls.Type.SetTracked(chipText, badge);
            chipText.Foreground = (Brush)FindResource("WlTextMuted");
            chip.Child = chipText;
            meta.Children.Add(chip);
        }

        var remove = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("WlIconButton"),
            Width = 26,
            Height = 26,
            ToolTip = deleteTooltip,
            Content = new System.Windows.Shapes.Path
            {
                Style = (Style)FindResource("WlIconSm"),
                Data = (Geometry)FindResource("WlIconTrash"),
                Stroke = (Brush)FindResource("WlTextMuted")
            }
        };

        AutomationProperties.SetName(remove, deleteTooltip);
        remove.Click += (_, args) =>
        {
            args.Handled = true;
            delete();
        };

        Grid.SetColumn(mark, 0);
        Grid.SetColumn(meta, 1);
        Grid.SetColumn(remove, 2);
        header.Children.Add(mark);
        header.Children.Add(meta);
        header.Children.Add(remove);
        return header;
    }

    private FrameworkElement Badge(string tag)
    {
        var badge = new Border
        {
            Style = (Style)FindResource("WlNoteBadge"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 9, 0, 0)
        };

        var text = new TextBlock { Style = (Style)FindResource("WlMonoLabel") };
        Controls.Type.SetTracked(text, tag.ToUpperInvariant());
        text.Foreground = (Brush)FindResource("WlTextMuted");
        badge.Child = text;
        return badge;
    }

    private FrameworkElement DetachedNote()
    {
        var text = new TextBlock
        {
            Style = (Style)FindResource("WlCaption"),
            Text = "Фрагмент не найден в текущем файле — пометка сохранена.",
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        text.Foreground = (Brush)FindResource("WlWarning");
        return text;
    }

    private bool IsDetached(ReadingAnchor anchor) =>
        _document is not null && !ReadingAnchorResolver.Resolve(_document.Text, anchor).IsPlaced;

    private void Unpaint(ReadingAnchor anchor)
    {
        if (_document is null || _project is null)
        {
            return;
        }

        _document.RangeFor(anchor.Start, anchor.Length)
            ?.ApplyPropertyValue(TextElement.BackgroundProperty, null);

        // Another mark may cover the same words; repainting restores whichever of
        // them is still there rather than leaving a hole in an overlapping passage.
        ApplyMarks();
    }

    private static string Shorten(string text, int max)
    {
        var clean = text.Replace('\n', ' ').Replace('\t', ' ').Trim();
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s{2,}", " ");
        return clean.Length <= max ? clean : clean[..max].TrimEnd() + "…";
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private void GoTo(ReadingAnchor anchor)
    {
        if (_document is null)
        {
            return;
        }

        var resolution = ReadingAnchorResolver.Resolve(_document.Text, anchor);
        if (!resolution.IsPlaced)
        {
            Announce("ФРАГМЕНТ НЕ НАЙДЕН В ЭТОМ ФАЙЛЕ");
            return;
        }

        GoToOffset(resolution.Start);

        // Selecting the passage on arrival is what makes the jump land somewhere
        // rather than merely near somewhere.
        if (_document.RangeFor(resolution.Start, resolution.Length) is { } range)
        {
            Canvas.Selection.Select(range.Start, range.End);
        }
    }

    private void GoToOffset(int offset) => GoToOffset(offset, lead: true);

    /// <param name="lead">
    /// Whether to leave a third of a viewport above the target.
    /// </param>
    /// <remarks>
    /// <para>Lead-in is right when the destination is a passage inside the text — a
    /// highlight, an annotation, a bookmark — because a marked sentence pinned to the top
    /// edge reads as though the book starts there, and what came before it is part of
    /// understanding it.</para>
    ///
    /// <para>It is wrong for a page, and measurably so: a page jump scrolled with lead-in
    /// leaves the top of the viewport a third of a screen *before* the page begins, so the
    /// reading position — which is the character at the top of the viewport — is the previous
    /// page, and the reader is told they are on page 6 immediately after asking for page 7.
    /// That is what <c>TheForwardArrowMovesExactlyOnePage</c> caught. A page starts where it
    /// starts.</para>
    /// </remarks>
    /// <inheritdoc cref="GoToOffset(int)"/>
    private void GoToOffset(int offset, bool lead)
    {
        if (_document is null)
        {
            return;
        }

        var pointer = _document.PointerAt(offset);
        var rect = pointer.GetCharacterRect(LogicalDirection.Forward);

        if (rect.IsEmpty)
        {
            return;
        }

        // The canvas does not scroll itself (see WlReadingCanvas): the page scrolls,
        // so the character rectangle is already in the scrolled content's space.
        var leadHeight = lead ? CanvasScroll.ViewportHeight / 3 : 0;
        _restoringPosition = true;
        CanvasScroll.ScrollToVerticalOffset(Math.Max(0, rect.Top + Canvas.Margin.Top - leadHeight));

        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => _restoringPosition = false));
    }

    private void RestorePosition()
    {
        if (_project is null || _document is null)
        {
            return;
        }

        var position = ReadingAnchorResolver.ClampPosition(_project.Position, _document.Length);
        if (position > 0)
        {
            GoToOffset(position);
        }

        RenderProgress(position);
    }

    /// <summary>
    /// The offset of the first character visible at the top of the viewport.
    /// </summary>
    /// <remarks>
    /// Only answerable once WPF has laid the document out. <c>GetPositionFromPoint</c>
    /// goes to the document's text view, and a text view whose layout has been
    /// invalidated — which is precisely what changing the type size does — throws
    /// rather than returning nothing. The last known position is the honest answer in
    /// that window: the reader has not scrolled, so it is also the right one.
    /// </remarks>
    private int VisiblePosition()
    {
        if (_document is null)
        {
            return 0;
        }

        if (!Canvas.IsMeasureValid || !Canvas.IsArrangeValid)
        {
            return _project?.Position ?? 0;
        }

        var y = CanvasScroll.VerticalOffset - Canvas.Margin.Top + 8;
        var pointer = Canvas.GetPositionFromPoint(new Point(12, Math.Max(0, y)), snapToText: true);
        return pointer is null ? _project?.Position ?? 0 : _document.OffsetOf(pointer);
    }

    private void Canvas_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_document is null || _restoringPosition || Math.Abs(e.VerticalChange) < 0.5)
        {
            return;
        }

        RenderProgress(VisiblePosition());

        // Debounced: scrolling fires continuously and the position is only interesting
        // once the reader has stopped somewhere.
        _positionTimer.Stop();
        _positionTimer.Start();
    }

    private void SavePosition()
    {
        if (_project is null || _document is null || _library is null)
        {
            return;
        }

        var position = VisiblePosition();
        if (position == _project.Position)
        {
            return;
        }

        _project.Position = position;
        _project.Progress = ReadingAnchorResolver.Fraction(position, _document.Length);
        _project.LastOpenedAt = DateTimeOffset.Now;
        SaveSettings();
        RenderSummary();
    }

    private void RenderProgress(int position)
    {
        if (_document is null)
        {
            return;
        }

        var fraction = ReadingAnchorResolver.Fraction(position, _document.Length);
        Controls.Type.SetTracked(ProgressText, $"{(int)Math.Round(fraction * 100)}%");
        ProgressFill.Width = 72 * fraction;

        RenderPage(position);

        var block = BlockIndexAt(position) + 1;
        Controls.Type.SetTracked(
            LocationText,
            _document.BlockCount > 0 ? $"АБЗАЦ {block} ИЗ {_document.BlockCount}" : string.Empty);
    }

    // ── Pages ────────────────────────────────────────────────────────────────

    /// <summary>Puts the current page in the toolbar without fighting the reader for the box.</summary>
    /// <remarks>
    /// The box is left alone while it has focus. Scrolling raises this on every frame, and
    /// overwriting a half-typed page number as someone types it is the classic way a jump-to
    /// field becomes unusable.
    /// </remarks>
    private void RenderPage(int position, bool force = false)
    {
        var page = _pagination.PageAt(position);

        // Plain Text, not Controls.Type tracking: tracking inserts a thin space between
        // every character, which is right for a lettered eyebrow like «АБЗАЦ 4 ИЗ 12» and
        // wrong for a bare number, where it spaces the digits of "367" apart.
        PageCountText.Text = $"/ {_pagination.PageCount}";

        PreviousPageButton.IsEnabled = page > 1;
        NextPageButton.IsEnabled = page < _pagination.PageCount;

        // Recorded before the box is considered, because this is what the reader is on,
        // and the arrows step from it whether or not the box happens to be showing it.
        var unchanged = page == _renderedPage;
        _renderedPage = page;

        // The box is left alone while it has focus — except after a navigation the reader
        // just asked for, which is the one case where what is in the box is certainly out of
        // date and certainly not what they are typing.
        if (!force && (unchanged || PageBox.IsKeyboardFocusWithin))
        {
            return;
        }

        PageBox.Text = page.ToString();
    }

    /// <summary>
    /// The page the reader is on, as the interface currently states it.
    /// </summary>
    /// <remarks>
    /// Read from what was last rendered rather than re-derived from the scroll offset. The
    /// arrows used to re-derive it, which made a second press before the scroll settled read
    /// the position from before the first press and go nowhere — clicking «next» twice
    /// quickly advanced one page. The rendered page is updated by every scroll and by every
    /// jump, so it tracks the reader without being subject to that race.
    /// </remarks>
    private int CurrentPage =>
        _renderedPage > 0 ? _renderedPage : _pagination.PageAt(VisiblePosition());

    /// <summary>
    /// Moves the reader to the first character of a page and records it as the position.
    /// </summary>
    /// <remarks>
    /// The position is saved rather than left to the scroll handler, because a jump is an
    /// explicit statement about where the reader wants to be and should survive the
    /// application closing a moment later.
    /// </remarks>
    private void GoToPage(int page)
    {
        if (_document is null || _project is null)
        {
            return;
        }

        var clamped = Math.Clamp(page, 1, _pagination.PageCount);
        var offset = _pagination.OffsetOfPage(clamped);

        GoToOffset(offset, lead: false);

        _project.Position = offset;
        _project.Progress = ReadingAnchorResolver.Fraction(offset, _document.Length);
        SaveSettings();

        RenderProgress(offset);
        RenderPage(offset, force: true);
        Announce($"СТРАНИЦА {clamped} ИЗ {_pagination.PageCount}");
    }

    /// <summary>Turns back one page. Public because the shell binds a shortcut to it.</summary>
    public void GoToPreviousPage() => GoToPage(CurrentPage - 1);

    /// <summary>Turns forward one page. Public because the shell binds a shortcut to it.</summary>
    public void GoToNextPage() => GoToPage(CurrentPage + 1);

    private void PreviousPage_Click(object sender, RoutedEventArgs e) => GoToPreviousPage();

    private void NextPage_Click(object sender, RoutedEventArgs e) => GoToNextPage();

    private void PageBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        CommitPageBox();

        // Focus goes back to the text, because the reason for typing a page number is to
        // read that page.
        Canvas.Focus();
    }

    private void PageBox_LostFocus(object sender, RoutedEventArgs e) => CommitPageBox();

    /// <summary>
    /// Acts on whatever is in the page box, and puts the real page back when it is not a page.
    /// </summary>
    /// <remarks>
    /// Rejection is silent and self-correcting: the box returns to the page the reader is
    /// actually on. An error message for "42x" would be telling someone off for a typo in a
    /// field whose only possible content is a number.
    /// </remarks>
    private void CommitPageBox()
    {
        if (_document is null)
        {
            return;
        }

        if (int.TryParse(PageBox.Text?.Trim(), out var page) && _pagination.IsValidPage(page))
        {
            if (page != CurrentPage)
            {
                GoToPage(page);
                return;
            }
        }

        RenderPage(_pagination.OffsetOfPage(CurrentPage), force: true);
    }

    private void RenderSummary()
    {
        if (_project is null)
        {
            return;
        }

        Controls.Type.SetTracked(SummaryText, _project.Summary().ToUpperInvariant());
    }

    // ── Typography ───────────────────────────────────────────────────────────

    private void RenderTypographyControls(ReaderTypography typography)
    {
        _suppressTypographyEvents = true;
        try
        {
            SpacingSlider.Value = typography.LineSpacing;
            WidthSlider.Value = typography.ContentWidth;
            Canvas.MaxWidth = typography.ContentWidth;
            Controls.Type.SetTracked(FontSizeText, ((int)typography.FontSize).ToString());
        }
        finally
        {
            _suppressTypographyEvents = false;
        }
    }

    private void FontSmaller_Click(object sender, RoutedEventArgs e) => AdjustFont(-1);

    private void FontLarger_Click(object sender, RoutedEventArgs e) => AdjustFont(+1);

    private void AdjustFont(double delta)
    {
        if (_project is null)
        {
            return;
        }

        _project.Typography.FontSize = Math.Clamp(_project.Typography.FontSize + delta, 12, 32);
        Controls.Type.SetTracked(FontSizeText, ((int)_project.Typography.FontSize).ToString());
        ApplyTypography();
    }

    private void Spacing_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressTypographyEvents || _project is null)
        {
            return;
        }

        _project.Typography.LineSpacing = e.NewValue;
        ApplyTypography();
    }

    private void Width_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressTypographyEvents || _project is null)
        {
            return;
        }

        _project.Typography.ContentWidth = e.NewValue;

        // Measure is the one setting that touches nothing but the canvas: the column is
        // already a single column, so it only has to be told how wide to be.
        Canvas.MaxWidth = e.NewValue;
        SaveSettings();
    }

    /// <summary>
    /// Re-sets the reader's typography on the book that is already open.
    /// </summary>
    /// <remarks>
    /// Type size, leading and measure are properties of the reader, not of the book.
    /// This used to re-import the source file and rebuild the whole
    /// <see cref="FlowDocument"/> for every press of «+» and every tick of the leading
    /// slider — seconds of PDF parsing per click, on the dispatcher, with no
    /// cancellation, so a handful of quick presses queued a handful of overlapping
    /// rebuilds and the last one to finish won.
    ///
    /// Nothing about the document model depends on type size, so nothing is rebuilt.
    /// <see cref="ReadingDocument.ApplyTypography"/> rewrites the metrics on the
    /// existing blocks, which leaves every run, offset and painted mark in place, and
    /// the reading position is a character offset that was never affected in the first
    /// place — it only has to be turned back into a scroll offset once WPF has
    /// re-laid the text out.
    ///
    /// The anchor is taken once for a run of changes rather than once per change. A
    /// reader pressing «+» six times has not moved, so the character to come back to is
    /// the same every time — and asking the layout where the viewport is while the
    /// previous change is still being laid out is a question it cannot answer: the
    /// document's text view throws until it has been re-validated. That is what turned
    /// a fast sequence of presses into a crash.
    /// </remarks>
    private void ApplyTypography()
    {
        if (_project is null || _document is null)
        {
            return;
        }

        var document = _document;
        _typographyAnchor ??= VisiblePosition();

        document.ApplyTypography(_project.Typography);
        Canvas.MaxWidth = _project.Typography.ContentWidth;

        // One restore per run of changes, queued behind the layout the last of them
        // causes. The document is re-checked rather than trusted: the reader can close
        // the book inside that beat, and a continuation that assumes the page it was
        // queued from is still there is the whole family of bug this pass removes.
        if (!_typographyRestorePending)
        {
            _typographyRestorePending = true;

            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    _typographyRestorePending = false;

                    var anchor = _typographyAnchor;
                    _typographyAnchor = null;

                    if (anchor is null || !ReferenceEquals(_document, document))
                    {
                        return;
                    }

                    GoToOffset(Math.Min(anchor.Value, document.Length));
                }));
        }

        SaveSettings();
    }

    // ── Canvas interaction ───────────────────────────────────────────────────

    /// <summary>
    /// Double-clicking a word offers it to the dictionary.
    /// </summary>
    /// <remarks>
    /// The same gesture, the same normalisation and the same destination as in the
    /// editor. A reader meeting an unfamiliar word is the case the dictionary exists
    /// for, so it must not require a different move here than it does there.
    /// </remarks>
    private void Canvas_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CaptureSelection() is not { } selection)
        {
            return;
        }

        var word = WordNavigation.Normalize(selection.Text);
        if (word is null)
        {
            return;
        }

        RememberWord(word, selection.Start);
        Announce($"СЛОВО «{word.ToUpperInvariant()}» — ПКМ ДЛЯ СЛОВАРЯ");
    }

    private void RememberWord(string word, int offset)
    {
        if (_project is null || _document is null)
        {
            return;
        }

        _project.Words.RemoveAll(existing =>
            string.Equals(existing.Word, word, StringComparison.CurrentCultureIgnoreCase));

        _project.Words.Insert(0, new InterestingWord { Word = word, Offset = offset });

        // A reading list of words is a trail, not an archive: the last fifty are
        // useful, the five hundredth is noise in a file that has to load quickly.
        if (_project.Words.Count > 50)
        {
            _project.Words.RemoveRange(50, _project.Words.Count - 50);
        }

        SaveSettings();
    }

    /// <summary>
    /// Fills the reader's one context menu, against the selection as it stands now.
    /// </summary>
    /// <remarks>
    /// The menu object itself is created in the constructor and never replaced, because
    /// a <see cref="RichTextBox"/> whose <c>ContextMenu</c> is null when this event is
    /// raised gets the framework's built-in editor menu instead — the class handler on
    /// <c>TextBoxBase</c> runs ahead of this one. Refilling a menu that already exists
    /// means there is exactly one menu system in the reader and the first right-click
    /// shows the same thing as the tenth.
    ///
    /// The selection is captured here, once, and every item closes over that snapshot.
    /// By the time an item is clicked the popup has taken focus and the live selection
    /// is no longer something to rely on.
    /// </remarks>
    private void Canvas_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = Canvas.ContextMenu;
        if (menu is null || _document is null || _project is null)
        {
            // Nothing is open. Suppressing the event is what keeps Windows' own
            // Cut/Copy/Paste from appearing over an empty reading surface.
            e.Handled = true;
            return;
        }

        // Taken once, here, and closed over by every item below. Nothing on this menu
        // ever consults the live selection again.
        var selection = CaptureSelection();
        var hasSelection = selection is not null;

        menu.Items.Clear();

        var highlight = new MenuItem
        {
            Header = "Выделить",
            IsEnabled = hasSelection,
            Style = TryFindResource("WlMenuItem") as Style
        };

        highlight.Items.Add(TintItem("Оранжевым", HighlightTint.Amber, selection));
        highlight.Items.Add(TintItem("Зелёным", HighlightTint.Sage, selection));
        highlight.Items.Add(TintItem("Красным", HighlightTint.Coral, selection));
        highlight.Items.Add(TintItem("Нейтральным", HighlightTint.Neutral, selection));
        menu.Items.Add(highlight);

        menu.Items.Add(Action("Добавить пометку", hasSelection, () => AddAnnotation(selection)));
        menu.Items.Add(Action("Создать карточку", hasSelection, () => CreateCard(selection, useAi: false)));

        if (_drafts?.IsAvailable == true)
        {
            menu.Items.Add(Action(
                "Создать карточку с помощью WriteLite AI",
                hasSelection,
                () => CreateCard(selection, useAi: true)));
        }

        menu.Items.Add(new Separator { Style = TryFindResource("WlMenuSeparator") as Style });
        menu.Items.Add(Action("Добавить закладку", true, () => AddBookmark(selection)));

        var word = selection is null ? null : WordNavigation.Normalize(selection.Text);
        if (word is not null)
        {
            menu.Items.Add(new Separator { Style = TryFindResource("WlMenuSeparator") as Style });
            menu.Items.Add(Action($"Открыть «{word}» в словаре", true, () =>
            {
                RememberWord(word, selection!.Start);
                WordNavigationRequested?.Invoke(word);
            }));
        }

        menu.Items.Add(new Separator { Style = TryFindResource("WlMenuSeparator") as Style });

        // Copy runs off the snapshot too. ApplicationCommands.Copy would be routed to a
        // control whose selection the popup has already taken focus away from.
        menu.Items.Add(Action("Копировать", hasSelection, () => CopySelection(selection!)));
    }

    private void CopySelection(ReaderSelection selection)
    {
        try
        {
            System.Windows.Clipboard.SetText(selection.Text);
        }
        catch (System.Runtime.InteropServices.ExternalException exception)
        {
            // Another process is holding the clipboard open. Worth saying, not worth
            // losing the reader's place over.
            CompatibilityLogger.Technical("reading-copy-failed", $"type={exception.GetType().Name}");
            Announce("БУФЕР ОБМЕНА ЗАНЯТ ДРУГОЙ ПРОГРАММОЙ");
        }
    }

    private MenuItem TintItem(string header, HighlightTint tint, ReaderSelection? selection)
    {
        var item = new MenuItem
        {
            Header = header,
            Style = TryFindResource("WlMenuItem") as Style,
            Icon = new System.Windows.Shapes.Rectangle
            {
                Width = 10,
                Height = 10,
                RadiusX = 2,
                RadiusY = 2,
                Fill = TintDot(tint)
            }
        };

        item.Click += (_, _) => AddHighlight(tint, selection);
        return item;
    }

    private MenuItem Action(string header, bool enabled, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = enabled,
            Style = TryFindResource("WlMenuItem") as Style
        };

        item.Click += (_, _) => action();
        return item;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        Close();
        CloseRequested?.Invoke();
    }

    /// <summary>Flushes the reading position without putting the book away.</summary>
    public void SavePositionNow()
    {
        _positionTimer.Stop();
        SavePosition();
        _library?.Flush();
    }

    /// <summary>Writes the position and releases the document. Called when the reader leaves.</summary>
    public void Close()
    {
        SavePositionNow();

        _announceTimer.Stop();
        _loadCancellation?.Cancel();
        _typographyAnchor = null;
        _document = null;
        Canvas.Document = new FlowDocument();

        // Emptied rather than replaced: the menu object stays, because a RichTextBox
        // without one falls back to the framework's editor menu. Clearing the items also
        // drops the snapshots they were holding for a book that is no longer open.
        if (Canvas.ContextMenu is { } menu)
        {
            menu.IsOpen = false;
            menu.Items.Clear();
        }
    }

    /// <summary>
    /// Records a change to the project and lets the disk catch up.
    /// </summary>
    /// <remarks>
    /// The in-memory project is already correct by the time this is called, and the
    /// library index and the shelf update from it immediately; only the file write is
    /// deferred and coalesced. Every one of these used to be two synchronous JSON
    /// writes on the dispatcher, on a path that runs on every scroll stop, every
    /// double-clicked word and every tick of the leading slider.
    /// </remarks>
    private void SaveSettings()
    {
        if (_project is null || _library is null)
        {
            return;
        }

        _library.SaveDeferred(_project);
        ProjectChanged?.Invoke();
    }

    private enum PanelTab
    {
        Annotations,
        Bookmarks,
        Cards
    }
}
