using System.Windows;
using System.Windows.Controls;
using WriteLite.Services.LanguageEngine;
using UserControl = System.Windows.Controls.UserControl;
using MessageBox = System.Windows.MessageBox;

namespace WriteLite.Views.Pages;

public partial class ExceptionsPage : UserControl
{
    private WriteLiteIgnoreService? _ignore;

    public ExceptionsPage()
    {
        InitializeComponent();
    }

    public event Action? ExceptionsChanged;

    public void Bind(WriteLiteIgnoreService ignore)
    {
        _ignore = ignore;
        Reload();
    }

    public void Reload()
    {
        if (_ignore is null)
        {
            return;
        }

        WordsList.ItemsSource = _ignore.GetIgnoredWords();
        // Show only neutral WriteLite ids; never raw third-party rule ids.
        RulesList.ItemsSource = _ignore.GetIgnoredRules()
            .Select(r => r.StartsWith("WL-", StringComparison.Ordinal)
                ? r
                : "WL-CUSTOM")
            .ToList();
    }

    private void RemoveWord_Click(object sender, RoutedEventArgs e)
    {
        if (_ignore is null || WordsList.SelectedItem is not string word)
        {
            return;
        }

        _ignore.UnignoreWord(word);
        Reload();
        ExceptionsChanged?.Invoke();
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (_ignore is null || RulesList.SelectedItem is not string displayed)
        {
            return;
        }

        // Prefer exact WL-id match; otherwise remove first non-WL entry if display is placeholder.
        var rules = _ignore.GetIgnoredRules();
        var match = rules.FirstOrDefault(r => string.Equals(r, displayed, StringComparison.Ordinal));
        if (match is null && displayed.StartsWith("WL-", StringComparison.Ordinal))
        {
            match = rules.FirstOrDefault(r => !r.StartsWith("WL-", StringComparison.Ordinal));
        }

        if (match is not null)
        {
            _ignore.UnignoreRule(match);
        }

        Reload();
        ExceptionsChanged?.Invoke();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_ignore is null)
        {
            return;
        }

        if (MessageBox.Show("Очистить все сохранённые исключения?", "Исключения",
                MessageBoxButton.YesNo) != MessageBoxResult.Yes)
        {
            return;
        }

        _ignore.ClearPersistent();
        Reload();
        ExceptionsChanged?.Invoke();
    }
}
