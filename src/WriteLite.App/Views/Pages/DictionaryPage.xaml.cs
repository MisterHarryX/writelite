using System.Windows;
using System.Windows.Input;
using WriteLite.Controls;
using WriteLite.Services;
using WriteLite.Services.Lexical;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Views.Pages;

/// <summary>
/// Word lookup: a reading surface over the installed dictionaries.
/// </summary>
/// <remarks>
/// Lookup reuses <see cref="ILexicalKnowledgeService"/> — the same offline service that
/// backs the double-click word card over other applications — so the page adds a surface
/// rather than a second source of dictionary data. English translations come from
/// <see cref="TranslationIndex"/>, built from the same pinned OpenRussian release as the
/// Russian pack. Nothing on this page is generated: when the packs have no answer, the
/// page says so rather than filling the space.
///
/// Synonyms, antonyms and translations are navigable, which turns the page from a lookup
/// box into something a reader can walk through, and the walk is retraceable through the
/// same back / forward model a browser uses.
/// </remarks>
public partial class DictionaryPage : UserControl
{
    /// <summary>
    /// How long a lookup may take before the skeleton appears.
    /// </summary>
    /// <remarks>
    /// A cached or database-backed lookup returns in well under this, and flashing a
    /// skeleton for 20 ms reads as a glitch. The placeholder is for the case where the
    /// pack is still warming up in the background at startup, which genuinely takes time.
    /// </remarks>
    private static readonly TimeSpan SkeletonDelay = TimeSpan.FromMilliseconds(140);

    /// <summary>Trail length. Long enough to retrace a session, short enough to stay a line.</summary>
    private const int MaxHistory = 40;

    private readonly LexicalLanguageDetector _languages = new();
    private readonly List<HistoryEntry> _history = [];
    private int _historyIndex = -1;

    private ILexicalKnowledgeService? _lexical;
    private TranslationIndex? _translations;
    private CancellationTokenSource? _lookupCancellation;
    private CancellationTokenSource? _skeletonCancellation;
    private long _requestId;

    public DictionaryPage()
    {
        InitializeComponent();
    }

    public void Bind(ILexicalKnowledgeService lexical, TranslationIndex? translations = null)
    {
        _lexical = lexical;
        _translations = translations;
    }

    /// <summary>Called when the page comes into view.</summary>
    public void Reload()
    {
        // Nothing to re-read: the article is whatever the reader last looked up, and
        // resetting it on every visit would throw away the trail they are walking.
    }

