using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WriteLite.Documents;
using WriteLite.Services;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Views.Pages;

/// <summary>
/// Standalone file-to-file conversion.
/// </summary>
/// <remarks>
/// Separate from the editor on purpose: converting a folder of documents is a
/// different job from writing one, and forcing it through "open, then save as"
/// would mean rendering a document nobody wants to read.
///
/// Everything runs through <see cref="DocumentConversionService"/>, so the formats
/// this page offers are exactly the ones that actually work — the target list is
/// generated from the converter rather than written out by hand, and a combination
/// that is not supported cannot be selected.
/// </remarks>
public partial class ConverterPage : UserControl
{
    private readonly DocumentConversionService _converter = new();

    private CancellationTokenSource? _operation;
    private string? _inputPath;
    private string? _outputPath;
    private string? _resultPath;

    public ConverterPage()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user asks to open a converted file in the editor.</summary>
    public event Action<string>? OpenInEditorRequested;

    /// <summary>Called when the page comes into view.</summary>
    public void Reload()
    {
        // The chosen file and target survive a visit elsewhere: coming back to find
        // the form cleared would be worse than finding it as it was left.
        UpdateReadiness();
    }

    /// <summary>Pre-selects a file, used when the editor hands one over.</summary>
    public void PrepareFor(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            SetInput(path);
        }
    }

    // ── Input ────────────────────────────────────────────────────────────────

    private void ChooseInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите файл для преобразования",
            Filter = DocumentFormats.OpenFilter,
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            SetInput(dialog.FileName);
        }
    }

    private void SetInput(string path)
    {
        var format = DocumentFormats.FromPath(path);

        if (format == DocumentFormat.Unknown)
        {
            ShowError("WriteLite открывает документы DOCX, ODT, PDF и TXT.");
            return;
        }

        _inputPath = path;
        _resultPath = null;
        ResultPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;

        InputNameText.Text = Path.GetFileName(path);

        var info = new FileInfo(path);
        InputDetailText.Text =
            $"{DocumentFormats.DisplayName(format)} · {FormatSize(info.Length)} · {Path.GetDirectoryName(path)}";

        RebuildTargets(format);
        UpdateReadiness();
    }

    /// <summary>
    /// Offers every format the converter can actually produce from this source.
    /// </summary>
    /// <remarks>
    /// Generated from <see cref="IDocumentConverter.CanConvert"/> rather than
    /// hard-coded, so the page can never advertise a conversion the engine does not
    /// implement. The source's own format is excluded: converting DOCX to DOCX is a
    /// copy, not a conversion.
    /// </remarks>
    private void RebuildTargets(DocumentFormat source)
    {
        var targets = DocumentFormats.Exportable
            .Where(format => format != source && _converter.CanConvert(source, format))
            .ToArray();

        TargetBox.ItemsSource = targets.Select(DocumentFormats.DisplayName).ToArray();
        TargetBox.Tag = targets;

        if (targets.Length > 0)
        {
            TargetBox.SelectedIndex = 0;
        }
    }

    private DocumentFormat SelectedTarget
    {
        get
        {
            if (TargetBox.Tag is DocumentFormat[] targets
                && TargetBox.SelectedIndex >= 0
                && TargetBox.SelectedIndex < targets.Length)
            {
                return targets[TargetBox.SelectedIndex];
            }

            return DocumentFormat.Unknown;
        }
    }

    private void Target_Changed(object sender, SelectionChangedEventArgs e)
    {
        var target = SelectedTarget;

        TargetNoteText.Text = target switch
        {
            DocumentFormat.Txt => "Останется только текст: оформление, таблицы и изображения не переносятся.",
            DocumentFormat.Pdf => "PDF собирается заново из содержимого документа — вёрстка может отличаться от исходной.",
            DocumentFormat.Docx or DocumentFormat.Odt =>
                "Переносятся текст, заголовки, списки, начертание, выравнивание, отступы и таблицы.",
            _ => string.Empty
        };

        // A target chosen after the path was set has to update the extension, or the
        // user gets a PDF named .docx.
        if (_inputPath is not null)
        {
            _outputPath = SuggestOutputPath(_inputPath, target);
            OutputPathText.Text = _outputPath ?? "—";
        }

        UpdateReadiness();
    }

    private static string? SuggestOutputPath(string inputPath, DocumentFormat target)
    {
        if (target == DocumentFormat.Unknown)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(inputPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var candidate = Path.Combine(directory, name + DocumentFormats.Extension(target));

        // Never propose overwriting something already there.
        var attempt = 1;
        while (File.Exists(candidate) && attempt < 100)
        {
            candidate = Path.Combine(directory, $"{name} ({attempt}){DocumentFormats.Extension(target)}");
            attempt++;
        }

        return candidate;
    }

    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedTarget;
        if (target == DocumentFormat.Unknown)
        {
            return;
        }

        var extension = DocumentFormats.Extension(target);
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить результат как",
            Filter = $"{DocumentFormats.DisplayName(target)} (*{extension})|*{extension}",
            FileName = Path.GetFileName(_outputPath ?? "converted" + extension),
            InitialDirectory = Path.GetDirectoryName(_outputPath ?? _inputPath ?? string.Empty),
            AddExtension = true
        };

        if (dialog.ShowDialog() == true)
        {
            _outputPath = dialog.FileName;
            OutputPathText.Text = _outputPath;
            UpdateReadiness();
        }
    }

    private void UpdateReadiness() =>
        ConvertButton.IsEnabled = _inputPath is not null
                                  && _outputPath is not null
                                  && SelectedTarget != DocumentFormat.Unknown
                                  && _operation is null;

    // ── Conversion ───────────────────────────────────────────────────────────

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_inputPath is null || _outputPath is null)
        {
            return;
        }

        var target = SelectedTarget;
        if (target == DocumentFormat.Unknown)
        {
            return;
        }

        _operation?.Cancel();
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        var token = _operation.Token;

        ResultPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        ConvertButton.IsEnabled = false;
        Motion.Reveal(ProgressPanel, offset: 4);

        try
        {
            var progress = new Progress<DocumentProgress>(ReportProgress);
            var result = await _converter.ConvertAsync(_inputPath, _outputPath, target, progress, token);

            token.ThrowIfCancellationRequested();
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            ShowError("Преобразование отменено. Файл не создан.");
        }
        catch (DocumentFormatException exception)
        {
            CompatibilityLogger.Technical("conversion-failed", $"reason={exception.Message}");
            ShowError(exception.UserMessage);
        }
        catch (UnauthorizedAccessException)
        {
            ShowError("Нет прав на запись в выбранную папку. Выберите другое расположение.");
        }
        catch (IOException)
        {
            ShowError("Файл занят другой программой. Закройте его и попробуйте снова.");
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("conversion-failed", $"type={exception.GetType().Name}");
            ShowError("Не удалось выполнить преобразование. Проверьте исходный файл.");
        }
        finally
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            _operation?.Dispose();
            _operation = null;
            UpdateReadiness();
        }
    }

    private void ReportProgress(DocumentProgress progress)
    {
        Controls.Type.SetTracked(ProgressStageText, progress.Stage.ToUpperInvariant());

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

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();

    private void ShowResult(DocumentConversionResult result)
    {
        _resultPath = result.OutputPath;

        var size = new FileInfo(result.OutputPath).Length;
        ResultDetailText.Text =
            $"{DocumentFormats.DisplayName(result.From)} → {DocumentFormats.DisplayName(result.To)} · " +
            $"{Path.GetFileName(result.OutputPath)} · {FormatSize(size)} · {result.Duration.TotalSeconds:0.0} с";

        // Warnings are shown, not swallowed: a conversion that dropped tables is
        // still a success, and the user is entitled to know before they rely on it.
        if (result.Warnings.Count > 0)
        {
            ResultWarningsText.Text = string.Join("\n", result.Warnings.Select(warning => "• " + warning.Message));
            ResultWarningsText.Visibility = Visibility.Visible;
        }
        else
        {
            ResultWarningsText.Visibility = Visibility.Collapsed;
        }

        ResultPanel.Visibility = Visibility.Visible;
        Motion.Reveal(ResultPanel, offset: 6);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        Motion.Reveal(ErrorPanel, offset: 4);
    }

    // ── Result actions ───────────────────────────────────────────────────────

    private void OpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (_resultPath is null || !File.Exists(_resultPath))
        {
            return;
        }

        TryShellExecute(_resultPath);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_resultPath is null || !File.Exists(_resultPath))
        {
            return;
        }

        try
        {
            // /select highlights the file rather than just opening its folder.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_resultPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowError("Не удалось открыть папку.");
        }
    }

    private void OpenInEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_resultPath is not null && File.Exists(_resultPath))
        {
            OpenInEditorRequested?.Invoke(_resultPath);
        }
    }

    private void ConvertAnother_Click(object sender, RoutedEventArgs e)
    {
        ResultPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ChooseInput_Click(sender, e);
    }

    private static void TryShellExecute(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No handler registered for the type; nothing here is worth a dialog.
            CompatibilityLogger.Technical("conversion-open-failed", $"type={exception.GetType().Name}");
        }
    }

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

    private void Page_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            SetInput(files[0]);
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        _ => $"{bytes / (1024.0 * 1024):0.#} МБ"
    };
}
