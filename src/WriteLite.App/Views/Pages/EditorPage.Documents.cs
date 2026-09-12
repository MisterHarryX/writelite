using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using WriteLite.Documents;
using WriteLite.Documents.Model;
using WriteLite.Resources;
using WriteLite.Services;
using WriteLite.Services.Documents;
using DragEventArgs = System.Windows.DragEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfParagraph = System.Windows.Documents.Paragraph;
// WinForms is enabled in this project; the WPF types are always what is meant here.
using DragDropEffects = System.Windows.DragDropEffects;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace WriteLite.Views.Pages;

/// <summary>
/// Files: opening, saving, exporting, converting and recovering.
/// </summary>
/// <remarks>
/// Every path in and out of the editor is asynchronous and cancellable, and every
/// one reports real progress. The one rule that shapes the rest: <em>the user's
/// file changes only when the user asks it to</em>. Autosave writes to WriteLite's
/// own directory, a PDF is never written back over its source, and an import that
/// loses formatting says so instead of pretending.
/// </remarks>
public partial class EditorPage
{
    private readonly DocumentWorkspaceService _workspace = new();

    /// <summary>
    /// Where unsaved work is snapshotted.
    /// </summary>
    /// <remarks>
    /// Settable so tests get their own directory. Without that they would autosave
    /// into the real application data folder and hand recovery prompts to whatever
    /// ran next — including the user's own next launch.
    /// </remarks>
    internal DocumentRecoveryService Recovery { get; set; } = new();

    private DispatcherTimer? _autosaveTimer;
    private CancellationTokenSource? _fileOperation;
    private bool _isDirty;
    private bool _suppressDirty;

    /// <summary>The file currently open, for surfaces that need to act on it.</summary>
    public string? CurrentDocumentPath => _workspace.CurrentPath;

