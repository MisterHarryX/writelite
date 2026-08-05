using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using WriteLite.Services.Spelling;
using WriteLite.Services.Lexical;
using WriteLite.Services;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using UserControl = System.Windows.Controls.UserControl;
using MessageBox = System.Windows.MessageBox;
using ListBox = System.Windows.Controls.ListBox;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace WriteLite.Views.Pages;

/// <summary>Ключ группировки списка слов — первая буква; всё нечитаемое сводится к «#».</summary>
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

public partial class DictionaryPage : UserControl
{
    private UserDictionaryService? _dictionary;
    private List<string> _all = [];
    private readonly LexicalPackCatalogService _packCatalog = LexicalPackCatalogService.CreateDefault();

    public DictionaryPage()
    {
        InitializeComponent();
        ReloadPacks();
    }

    /// <summary>
    /// Rebuilds the pack list off the dispatcher.
    ///
    /// Snapshot() is deceptively expensive: it fully parses the 27 MB open
    /// lexical pack (58k entries) just to render one catalog row, and it
    /// enumerates the entire LanguageTool 6.4 tree — thousands of files, one
    /// metadata syscall each for the size sum. This page is a field
    /// initializer of MainWindow, so doing that inline meant opening the main
    /// window parked the UI thread; the watchdog measured an 11.4 s stall and
    /// Windows recorded AppHangB1. The list simply appears when ready.
    /// </summary>
    private async Task ReloadPacksAsync()
    {
        try
        {
            var packs = await Task.Run(() => _packCatalog.Snapshot());
            PackList.ItemsSource = packs;
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("pack-catalog-failed", $"type={exception.GetType().Name}");
        }
    }

    public event Action? DictionaryChanged;

    public void Bind(UserDictionaryService dictionary)
    {
        _dictionary = dictionary;
        Reload();
    }

    public void Reload()
    {
        if (_dictionary is null)
        {
            return;
        }

        _all = _dictionary.Snapshot().OrderBy(w => w, StringComparer.CurrentCultureIgnoreCase).ToList();
        ApplyFilter();
        CountText.Text = Plural(_all.Count);
        // Каталог пакетов здесь намеренно не перечитывается: Snapshot() разбирает
        // 27-мегабайтный пакет, а добавление слова его никак не меняет.
    }

    /// <summary>«1 слово», «2 слова», «5 слов».</summary>
    private static string Plural(int count)
    {
        var mod100 = count % 100;
        var mod10 = count % 10;
        var form = mod100 is >= 11 and <= 14 ? "слов"
            : mod10 == 1 ? "слово"
            : mod10 is >= 2 and <= 4 ? "слова"
            : "слов";
        return $"{count} {form}";
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text?.Trim() ?? string.Empty;
        var view = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(w => w.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        // Группировка по первой букве: у списка появляются буквенные заголовки.
        var grouped = new ListCollectionView(view);
        grouped.GroupDescriptions.Add(new PropertyGroupDescription(null, new FirstLetterConverter()));
        WordsList.ItemsSource = grouped;

        var empty = view.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty)
        {
            return;
        }

        var searching = !string.IsNullOrEmpty(q);
        EmptyTitle.Text = searching ? "Ничего не найдено" : "Словарь пуст";
        EmptyHint.Text = searching
            ? $"По запросу «{q}» нет слов. Попробуйте изменить запрос."
            : "Добавьте имена, термины и сокращения, которые не нужно подчёркивать.";
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_dictionary is null)
        {
            return;
        }

        var word = (WordInput.Text ?? string.Empty).Trim();
        if (word.Length < 1 || word.Length > 64 || word.Any(char.IsControl))
        {
            MessageBox.Show("Введите корректное слово (1–64 символа).", "Словарь", MessageBoxButton.OK);
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

    private void RemoveWord_Click(object sender, RoutedEventArgs e)
    {
        if (_dictionary is null || sender is not Button { CommandParameter: string word })
        {
            return;
        }

        _dictionary.Remove(word);
        Reload();
        DictionaryChanged?.Invoke();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_dictionary is null || _all.Count == 0)
        {
            return;
        }

        if (MessageBox.Show("Удалить все слова из словаря?", "Словарь",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _dictionary.Clear();
        Reload();
        DictionaryChanged?.Invoke();
    }

    private void ReloadPacks() => _ = ReloadPacksAsync();

    private void RefreshPacks_Click(object sender, RoutedEventArgs e) => ReloadPacks();

    private async void ImportPack_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = "Импортировать словарный пакет WriteLite",
            Filter = "Словарный пакет WriteLite (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var imported = await _packCatalog.ImportAsync(dialog.FileName);
            ReloadPacks();
            MessageBox.Show($"Пакет «{imported.Name}» установлен и проверен.", "Словари", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("dictionary-import-failed", $"type={ex.GetType().Name}");
            MessageBox.Show("Пакет не установлен: формат или целостность не прошли проверку.", "Словари", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void VerifyPack_Click(object sender, RoutedEventArgs e)
    {
        if (PackList.SelectedItem is not LexicalPackDescriptor pack) return;
        try
        {
            var verified = await _packCatalog.VerifyAsync(pack);
            ReloadPacks();
            MessageBox.Show(verified.IsValid ? "Целостность подтверждена." : "Пакет повреждён.", "Словари");
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("dictionary-verify-failed", $"type={ex.GetType().Name}");
            MessageBox.Show("Не удалось подтвердить целостность пакета.", "Словари", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void TogglePack_Click(object sender, RoutedEventArgs e)
    {
        if (PackList.SelectedItem is not LexicalPackDescriptor pack) return;
        try
        {
            _packCatalog.SetEnabled(pack, !pack.IsEnabled);
            ReloadPacks();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Словари", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void RemovePack_Click(object sender, RoutedEventArgs e)
    {
        if (PackList.SelectedItem is not LexicalPackDescriptor pack) return;
        if (pack.IsBuiltIn)
        {
            MessageBox.Show("Встроенный пакет является частью WriteLite и не удаляется менеджером.", "Словари");
            return;
        }
        if (MessageBox.Show($"Удалить пакет «{pack.Name}»?", "Словари", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _packCatalog.Remove(pack);
        ReloadPacks();
    }

    private void LicensePack_Click(object sender, RoutedEventArgs e)
    {
        if (PackList.SelectedItem is not LexicalPackDescriptor pack) return;
        MessageBox.Show(
            $"{pack.Name}\n\nИсточник: {pack.Source}\nЛицензия: {pack.License}\nВерсия: {pack.Version}\nТип данных: {pack.DataType}",
            "Источник и лицензия", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
