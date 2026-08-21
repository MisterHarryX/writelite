using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using WriteLite.Services;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Lexical;
using WriteLite.Services.Spelling;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace WriteLite.Views.Pages;

/// <summary>Grouping key for the word list — first letter, everything unreadable folds into "#".</summary>
internal sealed class FirstLetterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string { Length: > 0 } word)
        {
            return "#";
        }

        var first = char.ToUpper(word[0], CultureInfo.CurrentCulture);
        return char.IsLetter(first) ? first.ToString() : "#";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Custom words, ignored words and ignored rules.
/// </summary>
/// <remarks>
/// Replaces the old split between the dictionary page's word list and a separate
/// exceptions page: all three lists answer the same question — "what should
/// WriteLite leave alone?" — so they belong on one screen.
/// </remarks>
public partial class WordManagerPage : UserControl
{
    private readonly System.Windows.Controls.TextBlock _countText = new();
    private readonly LexicalPackCatalogService _packs = LexicalPackCatalogService.CreateDefault();
    private UserDictionaryService? _dictionary;
    private WriteLiteIgnoreService? _ignore;
    private List<string> _all = [];

    public WordManagerPage()
    {
        InitializeComponent();
        _countText.SetResourceReference(StyleProperty, "WlMonoLabel");
        Header.Actions = _countText;
    }

    public event Action? DictionaryChanged;

    public event Action? ExceptionsChanged;

    public event Action<string>? NavigationRequested;

    private enum Tab
    {
        CustomWords,
        IgnoredWords,
        IgnoredRules,
        Dictionaries
    }

    private Tab Current =>
        TabIgnoredWords.IsChecked == true ? Tab.IgnoredWords :
        TabIgnoredRules.IsChecked == true ? Tab.IgnoredRules :
        TabDictionaries.IsChecked == true ? Tab.Dictionaries :
        Tab.CustomWords;

    public void Bind(UserDictionaryService dictionary, WriteLiteIgnoreService ignore)
    {
        _dictionary = dictionary;
        _ignore = ignore;
        Reload();
    }

    public void Reload()
    {
        if (Current == Tab.Dictionaries)
        {
            ReloadDictionaries();
            return;
        }

        _all = Current switch
        {
            Tab.IgnoredWords => (_ignore?.GetIgnoredWords() ?? []).ToList(),
            // Never surface raw third-party rule ids; only neutral WriteLite ones.
            Tab.IgnoredRules => (_ignore?.GetIgnoredRules() ?? [])
                .Select(rule => rule.StartsWith("WL-", StringComparison.Ordinal) ? rule : "WL-CUSTOM")
                .ToList(),
            _ => (_dictionary?.Snapshot() ?? []).ToList()
        };

        _all = _all.OrderBy(word => word, StringComparer.CurrentCultureIgnoreCase).ToList();
        ApplyFilter();
        Controls.Type.SetTracked(_countText, Plural(_all.Count).ToUpperInvariant());
    }

    /// <summary>«1 слово», «23 слова», «5 слов» — and the rule-list equivalent.</summary>
    private string Plural(int count) => Current == Tab.IgnoredRules
        ? RussianPlural.Rules(count)
        : RussianPlural.Words(count);

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var view = string.IsNullOrEmpty(query)
            ? _all
            : _all.Where(word => word.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        // Letter headings only help an alphabetical word list, not a short rule list.
        if (Current == Tab.IgnoredRules)
        {
            WordsList.ItemsSource = view;
        }
        else
        {
            var grouped = new ListCollectionView(view);
            grouped.GroupDescriptions.Add(new PropertyGroupDescription(null, new FirstLetterConverter()));
            WordsList.ItemsSource = grouped;
        }

        var empty = view.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty)
        {
            return;
        }

        if (!string.IsNullOrEmpty(query))
        {
            EmptyTitle.Text = "Ничего не найдено";
            EmptyHint.Text = $"По запросу «{query}» совпадений нет.";
            return;
        }

