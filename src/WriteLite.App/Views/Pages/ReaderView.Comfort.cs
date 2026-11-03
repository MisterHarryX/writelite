using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WriteLite.Services.Reading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace WriteLite.Views.Pages;

/// <summary>
/// Reading comfort: page themes (тёмная, сепия, светлая, ночь), a serif face, search inside the
/// book and a table of contents built from its headings.
/// </summary>
/// <remarks>
/// Theme and face belong to the book's <see cref="ReaderTypography"/>, so each book remembers how
/// it is read — a novel in sepia with serifs, a manual in the dark theme — and nothing is global.
/// </remarks>
public partial class ReaderView
{
    private sealed record PageTheme(string Id, string Name, Color Paper, Color Ink);

    private static readonly PageTheme[] Themes =
    [
        new("dark", "Тёмная", Color.FromRgb(0x0B, 0x0B, 0x0B), Color.FromRgb(0xE8, 0xE6, 0xE3)),
        new("sepia", "Сепия", Color.FromRgb(0xF4, 0xEC, 0xD8), Color.FromRgb(0x3B, 0x2F, 0x22)),
        new("light", "Светлая", Color.FromRgb(0xFB, 0xFB, 0xF9), Color.FromRgb(0x1D, 0x1D, 0x1F)),
        new("night", "Ночь", Color.FromRgb(0x00, 0x00, 0x00), Color.FromRgb(0xA8, 0xA2, 0x9A)),
    ];

    private static readonly FontFamily SerifFace = new("Georgia, Cambria, Times New Roman");

    private int _searchFrom;

    /// <summary>Paints the page in the book's theme and face. Called after every typography change.</summary>
    private void ApplyComfort()
    {
        if (_project is null) return;
        var theme = Themes.FirstOrDefault(t => t.Id == _project.Typography.Theme) ?? Themes[0];
        if (theme.Id == "dark")
        {
            CanvasScroll.SetResourceReference(BackgroundProperty, "WlBgBase");
            Canvas.SetResourceReference(ForegroundProperty, "WlText");
            if (_document is not null) _document.Flow.SetResourceReference(FlowDocument.ForegroundProperty, "WlText");
        }
        else
        {
            var paper = new SolidColorBrush(theme.Paper);
            var ink = new SolidColorBrush(theme.Ink);
            paper.Freeze();
            ink.Freeze();
            CanvasScroll.Background = paper;
            Canvas.Foreground = ink;
            if (_document is not null) _document.Flow.Foreground = ink;
        }

        var face = _project.Typography.Serif ? SerifFace : TryFindResource("WlFont") as FontFamily ?? new FontFamily("Segoe UI");
        Canvas.FontFamily = face;
        if (_document is not null) _document.Flow.FontFamily = face;
        SerifToggle.IsChecked = _project.Typography.Serif;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            Style = TryFindResource("WlContextMenu") as Style,
            PlacementTarget = ThemeButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        foreach (var theme in Themes)
        {
            var item = new MenuItem
            {
                Header = theme.Name,
                IsChecked = _project?.Typography.Theme == theme.Id || (theme.Id == "dark" && string.IsNullOrEmpty(_project?.Typography.Theme)),
                Style = TryFindResource("WlMenuItem") as Style,
            };
            item.Click += (_, _) =>
            {
                if (_project is null) return;
                _project.Typography.Theme = theme.Id;
                ApplyComfort();
                SaveSettings();
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void Serif_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        _project.Typography.Serif = SerifToggle.IsChecked == true;
        ApplyTypography();
        ApplyComfort();
        SaveSettings();
    }

    // ── Search ─────────────────────────────────────────────────────────────────

    private void Search_Click(object sender, RoutedEventArgs e) => OpenSearch();

    private void OpenSearch()
    {
        SearchBar.Visibility = Visibility.Visible;
        BookSearchBox.Focus();
        BookSearchBox.SelectAll();
    }

    private void BookSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext(backwards: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSearch_Click(sender, e);
            e.Handled = true;
        }
    }

    private void BookSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchFrom = 0;
        var needle = BookSearchBox.Text ?? string.Empty;
        if (_document is null || needle.Trim().Length < 2)
        {
            SearchStatus.Text = string.Empty;
            return;
        }

        var count = CountOccurrences(_document.Text, needle.Trim());
        SearchStatus.Text = count == 0 ? "не найдено" : $"{count} {Services.RussianPlural.Form(count, "совпадение", "совпадения", "совпадений")}";
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext(backwards: false);

    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindNext(backwards: true);

    private void FindNext(bool backwards)
    {
        if (_document is null) return;
        var needle = (BookSearchBox.Text ?? string.Empty).Trim();
        if (needle.Length == 0) return;
        var text = _document.Text;
        int at;
        if (backwards)
        {
            var from = Math.Max(0, _searchFrom - needle.Length - 1);
            at = from <= 0 ? -1 : text.LastIndexOf(needle, from, StringComparison.CurrentCultureIgnoreCase);
            if (at < 0) at = text.LastIndexOf(needle, StringComparison.CurrentCultureIgnoreCase);
        }
        else
        {
            at = text.IndexOf(needle, Math.Min(_searchFrom, text.Length), StringComparison.CurrentCultureIgnoreCase);
            if (at < 0) at = text.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase);
        }

        if (at < 0)
        {
            SearchStatus.Text = "не найдено";
            return;
        }

        _searchFrom = at + needle.Length;
        if (_document.RangeFor(at, needle.Length) is { } range)
        {
            Canvas.Selection.Select(range.Start, range.End);
            range.Start.Paragraph?.BringIntoView();
            BringCharacterIntoView(range.Start);
        }
    }

    private void BringCharacterIntoView(TextPointer pointer)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
                var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
                if (rect.IsEmpty) return;
                var origin = Canvas.TranslatePoint(new System.Windows.Point(0, rect.Top), CanvasScroll);
                CanvasScroll.ScrollToVerticalOffset(CanvasScroll.VerticalOffset + origin.Y - CanvasScroll.ViewportHeight / 3);
            }
            catch (InvalidOperationException)
            {
                // Layout not ready; the paragraph is already in view.
            }
        }));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase);
             i >= 0 && count < 9999;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.CurrentCultureIgnoreCase))
        {
            count++;
        }

        return count;
    }

    private void CloseSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBar.Visibility = Visibility.Collapsed;
        Canvas.Focus();
    }

    // ── Contents ───────────────────────────────────────────────────────────────

    private void Contents_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var menu = new ContextMenu
        {
            Style = TryFindResource("WlContextMenu") as Style,
            PlacementTarget = ContentsButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MaxHeight = 520,
        };
        var headings = _document.Headings;
        if (headings.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "В книге нет заголовков", IsEnabled = false, Style = TryFindResource("WlMenuItem") as Style });
        }

        foreach (var heading in headings.Take(300))
        {
            var item = new MenuItem
            {
                Header = new string(' ', Math.Max(0, heading.Level - 1) * 3) + heading.Title,
                Style = TryFindResource("WlMenuItem") as Style,
            };
            var offset = heading.Offset;
            item.Click += (_, _) =>
            {
                var pointer = _document.PointerAt(offset);
                pointer.Paragraph?.BringIntoView();
                BringCharacterIntoView(pointer);
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void Reader_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && _document is not null)
        {
            OpenSearch();
            e.Handled = true;
        }
    }
}
