using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DocumentImage = WriteLite.Documents.Model.DocumentImage;
using WriteLite.Services.Documents;
using WriteLite.Services.Storage;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MenuItem = System.Windows.Controls.MenuItem;
using WpfParagraph = System.Windows.Documents.Paragraph;
using WpfTable = System.Windows.Documents.Table;

namespace WriteLite.Views.Pages;

/// <summary>
/// The Word-like half of the editor: the «Лист» page view, highlighting, super/subscript,
/// clearing formatting and the «Вставка» menu (table, picture, link, page break, symbols, date).
/// </summary>
/// <remarks>
/// <para>«Лист» draws the document as a sheet of paper — white, A4 proportions, page margins,
/// dark ink — on the dimmed canvas, the way Word shows a document. It is a view, not a format:
/// the text colour the document saves is unchanged, because the ink is the document's default
/// foreground and only explicit colours are written out.</para>
/// </remarks>
public partial class EditorPage
{
    private static readonly Brush PaperInk = Frozen(Color.FromRgb(0x1F, 0x1F, 0x1F));
    private static readonly Brush Paper = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly string PreferencesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WriteLite", "editor-view.json");

    private bool _pageView;

    private sealed class EditorViewPreferences
    {
        public bool PageView { get; set; } = true;
    }

    /// <summary>The colour unformatted text is drawn in: dark ink on the sheet, the theme colour otherwise.</summary>
    private Brush CurrentInk => _pageView ? PaperInk : TryFindResource("WlText") as Brush ?? Brushes.White;

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void InitializePageView()
    {
        var preferences = AtomicJsonFile.Read<EditorViewPreferences>(PreferencesPath) ?? new EditorViewPreferences();
        SetPageView(preferences.PageView, persist: false);
    }

    private void PageView_Click(object sender, RoutedEventArgs e) => SetPageView(PageViewToggle.IsChecked == true, persist: true);