        (EmptyTitle.Text, EmptyHint.Text) = Current switch
        {
            Tab.IgnoredWords => ("Игнорируемых слов нет",
                "Слово попадает сюда, когда вы выбираете «Игнорировать» в панели замечаний."),
            Tab.IgnoredRules => ("Игнорируемых правил нет",
                "Правило попадает сюда, когда вы скрываете конкретный тип замечаний."),
            _ => ("Словарь пуст",
                "Добавьте имена, термины и сокращения, которые не нужно подчёркивать.")
        };
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (WordsList is null)
        {
            return;
        }

        var dictionaries = Current == Tab.Dictionaries;

        WordToolbar.Visibility = dictionaries ? Visibility.Collapsed : Visibility.Visible;
        DictionaryToolbar.Visibility = dictionaries ? Visibility.Visible : Visibility.Collapsed;
        WordsList.Visibility = dictionaries ? Visibility.Collapsed : Visibility.Visible;
        DictionariesList.Visibility = dictionaries ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = dictionaries ? Visibility.Collapsed : Visibility.Visible;

        // Only the custom dictionary accepts new entries; the other two lists are
        // populated by the user's decisions in the correction panel.
        var canAdd = Current == Tab.CustomWords;
        WordInput.IsEnabled = canAdd;
        AddButton.IsEnabled = canAdd;
        WordInput.Tag = canAdd
            ? "Добавьте слово, которое не нужно подчёркивать"
            : "Список пополняется из панели замечаний";

        FooterHint.Text = Current switch
        {
            Tab.IgnoredRules => "Показываются только нейтральные идентификаторы WriteLite (WL-…).",
            Tab.IgnoredWords => "Игнорируемое слово не проверяется, но остаётся в тексте без изменений.",
            Tab.Dictionaries => "Встроенные словари WriteLite работают всегда и настройки не требуют.",
            _ => "Слова из этого списка не считаются орфографическими ошибками."
        };