    /// <summary>
    /// How the page tells the user something went wrong.
    /// </summary>
    /// <remarks>
    /// A seam rather than a direct <c>MessageBox</c> call, for two reasons: the
    /// failure paths are exactly the ones that most need testing and a modal dialog
    /// cannot be driven by a test, and the shell may eventually want these as
    /// in-window notices rather than as dialogs.
    /// </remarks>
    internal Action<string, string> ReportError { get; set; } = static (title, message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>Asks a yes/no question. Returns true to proceed.</summary>
    internal Func<string, string, bool> AskConfirmation { get; set; } = static (title, message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    /// <summary>Opens a document on behalf of the shell, prompting about unsaved work first.</summary>
    public Task OpenFromShellAsync(string path) => OpenPathAsync(path);

    private void InitialiseDocumentState()
    {
        _autosaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = DocumentRecoveryService.Interval
        };
        _autosaveTimer.Tick += async (_, _) => await AutosaveAsync();
        _autosaveTimer.Start();
    }

    private void DisposeDocumentState()
    {
        _autosaveTimer?.Stop();
        _autosaveTimer = null;
        _fileOperation?.Cancel();
        _fileOperation?.Dispose();
        _fileOperation = null;

        // A clean shutdown means the work is either saved or deliberately discarded;
        // either way this session's recovery copy has done its job.
        if (!_isDirty)
        {
            Recovery.Discard();
        }
    }

    // ── State ────────────────────────────────────────────────────────────────

    private void MarkDirty()
    {
        if (_suppressDirty || _isDirty)
        {
            return;
        }

        _isDirty = true;
        DirtyDot.Visibility = Visibility.Visible;
    }

    private void MarkClean()
    {
        _isDirty = false;
        DirtyDot.Visibility = Visibility.Collapsed;
        Recovery.Discard();
    }

    private void UpdateDocumentChrome()
    {
        DocumentTitleText.Text = _workspace.DisplayName;
        DocumentTitleText.ToolTip = _workspace.CurrentPath;

        Controls.Type.SetTracked(
            FormatChip,
            _workspace.CurrentFormat == DocumentFormat.Unknown
                ? Strings.Chips_Draft
                : DocumentFormats.DisplayName(_workspace.CurrentFormat));

        RebuildRecentMenu();
    }

    private void EnsureEmptyDocument()
    {
        if (Editor.Document.Blocks.Count == 0)
        {
            Editor.Document.Blocks.Add(new WpfParagraph());
        }

        UpdateDocumentChrome();
        UpdatePlaceholder();
    }

    // ── Menu ─────────────────────────────────────────────────────────────────

    private void FileMenu_Click(object sender, RoutedEventArgs e)
    {
        RebuildRecentMenu();

        if (FileMenuButton.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = FileMenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void RebuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        var entries = _workspace.Recent.Load();

        if (entries.Count == 0)
        {
            RecentMenu.Items.Add(new MenuItem
            {
                Header = Strings.EditorDoc_RecentEmpty,
                IsEnabled = false,
                Style = TryFindResource("WlMenuItem") as Style
            });
            return;
        }

        foreach (var entry in entries)
        {
            var item = new MenuItem
            {
                Header = entry.Name,
                ToolTip = entry.Path,
                // A file that has since been moved stays listed but unclickable:
                // silently dropping it would make the list look unreliable.
                IsEnabled = entry.Exists,
                Tag = entry.Path,
                Style = TryFindResource("WlMenuItem") as Style
            };

            item.Click += async (_, _) => await OpenPathAsync(entry.Path);
            RecentMenu.Items.Add(item);
        }

        RecentMenu.Items.Add(new Separator { Style = TryFindResource("WlMenuSeparator") as Style });

        var clear = new MenuItem
        {
            Header = Strings.EditorDoc_ClearRecent,
            Style = TryFindResource("WlMenuItem") as Style
        };
        clear.Click += (_, _) =>
        {
            _workspace.Recent.Clear();
            RebuildRecentMenu();
        };
        RecentMenu.Items.Add(clear);
    }

    // ── New ──────────────────────────────────────────────────────────────────

    private void New_Click(object sender, RoutedEventArgs e) => NewDocument();

    private void NewDocument()
    {
        if (!ConfirmDiscardChanges())
        {
            return;
        }

        _suppressDirty = true;
        try
        {
            Editor.Document = new FlowDocument(new WpfParagraph())
            {
                FontFamily = Typography.DefaultFamily,
                FontSize = FlowDocumentBridge.ToPixels(Typography.DefaultSizePt),
                PagePadding = new Thickness(0),
                ColumnWidth = double.PositiveInfinity
            };
        }
        finally
        {
            _suppressDirty = false;
        }

        _workspace.Reset();
        _restoredDocument = false;
        _documentGeneration++;
        _index = null;
        _ignored.Clear();
        _allIssues = [];
        _lastAnalyzedText = string.Empty;
        _lastAnalyzedIssues = [];
        Issues.Clear();

        MarkClean();
        UpdateDocumentChrome();
        UpdatePlaceholder();
        RefreshPresentation();
        Editor.Focus();
    }

    // ── Open ─────────────────────────────────────────────────────────────────

    private async void Open_Click(object sender, RoutedEventArgs e) => await OpenDocumentAsync();

    private async Task OpenDocumentAsync()
    {
        if (!ConfirmDiscardChanges())
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = Strings.EditorDoc_OpenTitle,
            Filter = DocumentFormats.OpenFilter,
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await OpenPathAsync(dialog.FileName);
    }

    /// <summary>
    /// Opens a file, keeping the UI live throughout.
    /// </summary>
    /// <remarks>
    /// Parsing happens on a worker; only the finished model crosses back to the
    /// dispatcher to be turned into a FlowDocument. A 300-page DOCX therefore shows
    /// a progress bar and a working window rather than "Not Responding".
    /// </remarks>
    private async Task OpenPathAsync(string path)
    {
        if (!ConfirmDiscardChanges())
        {
            return;
        }

        CancelFileOperation();
        _fileOperation = new CancellationTokenSource();
        var token = _fileOperation.Token;

        ShowProgress(Strings.EditorDoc_Opening);

        try
        {
            var progress = new Progress<DocumentProgress>(ReportProgress);
            var result = await _workspace.OpenAsync(path, progress, token);
            token.ThrowIfCancellationRequested();

            LoadIntoEditor(result.Document);
            ReportWarnings(result.Warnings, Strings.EditorDoc_Opened);

            MarkClean();
            UpdateDocumentChrome();
            await ScheduleAnalysisAsync(WriteLiteDefaults.Debounce.EditorPostEditRescanDelay);
        }
        catch (OperationCanceledException)
        {
            Controls.Type.SetTracked(StatusText, Strings.Editor_OpeningCancelled);
        }
        catch (DocumentFormatException exception)
        {
            CompatibilityLogger.Technical("document-open-failed", $"reason={exception.Message}");
            ShowError(Strings.EditorDoc_OpenFailed, exception.UserMessage);
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("document-open-failed", exception);
            ShowError(
                Strings.EditorDoc_OpenFailed,
                Strings.EditorDoc_FileUnavailable);
        }
        finally
        {
            HideProgress();
        }
    }

    private void LoadIntoEditor(WlDocument document)
    {
        // Set before the document lands, so a check raised by the resulting TextChanged
        // does not describe this document as restored when it is an ordinary open.
        // RestoreAsync sets it back to true immediately afterwards.
        _restoredDocument = false;
        _suppressDirty = true;
        try
        {
            var flow = FlowDocumentBridge.ToFlowDocument(document, Typography);
            Editor.Document = flow;

            // A freshly loaded document is the undo baseline: undoing past the
            // import into an empty page would be a very unpleasant surprise.
            Editor.IsUndoEnabled = false;
            Editor.IsUndoEnabled = true;
        }
        finally
        {
            _suppressDirty = false;
        }

        _documentGeneration++;
        _index = null;
        _ignored.Clear();
        _allIssues = [];
        _lastAnalyzedText = string.Empty;
        _lastAnalyzedIssues = [];
        Issues.Clear();

        UpdatePlaceholder();
        RefreshPresentation();
        SetZoom(_zoomIndex);
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsAsync();

    private async Task SaveAsync()
    {
        // A document opened from PDF has no in-place save: writing the reconstructed
        // model back over the original would replace the user's file with WriteLite's
        // interpretation of it.
        if (!_workspace.CanSaveInPlace)
        {
            await SaveAsAsync();
            return;
        }

        await WriteAsync(_workspace.CurrentPath!, _workspace.CurrentFormat, isExport: false);
    }

    private async Task SaveAsAsync()
    {
        var format = _workspace.CurrentFormat is DocumentFormat.Unknown or DocumentFormat.Pdf
            ? DocumentFormat.Docx
            : _workspace.CurrentFormat;

        var dialog = new SaveFileDialog
        {
            Title = Strings.EditorDoc_SaveTitle,
            Filter = DocumentFormats.SaveFilter,
            FilterIndex = format switch
            {
                DocumentFormat.Docx => 1,
                DocumentFormat.Odt => 2,
                DocumentFormat.Txt => 3,
                DocumentFormat.Pdf => 4,
                _ => 1
            },
            FileName = _workspace.SuggestFileName(format),
            AddExtension = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var target = DocumentFormats.FromPath(dialog.FileName);
        if (target == DocumentFormat.Unknown)
        {
            target = format;
        }

        await WriteAsync(dialog.FileName, target, isExport: false);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<DocumentFormat>(tag, out var format))
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = string.Format(Strings.EditorDoc_ExportTo, DocumentFormats.DisplayName(format)),
            Filter = $"{DocumentFormats.DisplayName(format)} (*{DocumentFormats.Extension(format)})|*{DocumentFormats.Extension(format)}",
            FileName = _workspace.SuggestFileName(format),
            AddExtension = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await WriteAsync(dialog.FileName, format, isExport: true);
    }

    private async Task WriteAsync(string path, DocumentFormat format, bool isExport)
    {
        CancelFileOperation();
        _fileOperation = new CancellationTokenSource();
        var token = _fileOperation.Token;

        ShowProgress(isExport ? Strings.EditorDoc_Exporting : Strings.EditorDoc_Saving);

        try
        {
            // Reading the FlowDocument must happen on the dispatcher; everything
            // after that is the exporter's own work on a worker thread.
            var model = BuildDocumentModel();
            var progress = new Progress<DocumentProgress>(ReportProgress);

            var result = isExport
                ? await _workspace.ExportAsync(model, path, format, progress, token)
                : await _workspace.SaveAsync(model, path, format, progress, token);

            token.ThrowIfCancellationRequested();

            if (!isExport)
            {
                MarkClean();
                UpdateDocumentChrome();
            }

            ReportWarnings(
                result.Warnings,
                isExport
                    ? string.Format(Strings.EditorDoc_ExportedTo, DocumentFormats.DisplayName(format))
                    : Strings.EditorDoc_Saved);
        }
        catch (OperationCanceledException)
        {
            Controls.Type.SetTracked(StatusText, Strings.Editor_SavingCancelled);
        }
        catch (DocumentFormatException exception)
        {
            CompatibilityLogger.Technical("document-save-failed", $"reason={exception.Message}");
            ShowError(Strings.EditorDoc_SaveFailed, exception.UserMessage);
        }
        catch (UnauthorizedAccessException)
        {
            ShowError(Strings.EditorDoc_SaveFailed, Strings.EditorDoc_NoWritePermission);
        }
        catch (IOException)
        {
            ShowError(Strings.EditorDoc_SaveFailed, Strings.EditorDoc_FileBusy);
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("document-save-failed", exception);
            ShowError(Strings.EditorDoc_SaveFailed, Strings.EditorDoc_UnexpectedSaveError);
        }
        finally
        {
            HideProgress();
        }
    }

    /// <summary>Snapshots the editing surface into the format-independent model.</summary>
    private WlDocument BuildDocumentModel()
    {
        var metadata = _workspace.CurrentMetadata;
        metadata.Title ??= _workspace.DisplayName;
        return FlowDocumentBridge.ToDocumentModel(Editor.Document, metadata);
    }

    // ── Convert ──────────────────────────────────────────────────────────────

    private void Convert_Click(object sender, RoutedEventArgs e) => NavigationRequested?.Invoke("converter");

    // ── Drag and drop ────────────────────────────────────────────────────────

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;

        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] { Length: > 0 } files
            && DocumentFormats.FromPath(files[0]) != DocumentFormat.Unknown)
        {
            e.Effects = DragDropEffects.Copy;
        }

        e.Handled = true;
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] { Length: > 0 } files)
        {
            return;
        }

        var path = files[0];
        if (DocumentFormats.FromPath(path) == DocumentFormat.Unknown)
        {
            ShowError(
                Strings.EditorDoc_UnsupportedFormat,
                Strings.EditorDoc_SupportedFormats);
            return;
        }

        await OpenPathAsync(path);
    }

    // ── Autosave and recovery ────────────────────────────────────────────────

    private async Task AutosaveAsync()
    {
        if (!_isDirty || _fileOperation is not null)
        {
            return;
        }

        try
        {
            var model = BuildDocumentModel();
            await Recovery.SaveSnapshotAsync(model, _workspace.CurrentPath);
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("editor-autosave-failed", exception);
        }
    }

    /// <summary>
    /// Offers back work left behind by a session that did not close cleanly.
    /// </summary>
    /// <remarks>
    /// Called by the shell, deliberately not from <c>Loaded</c>. Putting a modal
    /// question on the load path means the page cannot be placed in a visual tree
    /// without interrogating the user — which is wrong for any preview or secondary
    /// host, and makes the page untestable.
    ///
    /// Asked once, and only when a snapshot from another session actually exists.
    /// Declining deletes it: leaving a copy of someone's document in the application
    /// directory after they said no would be a privacy problem.
    /// </remarks>
    public void CheckForRecoverableWork()
    {
        if (_recoveryOffered)
        {
            return;
        }

        _recoveryOffered = true;
        OfferRecoveryIfAny();
    }

    private bool _recoveryOffered;

    private void OfferRecoveryIfAny()
    {
        var orphans = Recovery.FindOrphaned();
        if (orphans.Count == 0)
        {
            return;
        }

        var snapshot = orphans[0];

        var restore = AskConfirmation(
            Strings.EditorDoc_RecoveryTitle,
            string.Format(
                Strings.EditorDoc_RecoveryMessage,
                snapshot.DisplayName,
                snapshot.SavedAt.ToString("dd.MM.yyyy HH:mm")));

        if (!restore)
        {
            Recovery.DiscardSnapshot(snapshot);
            return;
        }

        _ = RestoreAsync(snapshot);
    }

    private async Task RestoreAsync(RecoverySnapshot snapshot)
    {
        ShowProgress(Strings.EditorDoc_Restoring);

        try
        {
            var result = await _workspace.OpenAsync(snapshot.SnapshotPath);
            LoadIntoEditor(result.Document);
            _restoredDocument = true;

            // The recovered content belongs to the original file, not to the
            // snapshot in the application directory — saving must not overwrite it.
            if (!string.IsNullOrEmpty(snapshot.OriginalPath))
            {
                _workspace.AdoptImported(result.Document, snapshot.OriginalPath);
            }
            else
            {
                _workspace.Reset(Strings.EditorDoc_RestoredDocument);
            }

            Recovery.DiscardSnapshot(snapshot);
            MarkDirty();
            UpdateDocumentChrome();
            await ScheduleAnalysisAsync(WriteLiteDefaults.Debounce.EditorPostEditRescanDelay);
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("document-recovery-failed", exception);
            ShowError(Strings.EditorDoc_RecoveryFailed, Strings.EditorDoc_RecoveryCorrupted);
            Recovery.DiscardSnapshot(snapshot);
        }
        finally
        {
            HideProgress();
        }
    }

    // ── Progress and messages ────────────────────────────────────────────────

    private void ShowProgress(string stage)
    {
        Controls.Type.SetTracked(ProgressStageText, stage);
        ProgressIndicator.IsIndeterminate = true;
        ProgressIndicator.Value = 0;
        ProgressBar.Visibility = Visibility.Visible;
    }

    private void ReportProgress(DocumentProgress progress)
    {
        Controls.Type.SetTracked(ProgressStageText, progress.Stage.ToUpperInvariant());

        // Indeterminate when the total is genuinely unknown. A bar that crawls to
        // 90% and waits is a lie about what the program is doing.
        if (progress.Fraction is { } fraction)
        {
            ProgressIndicator.IsIndeterminate = false;
            ProgressIndicator.Value = Math.Clamp(fraction * 100, 0, 100);
        }
        else
        {
            ProgressIndicator.IsIndeterminate = true;
        }
    }

    private void HideProgress()
    {
        ProgressBar.Visibility = Visibility.Collapsed;
        _fileOperation?.Dispose();
        _fileOperation = null;
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e) => CancelFileOperation();

    private void CancelFileOperation()
    {
        _fileOperation?.Cancel();
        _fileOperation?.Dispose();
        _fileOperation = null;
    }

    /// <summary>
    /// Surfaces what an import or export could not preserve.
    /// </summary>
    /// <remarks>
    /// In the status bar rather than in a dialog. These are informational — the
    /// document opened, the file was written — and interrupting the writer to
    /// acknowledge "tables were flattened" would be worse than telling them quietly.
    /// </remarks>
    private void ReportWarnings(IReadOnlyList<DocumentWarning> warnings, string success)
    {
        if (warnings.Count == 0)
        {
            Controls.Type.SetTracked(StatusText, success.ToUpperInvariant());
            return;
        }

        Controls.Type.SetTracked(StatusText, $"{success.ToUpperInvariant()} · {warnings.Count}");

        var detail = string.Join("\n", warnings.Select(warning => "• " + warning.Message));
        StatusText.ToolTip = detail;

        CompatibilityLogger.Technical(
            "document-warnings",
            $"count={warnings.Count} codes={string.Join(",", warnings.Select(warning => warning.Code))}");
    }

    private void ShowError(string title, string message) => ReportError(title, message);

    /// <summary>Returns false when the user chose to keep editing rather than lose changes.</summary>
    private bool ConfirmDiscardChanges()
    {
        if (!_isDirty)
        {
            return true;
        }

        return AskConfirmation(
            Strings.EditorDoc_UnsavedChangesTitle,
            Strings.EditorDoc_UnsavedChangesMessage);
    }
}