    // ── Lookup ───────────────────────────────────────────────────────────────

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        _ = ShowWordAsync((SearchBox.Text ?? string.Empty).Trim());
    }

    /// <summary>
    /// Opens a word's article, as though the reader had searched for it.
    /// </summary>
    /// <param name="language">
    /// Leave unset to let the language detector decide, which is right for a word
    /// arriving from outside the page.
    /// </param>
    public Task ShowWordAsync(string word, LexicalLanguage language = LexicalLanguage.Unknown) =>
        LookupAsync(word, language, recordHistory: true);

    /// <summary>
    /// Looks a word up and renders it.
    /// </summary>
    /// <param name="language">
    /// The dictionary to search. <see cref="LexicalLanguage.Unknown"/> lets the detector
    /// decide, which is right for typed input; a chip knows its own language and passes
    /// it, so clicking the English translation of a Russian word opens the English entry
    /// rather than being re-detected into the wrong pack.
    /// </param>
    /// <param name="recordHistory">
    /// False when the navigation is itself a history move, otherwise going back would
    /// push a new entry and the reader could never leave.
    /// </param>
    private async Task LookupAsync(string word, LexicalLanguage language, bool recordHistory)
    {
        _lookupCancellation?.Cancel();
        _lookupCancellation?.Dispose();
        _lookupCancellation = new CancellationTokenSource();
        var token = _lookupCancellation.Token;

        if (string.IsNullOrWhiteSpace(word))
        {
            ShowLookupMessage("Найдите слово",
                "Введите слово, чтобы увидеть его значение, формы, синонимы, антонимы и перевод.");
            return;
        }

        if (_lexical is null)
        {
            ShowLookupMessage("Словарь ещё загружается",
                "Словарные данные готовятся в фоне. Проверка текста продолжает работать.");
            return;
        }

        SearchBox.Text = word;
        var detected = language == LexicalLanguage.Unknown ? _languages.DetectWord(word) : language;

        var request = new LexicalLookupRequest(
            Word: word,
            Sentence: word,
            FullText: word,
            Start: 0,
            Length: word.Length,
            SourceLanguage: detected,
            RequestId: ++_requestId);

        ShowSkeletonAfterDelay(token);

        try
        {
            var result = await _lexical.LookupAsync(request, token);
            if (token.IsCancellationRequested || result.RequestId != _requestId)
            {
                return;
            }

            // Translations are two indexed reads, but they happen on whatever thread
            // the lookup completed on rather than blocking the render.
            var translations = await Task.Run(
                () => ResolveTranslations(result, detected), token);

            if (token.IsCancellationRequested || result.RequestId != _requestId)
            {
                return;
            }

            if (recordHistory)
            {
                PushHistory(word, detected);
            }

            Render(result, detected, translations);
        }
        catch (OperationCanceledException)
        {
            // A newer query owns the article now.
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("dictionary-lookup-failed", $"type={exception.GetType().Name}");
            ShowLookupMessage("Не удалось выполнить поиск",
                "Словарные данные недоступны. Проверка текста продолжает работать.");
        }
    }

    /// <summary>
    /// Resolves English translations, and marks which of them actually open an article.
    /// </summary>
    /// <remarks>
    /// Runs off the dispatcher: a handful of indexed reads is fast, but the double-click
    /// card shares this service with whatever the user is typing in another application,
    /// and the dictionary page is not entitled to hold that thread.
    /// </remarks>
    private ChipModel[] ResolveTranslations(LexicalLookupResult result, LexicalLanguage language)
    {
        if (_translations is null)
        {
            return [];
        }

        // The lemma is what the index is keyed by; a surface form such as "красивого"
        // has to resolve through the lemma the lookup already established.
        var target = language == LexicalLanguage.English ? LexicalLanguage.Russian : LexicalLanguage.English;
        var values = _translations.Translate(result.Lemma, language);
        if (values.Count == 0 && !string.Equals(result.Lemma, result.Word, StringComparison.OrdinalIgnoreCase))
        {
            values = _translations.Translate(result.Word, language);
        }

        return values.Select(value => new ChipModel(value, target, HasArticle(value, target))).ToArray();
    }

    private ChipModel[] ResolveChips(IReadOnlyList<LexicalSuggestion>? suggestions, LexicalLanguage language)
    {
        if (suggestions is null || suggestions.Count == 0)
        {
            return [];
        }

        return suggestions
            .Select(suggestion => suggestion.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Select(value => new ChipModel(value, language, HasArticle(value, language)))
            .ToArray();
    }

    /// <summary>True when the installed packs can actually show an article for a word.</summary>
    private bool HasArticle(string word, LexicalLanguage language)
    {
        if (_lexical is not OfflineLexicalKnowledgeService offline)
        {
            // A service that cannot be probed: treat every chip as navigable rather
            // than greying out words that would in fact have opened.
            return true;
        }

        var probe = language == LexicalLanguage.English
            ? TranslationIndex.Normalize(word, language)
            : word;
        return offline.LookupEntry(probe, language) is not null;
    }

    private void Render(LexicalLookupResult result, LexicalLanguage language, ChipModel[] translations)
    {
        if (result.IsEmpty && result.Definitions.Count == 0 && result.Synonyms.Count == 0 && translations.Length == 0)
        {
            ShowLookupMessage(
                "Ничего не найдено",
                result.StatusMessage ?? $"Слова «{result.Word}» нет в установленных словарях.");
            return;
        }

        FinishLoading();
        LookupEmptyState.Visibility = Visibility.Collapsed;
        Article.Visibility = Visibility.Visible;

        WordText.Text = result.Word;
        PartOfSpeechText.Text = PartOfSpeechName(result.PartOfSpeech);
        LemmaText.Text = string.Equals(result.Lemma, result.Word, StringComparison.CurrentCultureIgnoreCase)
            ? string.Empty
            : result.Lemma;

        SetText(SurfaceNoteText, result.SurfaceFormNote);

        var definitions = result.Definitions
            .Select((definition, index) => new DefinitionRow(
                Number: (index + 1).ToString("00"),
                Text: definition.Definition,
                Meta: BuildDefinitionMeta(definition)))
            .ToArray();
        DefinitionsList.ItemsSource = definitions;
        DefinitionsBlock.Visibility = definitions.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // The label names the direction, so a reader of an English entry is not told
        // that Russian words are its "translation" without saying into what.
        Controls.Type.SetTracked(
            TranslationsLabel,
            language == LexicalLanguage.English ? "ПЕРЕВОД НА РУССКИЙ" : "ПЕРЕВОД НА АНГЛИЙСКИЙ");
        FillChips(TranslationsPanel, TranslationsBlock, translations);

        FillChips(SynonymsPanel, SynonymsBlock, ResolveChips(result.Synonyms, language));
        FillChips(AntonymsPanel, AntonymsBlock, ResolveChips(result.Antonyms, language));

        var forms = result.Morphology?.ToDisplayList() ?? [];
        var formsText = forms.Count > 0 ? string.Join(" · ", forms) : null;
        FormsText.Text = formsText ?? string.Empty;
        FormsBlock.Visibility = formsText is null ? Visibility.Collapsed : Visibility.Visible;
        FormsText.Visibility = FormsToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        var examples = result.Examples.Select(example => example.Text).ToArray();
        ExamplesList.ItemsSource = examples;
        ExamplesBlock.Visibility = examples.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var relations = result.Relations is null
            ? null
            : string.Join(" · ", result.Relations.Select(relation => relation.Value).Distinct());
        RelationsText.Text = relations ?? string.Empty;
        RelationsBlock.Visibility = string.IsNullOrWhiteSpace(relations) ? Visibility.Collapsed : Visibility.Visible;

        SourceText.Text = BuildProvenance();

        ArticleScroll.ScrollToHome();

        // The blocks arrive as a cascade rather than all at once. The order is the
        // reading order, so the eye is led down the article it is about to read.
        Motion.RevealSequence(
        [
            WordText,
            GrammarLine,
            DefinitionsBlock,
            TranslationsBlock,
            SynonymsBlock,
            AntonymsBlock,
            FormsBlock,
            ExamplesBlock,
            RelationsBlock
        ]);
    }

    /// <summary>Rebuilds one chip row.</summary>
    private void FillChips(System.Windows.Controls.Panel panel, FrameworkElement block, ChipModel[] chips)
    {
        panel.Children.Clear();

        if (chips.Length == 0)
        {
            block.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var chip in chips)
        {
            var control = new WordChip
            {
                Word = chip.Word,
                Language = chip.Language,
                HasArticle = chip.HasArticle
            };

            if (chip.HasArticle)
            {
                control.Click += Chip_Click;
            }

            panel.Children.Add(control);
        }

        block.Visibility = Visibility.Visible;
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WordChip chip)
        {
            return;
        }

        _ = LookupAsync(chip.Word, chip.Language, recordHistory: true);
    }

    /// <summary>
    /// Shows the skeleton only if the lookup is still running after a short delay.
    /// </summary>
    /// <remarks>
    /// Cancelled by <see cref="FinishLoading"/> as soon as there is something to show.
    /// Without that the placeholder wins every fast lookup: the article renders in
    /// twenty milliseconds, the delayed callback fires a hundred and twenty later, and
    /// the skeleton is drawn over a perfectly good entry and stays there.
    /// </remarks>
    private void ShowSkeletonAfterDelay(CancellationToken token)
    {
        _skeletonCancellation?.Cancel();
        _skeletonCancellation?.Dispose();
        _skeletonCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var skeletonToken = _skeletonCancellation.Token;

        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(SkeletonDelay, skeletonToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (skeletonToken.IsCancellationRequested)
            {
                return;
            }

            Article.Visibility = Visibility.Collapsed;
            LookupEmptyState.Visibility = Visibility.Collapsed;
            Motion.Reveal(LoadingState, offset: 0);
        });
    }

    /// <summary>Stops a pending skeleton and hides one that is already showing.</summary>
    private void FinishLoading()
    {
        _skeletonCancellation?.Cancel();
        LoadingState.Visibility = Visibility.Collapsed;
    }

    private void ShowLookupMessage(string title, string hint)
    {
        Article.Visibility = Visibility.Collapsed;
        FinishLoading();
        LookupEmptyTitle.Text = title;
        LookupEmptyHint.Text = hint;
        Motion.Reveal(LookupEmptyState);
    }

    private void Disclosure_Toggled(object sender, RoutedEventArgs e)
    {
        if (FormsToggle.IsChecked == true)
        {
            Motion.Reveal(FormsText);
        }
        else
        {
            FormsText.Visibility = Visibility.Collapsed;
        }
    }

    // ── History ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Records a step in the trail, dropping anything ahead of the current position.
    /// </summary>
    /// <remarks>
    /// Browser semantics, deliberately: going back three words and then looking up a
    /// fourth abandons the branch you left, which is the behaviour everyone already
    /// has in their fingers.
    /// </remarks>
    private void PushHistory(string word, LexicalLanguage language)
    {
        var entry = new HistoryEntry(word, language);

        if (_historyIndex >= 0 && _historyIndex < _history.Count && _history[_historyIndex] == entry)
        {
            return;
        }

        if (_historyIndex < _history.Count - 1)
        {
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        }

        _history.Add(entry);

        if (_history.Count > MaxHistory)
        {
            _history.RemoveAt(0);
        }

        _historyIndex = _history.Count - 1;
        UpdateHistoryAffordances();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex <= 0)
        {
            return;
        }

        _historyIndex--;
        NavigateToHistoryPosition();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex >= _history.Count - 1)
        {
            return;
        }

        _historyIndex++;
        NavigateToHistoryPosition();
    }

    private void NavigateToHistoryPosition()
    {
        var entry = _history[_historyIndex];
        UpdateHistoryAffordances();
        _ = LookupAsync(entry.Word, entry.Language, recordHistory: false);
    }

    private void UpdateHistoryAffordances()
    {
        BackButton.IsEnabled = _historyIndex > 0;
        ForwardButton.IsEnabled = _historyIndex >= 0 && _historyIndex < _history.Count - 1;

        // The last few steps, so the reader can see the path they took without a
        // history panel. Older steps are still reachable with the back button.
        if (_history.Count <= 1)
        {
            TrailText.Visibility = Visibility.Collapsed;
            return;
        }

        var start = Math.Max(0, _historyIndex - 3);
        var trail = _history
            .Skip(start)
            .Take(_historyIndex - start + 1)
            .Select(entry => entry.Word);

        TrailText.Text = (start > 0 ? "… → " : string.Empty) + string.Join(" → ", trail);
        TrailText.Visibility = Visibility.Visible;
    }

    // ── Presentation helpers ─────────────────────────────────────────────────

    private static string BuildProvenance()
    {
        // Repository, dataset IDs and licences remain in THIRD_PARTY_NOTICES.txt.
        // The article footer communicates the user-relevant privacy property only.
        return "Локальные словари WriteLite · без интернета";
    }

    private static string? BuildDefinitionMeta(LexicalDefinition definition)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(definition.UsageLabel)) parts.Add(definition.UsageLabel!);
        if (definition.IsHistorical) parts.Add(definition.EraLabel ?? "историческое");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static void SetText(System.Windows.Controls.TextBlock target, string? value)
    {
        target.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
        target.Text = value ?? string.Empty;
    }

    private static string PartOfSpeechName(LexicalPartOfSpeech partOfSpeech) => partOfSpeech switch
    {
        LexicalPartOfSpeech.Noun => "существительное",
        LexicalPartOfSpeech.Verb => "глагол",
        LexicalPartOfSpeech.Adjective => "прилагательное",
        LexicalPartOfSpeech.Adverb => "наречие",
        LexicalPartOfSpeech.Pronoun => "местоимение",
        LexicalPartOfSpeech.Preposition => "предлог",
        LexicalPartOfSpeech.Conjunction => "союз",
        LexicalPartOfSpeech.Particle => "частица",
        LexicalPartOfSpeech.Interjection => "междометие",
        LexicalPartOfSpeech.Numeral => "числительное",
        _ => string.Empty
    };

    private readonly record struct HistoryEntry(string Word, LexicalLanguage Language);

    private sealed record ChipModel(string Word, LexicalLanguage Language, bool HasArticle);

    private sealed record DefinitionRow(string Number, string Text, string? Meta)
    {
        public Visibility MetaVisibility => string.IsNullOrWhiteSpace(Meta) ? Visibility.Collapsed : Visibility.Visible;
    }
}