    internal void SetPageView(bool on, bool persist)
    {
        var oldInk = CurrentInk;
        _pageView = on;
        var ink = CurrentInk;
        PageViewToggle.IsChecked = on;

        if (on)
        {
            PageSheet.Background = Paper;
            PageSheet.Padding = new Thickness(76, 72, 76, 96);
            PageSheet.MinHeight = 1060;
            PageSheet.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.45 };
            CanvasMeasure.MaxWidth = 816;
            CanvasScroller.SetResourceReference(BackgroundProperty, "WlBg");
            Editor.CaretBrush = PaperInk;
            CanvasPlaceholder.Foreground = Frozen(Color.FromRgb(0x9A, 0x9A, 0x9A));
        }
        else
        {
            PageSheet.Background = Brushes.Transparent;
            PageSheet.Padding = new Thickness(0);
            PageSheet.MinHeight = 0;
            PageSheet.Effect = null;
            CanvasMeasure.MaxWidth = 760;
            CanvasScroller.SetResourceReference(BackgroundProperty, "WlBgBase");
            Editor.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "WlBrand");
            CanvasPlaceholder.SetResourceReference(TextBlock.ForegroundProperty, "WlTextMuted");
        }

        RepaintInk(oldInk, ink);
        if (persist)
        {
            AtomicJsonFile.Write(PreferencesPath, new EditorViewPreferences { PageView = on });
        }
    }

    /// <summary>Moves every run drawn in the old default colour to the new one.</summary>
    private void RepaintInk(Brush oldInk, Brush newInk)
    {
        if (Editor.Document is not { } document) return;
        var wasInternal = _internalEdit;
        _internalEdit = true;
        try
        {
            var oldColor = (oldInk as SolidColorBrush)?.Color;
            document.Foreground = newInk;
            foreach (var inline in AllInlines(document.Blocks))
            {
                if (ReferenceEquals(inline.Foreground, oldInk)
                    || (inline.Foreground is SolidColorBrush brush && oldColor is { } c && brush.Color == c))
                {
                    inline.Foreground = newInk;
                }
            }

            foreach (var paragraph in AllParagraphs(document.Blocks))
            {
                if (ReferenceEquals(paragraph.Foreground, oldInk)) paragraph.Foreground = newInk;
            }
        }
        finally
        {
            _internalEdit = wasInternal;
        }
    }

    private static IEnumerable<WpfParagraph> AllParagraphs(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case WpfParagraph p:
                    yield return p;
                    break;
                case List list:
                    foreach (var item in list.ListItems)
                    foreach (var nested in AllParagraphs(item.Blocks))
                        yield return nested;
                    break;
                case WpfTable table:
                    foreach (var group in table.RowGroups)
                    foreach (var row in group.Rows)
                    foreach (var cell in row.Cells)
                    foreach (var nested in AllParagraphs(cell.Blocks))
                        yield return nested;
                    break;
                case Section section:
                    foreach (var nested in AllParagraphs(section.Blocks))
                        yield return nested;
                    break;
            }
        }
    }

    private static IEnumerable<Inline> AllInlines(IEnumerable<Block> blocks)
    {
        foreach (var paragraph in AllParagraphs(blocks))
        foreach (var inline in Flatten(paragraph.Inlines))
            yield return inline;
    }

    private static IEnumerable<Inline> Flatten(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
            {
                foreach (var nested in Flatten(span.Inlines)) yield return nested;
            }
        }
    }

    // ── Character formatting ───────────────────────────────────────────────────

    private void Highlight_Click(object sender, RoutedEventArgs e)
    {
        var menu = MenuFor(HighlightButton);
        foreach (var (name, hex) in new[]
                 {
                     ("Жёлтый", "#FFF59D"), ("Зелёный", "#C5E1A5"), ("Голубой", "#B3E5FC"),
                     ("Розовый", "#F8BBD0"), ("Оранжевый", "#FFCC80"), ("Без выделения", ""),
                 })
        {
            menu.Items.Add(MenuEntry(name, () =>
            {
                if (!EnsureSelection()) return;
                object? value = hex.Length == 0 ? null : FlowDocumentBridge.ParseBrush(hex);
                ApplyToSelection(() => Editor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, value));
            }));
        }

        menu.IsOpen = true;
    }

    private void Superscript_Click(object sender, RoutedEventArgs e) => ToggleBaseline(BaselineAlignment.Superscript, SuperscriptToggle);

    private void Subscript_Click(object sender, RoutedEventArgs e) => ToggleBaseline(BaselineAlignment.Subscript, SubscriptToggle);

    private void ToggleBaseline(BaselineAlignment alignment, System.Windows.Controls.Primitives.ToggleButton toggle)
    {
        if (!EnsureSelection())
        {
            toggle.IsChecked = false;
            return;
        }

        var current = Editor.Selection.GetPropertyValue(Inline.BaselineAlignmentProperty);
        var on = !(current is BaselineAlignment a && a == alignment);
        var size = Editor.Selection.GetPropertyValue(TextElement.FontSizeProperty) is double s ? s : FlowDocumentBridge.ToPixels(12);
        ApplyToSelection(() =>
        {
            Editor.Selection.ApplyPropertyValue(Inline.BaselineAlignmentProperty, on ? alignment : BaselineAlignment.Baseline);
            Editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, on ? Math.Max(6, s0(size) * 0.7) : s0(size) / 0.7);
        });
        toggle.IsChecked = on;

        static double s0(double v) => v;
    }

    private void ClearFormatting_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureSelection()) return;
        var ink = CurrentInk;
        ApplyToSelection(() =>
        {
            Editor.Selection.ClearAllProperties();
            Editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, ink);
        });
    }

    // ── Insert ─────────────────────────────────────────────────────────────────

    private void InsertMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = MenuFor(InsertMenuButton);
        var table = new MenuItem { Header = "Таблица", Style = TryFindResource("WlMenuItem") as Style };
        foreach (var (rows, cols) in new[] { (2, 2), (3, 3), (4, 3), (5, 4), (8, 5) })
        {
            table.Items.Add(MenuEntry($"{rows} × {cols}", () => InsertTable(rows, cols)));
        }

        menu.Items.Add(table);
        menu.Items.Add(MenuEntry("Изображение…", InsertImage));
        menu.Items.Add(MenuEntry("Ссылка из буфера обмена", InsertLink));
        menu.Items.Add(MenuEntry("Разрыв страницы", InsertPageBreak));
        menu.Items.Add(MenuEntry("Сегодняшняя дата", () => InsertText(DateTime.Today.ToString("d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")))));
        var symbols = new MenuItem { Header = "Символ", Style = TryFindResource("WlMenuItem") as Style };
        foreach (var symbol in new[] { "—", "–", "«»", "„“", "…", "№", "§", "©", "®", "™", "°", "±", "×", "÷", "≈", "≠", "≤", "≥", "→", "←", "•", "₽", "€", "$" })
        {
            symbols.Items.Add(MenuEntry(symbol, () => InsertText(symbol == "«»" ? "«»" : symbol)));
        }

        menu.Items.Add(symbols);
        menu.IsOpen = true;
    }

    private void InsertText(string text)
    {
        ApplyToSelection(() =>
        {
            Editor.Selection.Text = text;
            Editor.CaretPosition = Editor.Selection.End;
        });
        Editor.Focus();
    }

    private void InsertTable(int rows, int columns)
    {
        var table = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 6) };
        for (var c = 0; c < columns; c++) table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        var line = TryFindResource("WlLineStrong") as Brush ?? Brushes.Gray;
        for (var r = 0; r < rows; r++)
        {
            var row = new TableRow();
            for (var c = 0; c < columns; c++)
            {
                row.Cells.Add(new TableCell(new WpfParagraph())
                {
                    BorderBrush = _pageView ? Frozen(Color.FromRgb(0xBD, 0xBD, 0xBD)) : line,
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(6, 4, 6, 4),
                    FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                });
            }

            group.Rows.Add(row);
        }

        table.RowGroups.Add(group);
        InsertBlock(table);
        var first = table.RowGroups[0].Rows[0].Cells[0].Blocks.FirstBlock;
        Editor.CaretPosition = first.ContentStart;
        Editor.Focus();
    }

    private void InsertImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Вставить изображение",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            var contentType = extension switch
            {
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                _ => "image/jpeg",
            };
            var inline = FlowDocumentBridge.CreateImageInline(new DocumentImage(bytes, contentType, 0, 0));
            ApplyToSelection(() =>
            {
                var position = Editor.Selection.IsEmpty ? Editor.CaretPosition : Editor.Selection.Start;
                if (!position.IsAtInsertionPosition) position = position.GetInsertionPosition(LogicalDirection.Forward) ?? position;
                if (position.Paragraph is { } paragraph)
                {
                    new InlineUIContainer(((InlineUIContainer)inline).Child, position);
                }
                else
                {
                    InsertBlock(new WpfParagraph(inline));
                }
            });
        }
        catch (Exception ex)
        {
            Services.CompatibilityLogger.Technical("editor-insert-image-failed", $"type={ex.GetType().Name}");
            System.Windows.MessageBox.Show(Window.GetWindow(this), "Не удалось открыть изображение.", "Редактор",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void InsertLink()
    {
        string url;
        try
        {
            url = System.Windows.Clipboard.GetText().Trim();
        }
        catch
        {
            url = string.Empty;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto"))
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this), "Скопируйте адрес ссылки (https://…) и выберите пункт ещё раз.",
                "Редактор", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ApplyToSelection(() =>
        {
            var range = Editor.Selection;
            if (range.IsEmpty)
            {
                range.Text = url;
            }

            var link = new Hyperlink(range.Start, range.End) { NavigateUri = uri };
            link.SetResourceReference(TextElement.ForegroundProperty, "WlBrand");
        });
    }

    private void InsertPageBreak()
    {
        InsertBlock(FlowDocumentBridge.BuildPageBreak(Typography));
        Editor.Focus();
    }

    /// <summary>Inserts a block after the paragraph that holds the caret (or at the end).</summary>
    private void InsertBlock(Block block)
    {
        ApplyToSelection(() =>
        {
            var current = Editor.CaretPosition.Paragraph;
            var blocks = current?.Parent is TableCell or ListItem ? null : Editor.Document.Blocks;
            if (current is not null && blocks is not null && blocks.Contains(current))
            {
                blocks.InsertAfter(current, block);
            }
            else
            {
                Editor.Document.Blocks.Add(block);
            }

            // Keep somewhere to type after the new block.
            if (block.NextBlock is null) Editor.Document.Blocks.Add(new WpfParagraph());
        });
    }

    // ── Menus ──────────────────────────────────────────────────────────────────

    private System.Windows.Controls.ContextMenu MenuFor(FrameworkElement owner) => new()
    {
        Style = TryFindResource("WlContextMenu") as Style,
        PlacementTarget = owner,
        Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
    };

    private MenuItem MenuEntry(string header, Action action)
    {
        var item = new MenuItem { Header = header, Style = TryFindResource("WlMenuItem") as Style };
        item.Click += (_, _) => action();
        return item;
    }
}
