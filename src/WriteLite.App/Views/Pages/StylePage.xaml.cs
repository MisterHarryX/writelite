using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WriteLite.Documents;
using WriteLite.Resources;
using WriteLite.Services;
using WriteLite.Services.Settings;
using WriteLite.Services.Spelling;
using WriteLite.Services.Personal;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Views.Pages;

/// <summary>
/// «Мой стиль письма»: what WriteLite has learned about how the user writes, the switches that
/// govern it, and the calibration that seeds it.
/// </summary>
public partial class StylePage : UserControl
{
    private WriteLiteAppSettings _settings = new();
    private PersonalStyleService? _style;
    private UserDictionaryService? _dictionary;
    private bool _loading;

    // Calibration state.
    private int _promptIndex;
    private int _calibrationWords;
    private bool _calibrating;

    public StylePage()
    {
        InitializeComponent();
        System.Windows.DataObject.AddPastingHandler(CalibrationInput, OnCalibrationPaste);
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) =>
        {
            if (_style is not null) _style.Changed -= OnStyleChanged;
        };
    }

    /// <summary>Raised when a switch on this page changed the shared settings.</summary>
    public event Action<WriteLiteAppSettings>? SettingsChanged;

    /// <summary>Raised when a word was added to the user dictionary from this page.</summary>
    public event Action? DictionaryChanged;

    /// <summary>True while the calibration is on screen.</summary>
    public bool IsCalibrating => _calibrating;

    public void Bind(WriteLiteAppSettings settings, PersonalStyleService? style, UserDictionaryService? dictionary)
    {
        _settings = settings;
        if (_style is not null) _style.Changed -= OnStyleChanged;
        _style = style;
        _dictionary = dictionary;
        if (_style is not null) _style.Changed += OnStyleChanged;
        Refresh();
    }

    /// <summary>Re-reads the settings and the library. Cheap; called whenever the page comes into view.</summary>
    public void Refresh()
    {
        _loading = true;
        ChkMode.IsChecked = _settings.PersonalStyleEnabled;
        ChkPrediction.IsChecked = _settings.PersonalWordPrediction;
        ChkLearning.IsChecked = _settings.PersonalStyleLearning;
        ChkOtherApps.IsChecked = _settings.PersonalStyleLearnInOtherApps;
        ChkPrediction.IsEnabled = _settings.PersonalStyleEnabled;
        ChkOtherApps.IsEnabled = _settings.PersonalStyleLearning;
        _loading = false;

        var snapshot = _style?.Snapshot() ?? PersonalStyleSnapshot.Empty;
        RenderCalibrationCard(snapshot);
        RenderProfile(snapshot);
    }

    private void OnStyleChanged(object? sender, EventArgs e)
    {
        // Raised on the learning worker, and often while the user types elsewhere. The page
        // only needs the latest picture, at background priority — and IsVisible is a
        // dependency property, so even the question is asked on the dispatcher.
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (IsVisible && !_calibrating) Refresh();
        });
    }

    // ── Overview ──────────────────────────────────────────────────────────────

    private void RenderCalibrationCard(PersonalStyleSnapshot snapshot)
    {
        var done = _settings.StyleCalibrationCompleted;
        var total = StyleCalibration.Prompts.Count;
        var count = Math.Min(snapshot.CalibrationTexts, total);

        CalibrationCardTitle.Text = done ? Strings.Style_CalibrationDoneTitle : Strings.Style_CalibrationTitle;
        CalibrationCardBody.Text = done ? Strings.Style_CalibrationDoneBody : Strings.Style_CalibrationBody;
        StartCalibrationButton.Content = done ? Strings.Style_RepeatCalibration : Strings.Style_StartCalibration;
        CalibrationProgressText.Text = count == 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Style_CalibrationEstimate, total)
            : string.Format(CultureInfo.CurrentCulture, Strings.Style_CalibrationProgress, count, total);
    }

    private void RenderProfile(PersonalStyleSnapshot s)
    {
        var culture = CultureInfo.CurrentCulture;

        StatWords.Text = s.TotalWords.ToString("N0", culture);
        StatWordsDesc.Text = string.Format(culture, Strings.Style_StatWordsDesc, s.DistinctWords.ToString("N0", culture));

        StatSentence.Text = s.Sentences == 0 ? "—" : string.Format(culture, Strings.Style_StatSentenceValue, s.AverageSentenceLength.ToString("0.#", culture));
        StatSentenceDesc.Text = s.Sentences == 0
            ? Strings.Style_StatNotEnough
            : string.Format(culture, Strings.Style_StatSentenceDesc, s.CommasPerSentence.ToString("0.#", culture));

        var (level, levelDesc) = s.TotalWords switch
        {
            < 300 => (Strings.Style_LevelStart, Strings.Style_LevelStartDesc),
            < 3000 => (Strings.Style_LevelBasic, Strings.Style_LevelBasicDesc),
            _ => (Strings.Style_LevelConfident, Strings.Style_LevelConfidentDesc),
        };
        StatLevel.Text = level;
        StatLevelDesc.Text = levelDesc;

        if (s.YoShare is { } yo)
        {
            StatYo.Text = Percent(yo);
            StatYoDesc.Text = yo >= 0.9 ? Strings.Style_StatYoAlways : yo <= 0.1 ? Strings.Style_StatYoNever : Strings.Style_StatYoMixed;
        }
        else
        {
            StatYo.Text = "—";
            StatYoDesc.Text = Strings.Style_StatNotEnough;
        }

        if (s.GuillemetShare is { } q)
        {
            StatQuotes.Text = q >= 0.5 ? Strings.Style_QuotesGuillemets : Strings.Style_QuotesStraight;
            StatQuotesDesc.Text = string.Format(culture, Strings.Style_StatQuotesDesc, Percent(Math.Max(q, 1 - q)));
        }
        else
        {
            StatQuotes.Text = "—";
            StatQuotesDesc.Text = Strings.Style_StatNotEnough;
        }

        StatPredictions.Text = s.PredictionsAccepted.ToString("N0", culture);

        TopWordsList.ItemsSource = s.TopWords.Select(w => new { w.Word, Count = w.Count.ToString("N0", culture) }).ToArray();
        TopWordsEmpty.Visibility = s.TopWords.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UnknownList.ItemsSource = s.FrequentUnknown
            .Where(u => _dictionary?.Contains(u.Word) != true)
            .Select(u => new
            {
                u.Word,
                Summary = string.Format(culture, Strings.Style_UnknownSummary, u.Count, u.Days),
            })
            .ToArray();
        UnknownEmpty.Visibility = UnknownList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        CorrectionsList.ItemsSource = s.Corrections
            .Select(c => new CorrectionRow(c.Typed, c.Intended, string.Format(culture, Strings.Style_CorrectionSummary, c.Count)))
            .ToArray();
        CorrectionsEmpty.Visibility = s.Corrections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        static string Percent(double value) => Math.Round(value * 100).ToString("0", CultureInfo.CurrentCulture) + " %";
    }

    private sealed record CorrectionRow(string Typed, string Intended, string Summary);

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings.PersonalStyleEnabled = ChkMode.IsChecked == true;
        _settings.PersonalWordPrediction = ChkPrediction.IsChecked == true;
        _settings.PersonalStyleLearning = ChkLearning.IsChecked == true;
        _settings.PersonalStyleLearnInOtherApps = ChkOtherApps.IsChecked == true;
        ChkPrediction.IsEnabled = _settings.PersonalStyleEnabled;
        ChkOtherApps.IsEnabled = _settings.PersonalStyleLearning;
        SettingsChanged?.Invoke(_settings);
    }

    private void AddUnknown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string word } || _dictionary is null) return;

        _dictionary.Add(word);
        _style?.ForgetUnknown(word);
        DictionaryChanged?.Invoke();
        Refresh();
    }

    private void DismissUnknown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string word }) return;
        _style?.ForgetUnknown(word);
        Refresh();
    }

    private void ForgetCorrection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CorrectionRow row }) return;
        _style?.ForgetCorrection(row.Typed, row.Intended);
        Refresh();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_style is null) return;

        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            Strings.Style_ResetConfirm,
            Strings.Style_ResetTitle,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        _style.Reset();
        _settings.StyleCalibrationCompleted = false;
        SettingsChanged?.Invoke(_settings);
        CompatibilityLogger.State("personal-style-reset");
        Refresh();
    }

    /// <summary>
    /// Learns from documents the user declares as their own.
    /// </summary>
    /// <remarks>
    /// Read with the same importers the editor uses, so anything the editor can open can teach
    /// the library. Nothing is copied: the text is learned and dropped.
    /// </remarks>
    private async void ImportTexts_Click(object sender, RoutedEventArgs e)
    {
        if (_style is null) return;
        if (!_settings.PersonalStyleLearning)
        {
            ShowImportStatus(Strings.Style_ImportLearningOff);
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Strings.Style_ImportDialogTitle,
            Filter = DocumentFormats.OpenFilter,
            Multiselect = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var serializer = new DocumentSerializer();
        var files = 0;
        var words = 0;
        var failed = 0;
        ShowImportStatus(Strings.Style_ImportRunning);

        foreach (var path in dialog.FileNames)
        {
            try
            {
                var imported = await serializer.LoadAsync(path).ConfigureAwait(true);
                var text = imported.Document.ToPlainText();
                words += await _style.ObserveAsync(text, StyleLearningSource.Import).ConfigureAwait(true);
                files++;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failed++;
                CompatibilityLogger.Technical("personal-style-import-failed", $"type={exception.GetType().Name} ext={Path.GetExtension(path)}");
            }
        }

        ShowImportStatus(failed == 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Style_ImportDone, words, files)
            : string.Format(CultureInfo.CurrentCulture, Strings.Style_ImportDonePartial, words, files, failed));
        Refresh();
    }

    private void ShowImportStatus(string text)
    {
        ImportStatusText.Text = text;
        ImportStatusText.Visibility = Visibility.Visible;
    }

    // ── Calibration ───────────────────────────────────────────────────────────

    private void StartCalibration_Click(object sender, RoutedEventArgs e) => StartCalibration();

    /// <summary>Puts the calibration on screen, at its first topic.</summary>
    public void StartCalibration()
    {
        _promptIndex = 0;
        _calibrationWords = 0;
        _calibrating = true;
        OverviewView.Visibility = Visibility.Collapsed;
        CalibrationView.Visibility = Visibility.Visible;
        ShowPrompt();
        CompatibilityLogger.State("style-calibration-started");
    }

    private void ShowPrompt()
    {
        var prompts = StyleCalibration.Prompts;
        var prompt = prompts[_promptIndex];

        Controls.Type.SetTracked(
            CalibrationStepText,
            string.Format(CultureInfo.CurrentCulture, Strings.Style_CalibrationStep, _promptIndex + 1, prompts.Count).ToUpperInvariant());
        CalibrationPromptTitle.Text = prompt.Title;
        CalibrationPromptHint.Text = prompt.Hint;
        CalibrationNextButton.Content = _promptIndex == prompts.Count - 1 ? Strings.Style_CalibrationFinish : Strings.Style_CalibrationNext;
        CalibrationPasteHint.Visibility = Visibility.Collapsed;
        CalibrationInput.Text = string.Empty;
        UpdateCalibrationCounter();
        CalibrationInput.Focus();
    }

    private void CalibrationInput_TextChanged(object sender, TextChangedEventArgs e) => UpdateCalibrationCounter();

    private void UpdateCalibrationCounter()
    {
        var words = StyleCalibration.CountWords(CalibrationInput.Text);
        CalibrationCounter.Text = string.Format(CultureInfo.CurrentCulture, Strings.Style_CalibrationCounter, words, StyleCalibration.MinimumWords);
        CalibrationNextButton.IsEnabled = words >= StyleCalibration.MinimumWords;

        var track = (CalibrationProgressFill.Parent as FrameworkElement)?.Width ?? 320;
        CalibrationProgressFill.Width = Math.Min(1.0, (double)words / StyleCalibration.TargetWords) * (double.IsNaN(track) ? 320 : track);
    }

    /// <summary>The texts are typed, not pasted — see <see cref="StyleCalibration"/>.</summary>
    private void OnCalibrationPaste(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        CalibrationPasteHint.Visibility = Visibility.Visible;
    }

    private async void NextPrompt_Click(object sender, RoutedEventArgs e)
    {
        var text = CalibrationInput.Text;
        if (StyleCalibration.CountWords(text) < StyleCalibration.MinimumWords) return;

        CalibrationNextButton.IsEnabled = false;
        if (_style is not null)
        {
            _calibrationWords += await _style.ObserveAsync(text, StyleLearningSource.Calibration).ConfigureAwait(true);
            _style.RecordCalibrationText();
        }

        Advance();
    }

    private void SkipPrompt_Click(object sender, RoutedEventArgs e) => Advance();

    private void Advance()
    {
        if (_promptIndex < StyleCalibration.Prompts.Count - 1)
        {
            _promptIndex++;
            ShowPrompt();
            return;
        }

        FinishCalibration(completed: true);
    }

    private void CancelCalibration_Click(object sender, RoutedEventArgs e) => FinishCalibration(completed: false);

    private void FinishCalibration(bool completed)
    {
        _calibrating = false;
        CalibrationInput.Text = string.Empty;
        CalibrationView.Visibility = Visibility.Collapsed;
        OverviewView.Visibility = Visibility.Visible;

        // Finishing the last topic completes the calibration even if some were skipped: the
        // user saw every topic, and the library keeps learning from ordinary writing anyway.
        if (completed && !_settings.StyleCalibrationCompleted)
        {
            _settings.StyleCalibrationCompleted = true;
            SettingsChanged?.Invoke(_settings);
        }

        CompatibilityLogger.Technical("style-calibration-finished", $"completed={(completed ? 1 : 0)} words={_calibrationWords}");
        if (completed)
        {
            ShowImportStatus(string.Format(CultureInfo.CurrentCulture, Strings.Style_CalibrationResult, _calibrationWords));
        }

        Refresh();
    }
}