        Reload();
    }

    // ── Мои словари ──────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the list of the user's own dictionaries.
    /// </summary>
    /// <remarks>
    /// Reads only the user directory, so unlike the old catalogue it does not parse
    /// tens of megabytes of bundled packs to draw a list. It stays off the dispatcher
    /// anyway: the files are the user's and may be anything.
    /// </remarks>
    private async void ReloadDictionaries()
    {
        DictionaryRow[] rows;
        try
        {
            rows = await Task.Run(() => _packs.UserPacks().Select(DictionaryRow.From).ToArray());
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("user-dictionaries-failed", $"type={exception.GetType().Name}");
            rows = [];
        }

        if (Current != Tab.Dictionaries)
        {
            return;
        }

        DictionariesList.ItemsSource = rows;
        Controls.Type.SetTracked(_countText, RussianPlural.Dictionaries(rows.Length).ToUpperInvariant());

        var empty = rows.Length == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyAction.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            EmptyTitle.Text = "У вас пока нет собственных словарей";
            EmptyHint.Text = "Добавьте свой словарь терминов, чтобы WriteLite учитывал его при проверке.";
        }
    }

    private async void AddDictionary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = "Добавить словарь",
            Filter = "Словарь WriteLite (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var imported = await _packs.ImportAsync(dialog.FileName);
            ReloadDictionaries();
            MessageBox.Show(
                $"Словарь «{imported.Name}» добавлен.",
                "Мои словари", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("user-dictionary-import-failed", $"type={exception.GetType().Name}");
            MessageBox.Show(
                "Не удалось добавить словарь: файл не является словарём WriteLite или повреждён.",
                "Мои словари", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DictionaryEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: DictionaryRow row } toggle)
        {
            return;
        }

        var enabled = toggle.IsChecked == true;
        if (enabled == row.IsEnabled)
        {
            return;
        }

        try
        {
            _packs.SetEnabled(row.Pack, enabled);
            ReloadDictionaries();
            DictionaryChanged?.Invoke();
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("user-dictionary-toggle-failed", $"type={exception.GetType().Name}");
            toggle.IsChecked = row.IsEnabled;
        }
    }

    private void RemoveDictionary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: DictionaryRow row })
        {
            return;
        }

        if (MessageBox.Show(
                $"Удалить словарь «{row.Name}»?",
                "Мои словари", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _packs.Remove(row.Pack);
            ReloadDictionaries();
            DictionaryChanged?.Invoke();
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("user-dictionary-remove-failed", $"type={exception.GetType().Name}");
            MessageBox.Show("Не удалось удалить словарь.", "Мои словари", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// One row in «Мои словари»: what the dictionary is, not where its file lives.
    /// </summary>
    private sealed record DictionaryRow(LexicalPackDescriptor Pack, string Name, string Summary, bool IsEnabled)
    {
        public static DictionaryRow From(LexicalPackDescriptor pack)
        {
            var parts = new List<string> { pack.Language };
            if (pack.EntryCount is int count)
            {
                parts.Add(RussianPlural.Words(count));
            }

            if (!pack.IsValid)
            {
                parts.Add("файл повреждён");
            }
            else if (!pack.IsEnabled)
            {
                parts.Add("отключён");
            }

            return new DictionaryRow(pack, pack.Name, string.Join(" · ", parts), pack.IsEnabled);
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_dictionary is null || Current != Tab.CustomWords)
        {
            return;
        }

        var word = (WordInput.Text ?? string.Empty).Trim();
        if (word.Length < 1 || word.Length > 64 || word.Any(char.IsControl))
        {
            MessageBox.Show("Введите корректное слово (1–64 символа).", "Менеджер слов", MessageBoxButton.OK);
            return;
        }

        _dictionary.Add(word);
        WordInput.Text = string.Empty;
        Reload();
        DictionaryChanged?.Invoke();
    }

    private void WordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        Add_Click(sender, e);
    }

    private void RemoveEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string entry })
        {
            return;
        }

        switch (Current)
        {
            case Tab.CustomWords:
                _dictionary?.Remove(entry);
                Reload();
                DictionaryChanged?.Invoke();
                break;

            case Tab.IgnoredWords:
                _ignore?.UnignoreWord(entry);
                Reload();
                ExceptionsChanged?.Invoke();
                break;

            case Tab.IgnoredRules:
                RemoveRule(entry);
                Reload();
                ExceptionsChanged?.Invoke();
                break;
        }
    }

    /// <summary>
    /// Rule ids are displayed normalised, so an exact match is tried first and a
    /// masked "WL-CUSTOM" row falls back to the first non-WriteLite id.
    /// </summary>
    private void RemoveRule(string displayed)
    {
        if (_ignore is null)
        {
            return;
        }

        var rules = _ignore.GetIgnoredRules();
        var match = rules.FirstOrDefault(rule => string.Equals(rule, displayed, StringComparison.Ordinal))
                    ?? rules.FirstOrDefault(rule => !rule.StartsWith("WL-", StringComparison.Ordinal));

        if (match is not null)
        {
            _ignore.UnignoreRule(match);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_all.Count == 0)
        {
            return;
        }

        var question = Current switch
        {
            Tab.IgnoredWords => "Очистить все сохранённые исключения?",
            Tab.IgnoredRules => "Очистить все сохранённые исключения?",
            _ => "Удалить все слова из словаря?"
        };

        if (MessageBox.Show(question, "Менеджер слов", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            return;
        }

        if (Current == Tab.CustomWords)
        {
            _dictionary?.Clear();
            Reload();
            DictionaryChanged?.Invoke();
            return;
        }

        // ClearPersistent wipes both ignored words and ignored rules; the confirmation
        // above says so rather than pretending the tabs are cleared independently.
        _ignore?.ClearPersistent();
        Reload();
        ExceptionsChanged?.Invoke();
    }

    private void OpenDictionary_Click(object sender, RoutedEventArgs e) => NavigationRequested?.Invoke("dictionary");
}
