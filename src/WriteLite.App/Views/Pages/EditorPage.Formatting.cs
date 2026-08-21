using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WriteLite.Services;
using WriteLite.Services.Documents;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfList = System.Windows.Documents.List;
using WpfParagraph = System.Windows.Documents.Paragraph;
// WinForms is enabled in this project; the WPF types are always what is meant here.
using Brushes = System.Windows.Media.Brushes;
using FontStyle = System.Windows.FontStyle;
using MenuItem = System.Windows.Controls.MenuItem;

namespace WriteLite.Views.Pages;

/// <summary>
/// Formatting, zoom, and find-and-replace.
/// </summary>
/// <remarks>
/// Every command here goes through the selection rather than through the document
/// model. That is deliberate: the model is rebuilt from the FlowDocument when the
/// file is saved, so formatting applied to the visible selection is formatting that
/// reaches the file — with no second code path that could disagree with what the
/// user sees.
/// </remarks>
public partial class EditorPage
{
    /// <summary>Indent step. 1,25 cm is the Russian typographic default for a first line.</summary>
    private const double IndentStepPoints = 35.4;

    private static readonly double[] ZoomSteps = [0.7, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];

    /// <summary>
    /// Colours offered for text.
    /// </summary>
    /// <remarks>
    /// The product palette plus black and white, not a colour picker. A document
    /// editor inside a single-accent product should not be a place where any of
    /// sixteen million colours can be applied by accident.
    /// </remarks>
    private static readonly (string Label, string Hex)[] TextColours =
    [
        ("По умолчанию", ""),
        ("Оранжевый", "#EC6C08"),
        ("Красный", "#E3695A"),
        ("Жёлтый", "#E0A64B"),
        ("Зелёный", "#6FB593"),
        ("Серый", "#8A847B"),
        ("Чёрный", "#000000"),
        ("Белый", "#FFFFFF")
    ];

    private int _zoomIndex = 3;
    private bool _suppressFormattingSync;

    private void InitialiseFormattingControls()
    {
        // System families, filtered to ones that can actually render Cyrillic: a
        // Russian-language editor offering a font that shows the document as boxes
        // is offering a trap.
        var families = Fonts.SystemFontFamilies
            .Select(family => family.Source)
            .Where(SupportsCyrillic)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        FontFamilyBox.ItemsSource = families;
        FontSizeBox.ItemsSource = new[] { "8", "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "32", "48", "72" };

        StyleBox.ItemsSource = new[]
        {
            "Обычный текст",
            "Заголовок 1",
            "Заголовок 2",
            "Заголовок 3",
            "Заголовок 4"
        };

        foreach (var (label, hex) in TextColours)
        {
            var item = new MenuItem
            {
                Header = label,
                Tag = hex,
                Style = TryFindResource("WlMenuItem") as Style
            };

            if (hex.Length > 0 && FlowDocumentBridge.ParseBrush(hex) is { } brush)
            {
                item.Icon = new System.Windows.Shapes.Rectangle
                {
                    Width = 10,
                    Height = 10,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = brush
                };
            }

            item.Click += TextColourChosen;
            ColorMenu.Items.Add(item);
        }
    }

    /// <summary>
    /// True when the family has glyphs for Cyrillic.
    /// </summary>
    /// <remarks>
    /// Checked against the typeface's character map rather than against a name list,
    /// so a machine with unusual fonts installed gets a correct answer. Symbol and
    /// icon fonts fail this test, which is exactly why it is here.
    /// </remarks>
    private static bool SupportsCyrillic(string familyName)
    {
        try
        {
            var typeface = new Typeface(
                new FontFamily(familyName),
                FontStyles.Normal,
                FontWeights.Normal,
                FontStretches.Normal);

            if (!typeface.TryGetGlyphTypeface(out var glyphTypeface))
            {
                return false;
            }

            // 'а' and 'Я' together rule out fonts carrying only a stray Cyrillic glyph.
            return glyphTypeface.CharacterToGlyphMap.ContainsKey('а')
                   && glyphTypeface.CharacterToGlyphMap.ContainsKey('Я');
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    // ── Character formatting ─────────────────────────────────────────────────

    private void Bold_Click(object sender, RoutedEventArgs e) => ToggleBold();

    private void Italic_Click(object sender, RoutedEventArgs e) => ToggleItalic();

    private void Underline_Click(object sender, RoutedEventArgs e) => ToggleUnderline();

    private void Strikethrough_Click(object sender, RoutedEventArgs e) => ToggleStrikethrough();

    private void ToggleBold() => ToggleInlineProperty(
        TextElement.FontWeightProperty,
        FontWeights.Bold,
        FontWeights.Normal,
        current => current is FontWeight weight && weight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight());

    private void ToggleItalic() => ToggleInlineProperty(
        TextElement.FontStyleProperty,
        FontStyles.Italic,
        FontStyles.Normal,
        current => current is FontStyle style && style != FontStyles.Normal);

    private void ToggleUnderline() => ToggleDecoration(TextDecorationLocation.Underline);

    private void ToggleStrikethrough() => ToggleDecoration(TextDecorationLocation.Strikethrough);

    private void ToggleInlineProperty(
        DependencyProperty property,
        object onValue,
        object offValue,
        Func<object?, bool> isOn)
    {
        if (!EnsureSelection())
        {
            return;
        }

        var current = Editor.Selection.GetPropertyValue(property);
        var value = isOn(current) ? offValue : onValue;

        ApplyToSelection(() => Editor.Selection.ApplyPropertyValue(property, value));
    }

    /// <summary>
    /// Adds or removes one decoration without disturbing the other.
    /// </summary>
    /// <remarks>
    /// <c>TextDecorations</c> is a collection property, so the naive
    /// "apply Underline" call replaces strikethrough with underline. The collection
    /// has to be read, edited and written back for both to coexist.
    /// </remarks>
    private void ToggleDecoration(TextDecorationLocation location)
    {
        if (!EnsureSelection())
        {
            return;
        }

        var existing = Editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        var decorations = new TextDecorationCollection();

        var alreadyOn = false;
        if (existing is not null)
        {
            foreach (var decoration in existing)
            {
                if (decoration.Location == location)
                {
                    alreadyOn = true;
                    continue;
                }

                decorations.Add(decoration);
            }
        }

        if (!alreadyOn)
        {
            decorations.Add(location == TextDecorationLocation.Underline
                ? TextDecorations.Underline[0]
                : TextDecorations.Strikethrough[0]);
        }

        ApplyToSelection(() => Editor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, decorations));
    }

    private void FontFamily_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFormattingSync || FontFamilyBox.SelectedItem is not string family)
        {
            return;
        }

        if (!EnsureSelection())
        {
            return;
        }

        ApplyToSelection(() =>
            Editor.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily(family)));
    }

    private void FontSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFormattingSync)
        {
            return;
        }

        var raw = FontSizeBox.SelectedItem as string ?? FontSizeBox.Text;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out var points)
            && !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out points))
        {
            return;
        }

        points = Math.Clamp(points, 6, 200);
        if (!EnsureSelection())
        {
            return;
        }

        ApplyToSelection(() => Editor.Selection.ApplyPropertyValue(
            TextElement.FontSizeProperty,
            FlowDocumentBridge.ToPixels(points)));
    }

    private void TextColor_Click(object sender, RoutedEventArgs e) => OpenMenu(TextColorButton);

    private void TextColourChosen(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string hex } || !EnsureSelection())
        {
            return;
        }

        var brush = hex.Length == 0
            ? TryFindResource("WlText") as Brush ?? Brushes.White
            : FlowDocumentBridge.ParseBrush(hex) ?? Brushes.White;

        ColorSwatch.Background = brush;
        ApplyToSelection(() => Editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, brush));
    }

    // ── Paragraph formatting ─────────────────────────────────────────────────

    private void Align_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        var alignment = tag switch
        {
            "Center" => TextAlignment.Center,
            "Right" => TextAlignment.Right,
            "Justify" => TextAlignment.Justify,
            _ => TextAlignment.Left
        };

        ApplyToParagraphs(paragraph => paragraph.TextAlignment = alignment);
        UpdateFormattingState();
    }

    private void IndentMore_Click(object sender, RoutedEventArgs e) => ShiftIndent(IndentStepPoints);

    private void IndentLess_Click(object sender, RoutedEventArgs e) => ShiftIndent(-IndentStepPoints);

    private void ShiftIndent(double points) => ApplyToParagraphs(paragraph =>
    {
        var margin = paragraph.Margin;
        margin.Left = Math.Max(0, margin.Left + FlowDocumentBridge.ToPixels(points));
        paragraph.Margin = margin;
    });

    private void Spacing_Click(object sender, RoutedEventArgs e) => OpenMenu(SpacingButton);

    private void LineSpacing_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }
            || !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var multiplier))
        {
            return;
        }

        ApplyToParagraphs(paragraph =>
        {
            if (Math.Abs(multiplier - 1.0) < 0.001)
            {
                paragraph.LineHeight = double.NaN;
                paragraph.LineStackingStrategy = LineStackingStrategy.MaxHeight;
                return;
            }

            paragraph.LineHeight = paragraph.FontSize * multiplier * 1.2;
            paragraph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        });
    }

    private void ParagraphSpacing_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag })
        {
            return;
        }

        var step = FlowDocumentBridge.ToPixels(6);

        ApplyToParagraphs(paragraph =>
        {
            var margin = paragraph.Margin;

            switch (tag)
            {
                case "before":
                    margin.Top += step;
                    break;
                case "after":
                    margin.Bottom += step;
                    break;
                case "none":
                    margin.Top = 0;
                    margin.Bottom = 0;
                    break;
                case "firstline":
                    paragraph.TextIndent = FlowDocumentBridge.ToPixels(IndentStepPoints);
                    return;
                case "nofirstline":
                    paragraph.TextIndent = 0;
                    return;
            }

            paragraph.Margin = margin;
        });
    }

    private void ParagraphStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFormattingSync || StyleBox.SelectedIndex < 0)
        {
            return;
        }

        var level = StyleBox.SelectedIndex;
        var typography = Typography;

        ApplyToParagraphs(paragraph =>
        {
            if (level == 0)
            {
                paragraph.Tag = new ParagraphMetadata(null, null, (paragraph.Tag as ParagraphMetadata)?.List);
                paragraph.FontSize = FlowDocumentBridge.ToPixels(typography.DefaultSizePt);
                paragraph.FontWeight = FontWeights.Normal;
                ClearInlineSizes(paragraph);
                return;
            }

            var heading = typography.Heading(level);
            paragraph.Tag = new ParagraphMetadata(level, $"Heading{level}", (paragraph.Tag as ParagraphMetadata)?.List);
            paragraph.FontSize = FlowDocumentBridge.ToPixels(heading.SizePt);
            paragraph.FontWeight = FontWeights.SemiBold;

            // Runs carrying their own size would win over the paragraph's, so the
            // heading would keep body-text size after the style change.
            ClearInlineSizes(paragraph);
        });

        MarkDirty();
    }

    private static void ClearInlineSizes(WpfParagraph paragraph)
    {
        foreach (var inline in paragraph.Inlines.ToArray())
        {
            inline.ClearValue(TextElement.FontSizeProperty);
            inline.ClearValue(TextElement.FontWeightProperty);
        }
    }

    // ── Lists ────────────────────────────────────────────────────────────────

    private void BulletList_Click(object sender, RoutedEventArgs e) => ToggleList(TextMarkerStyle.Disc);

    private void NumberedList_Click(object sender, RoutedEventArgs e) => ToggleList(TextMarkerStyle.Decimal);

    /// <summary>
    /// Turns the selected paragraphs into a list, or back into ordinary paragraphs.
    /// </summary>
    /// <remarks>
    /// WPF ships <c>EditingCommands.ToggleBullets</c>, which does this — and also
    /// resets the paragraphs' margins and marker offsets to its own defaults, which
    /// visibly moves the text. Doing the restructuring directly keeps the list
    /// looking like the rest of the document.
    /// </remarks>
    private void ToggleList(TextMarkerStyle marker)
    {
        var paragraphs = SelectedParagraphs().ToArray();
        if (paragraphs.Length == 0)
        {
            return;
        }

        _internalEdit = true;
        try
        {
            var existing = paragraphs[0].Parent as ListItem;

            if (existing?.Parent is WpfList list && list.MarkerStyle == marker)
            {
                UnwrapList(list);
            }
            else if (existing?.Parent is WpfList other)
            {
                other.MarkerStyle = marker;
            }
            else
            {
                WrapInList(paragraphs, marker);
            }
        }
        finally
        {
            _internalEdit = false;
        }

        _documentGeneration++;
        MarkDirty();
        UpdateFormattingState();
        QueueAnalysis(TimeSpan.FromMilliseconds(120), fullDocument: true);
    }

    /// <summary>
    /// The collection a block actually lives in.
    /// </summary>
    /// <remarks>
    /// WPF gives a block its logical <c>Parent</c> but no way to reach the
    /// <see cref="BlockCollection"/> holding it, and that collection is what any
    /// structural edit has to operate on. Each container exposes its own property,
    /// so the type has to be recovered by hand.
    /// </remarks>
    private BlockCollection HostCollectionOf(Block block) => block.Parent switch
    {
        FlowDocument document => document.Blocks,
        ListItem item => item.Blocks,
        TableCell cell => cell.Blocks,
        Section section => section.Blocks,
        Floater floater => floater.Blocks,
        Figure figure => figure.Blocks,
        _ => Editor.Document.Blocks
    };

    private void WrapInList(WpfParagraph[] paragraphs, TextMarkerStyle marker)
    {
        var host = HostCollectionOf(paragraphs[0]);

        var list = new WpfList
        {
            MarkerStyle = marker,
            MarkerOffset = FlowDocumentBridge.ToPixels(9),
            Padding = new Thickness(FlowDocumentBridge.ToPixels(18), 0, 0, 0),
            Margin = new Thickness(0)
        };

        host.InsertBefore(paragraphs[0], list);

        foreach (var paragraph in paragraphs)
        {
            host.Remove(paragraph);
            list.ListItems.Add(new ListItem(paragraph));
        }
    }

    private void UnwrapList(WpfList list)
    {
        var host = HostCollectionOf(list);

        var released = list.ListItems
            .SelectMany(item => item.Blocks.OfType<WpfParagraph>())
            .ToArray();

        foreach (var paragraph in released)
        {
            (paragraph.Parent as ListItem)?.Blocks.Remove(paragraph);
            host.InsertBefore(list, paragraph);
        }

        host.Remove(list);
    }

    // ── History ──────────────────────────────────────────────────────────────

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.CanUndo)
        {
            Editor.Undo();
            _documentGeneration++;
            QueueAnalysis(TimeSpan.FromMilliseconds(120), fullDocument: true);
        }
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.CanRedo)
        {
            Editor.Redo();
            _documentGeneration++;
            QueueAnalysis(TimeSpan.FromMilliseconds(120), fullDocument: true);
        }
    }

    /// <summary>Groups several edits so one Ctrl+Z undoes the whole action.</summary>
    private void BeginUndoBatch() => Editor.BeginChange();

    private void EndUndoBatch() => Editor.EndChange();

    // ── Zoom ─────────────────────────────────────────────────────────────────

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoomIndex + 1);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoomIndex - 1);

    private void Canvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        e.Handled = true;
        SetZoom(_zoomIndex + Math.Sign(e.Delta));
    }

    private void SetZoom(int index)
    {
        _zoomIndex = Math.Clamp(index, 0, ZoomSteps.Length - 1);
        var scale = ZoomSteps[_zoomIndex];

        ZoomTransform.ScaleX = scale;
        ZoomTransform.ScaleY = scale;
        Controls.Type.SetTracked(ZoomText, $"{scale * 100:0}%");

        // The measured column scales with the zoom, so the line length a writer set
        // stays the line length they see.
        CanvasMeasure.MaxWidth = 760 / scale;
    }

    // ── Find and replace ─────────────────────────────────────────────────────

    private void Find_Click(object sender, RoutedEventArgs e) => OpenFind(replace: false);

    private void OpenFind(bool replace)
    {
        FindBar.Visibility = Visibility.Visible;
        Motion.Reveal(FindBar, offset: 4);

        var target = replace ? ReplaceBox : FindBox;
        target.Focus();
        target.SelectAll();
    }

    private void CloseFind_Click(object sender, RoutedEventArgs e) => CloseFind();

    private void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        FindStatusText.Text = string.Empty;
        Editor.Focus();
    }

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        FindNext();
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();

    /// <summary>
    /// Selects the next match after the caret, wrapping once.
    /// </summary>
    /// <remarks>
    /// Searching runs over the flat projection rather than over the document, so a
    /// phrase spanning several runs of different formatting is still one match —
    /// which is what a person means by "find this sentence".
    /// </remarks>
    private void FindNext()
    {
        var needle = FindBox.Text;
        if (string.IsNullOrEmpty(needle))
        {
            return;
        }

        var index = DocumentTextIndex.Build(Editor.Document, _documentGeneration);
        _index = index;

        var from = Math.Max(0, index.OffsetOf(Editor.Selection.End));
        var found = index.Text.IndexOf(needle, Math.Min(from, index.Text.Length), StringComparison.CurrentCultureIgnoreCase);

        if (found < 0)
        {
            found = index.Text.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase);
            if (found < 0)
            {
                FindStatusText.Text = "Не найдено";
                return;
            }

            FindStatusText.Text = "Поиск с начала";
        }
        else
        {
            var total = CountOccurrences(index.Text, needle);
            FindStatusText.Text = total == 1 ? "1 совпадение" : $"{total} совпадений";
        }

        if (index.RangeFor(found, needle.Length) is not { } range)
        {
            return;
        }

        Editor.Focus();
        Editor.Selection.Select(range.Start, range.End);
        range.Start.Paragraph?.BringIntoView();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.CurrentCultureIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        var needle = FindBox.Text;
        if (string.IsNullOrEmpty(needle))
        {
            return;
        }

        // Replace acts on a current match; without one it finds the next instead,
        // which is what every editor does with an empty selection.
        if (!Editor.Selection.IsEmpty
            && string.Equals(Editor.Selection.Text, needle, StringComparison.CurrentCultureIgnoreCase))
        {
            _internalEdit = true;
            try
            {
                Editor.Selection.Text = ReplaceBox.Text;
            }
            finally
            {
                _internalEdit = false;
            }

            _documentGeneration++;
            MarkDirty();
        }

        FindNext();
        QueueAnalysis(TimeSpan.FromMilliseconds(150), fullDocument: true);
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        var needle = FindBox.Text;
        if (string.IsNullOrEmpty(needle))
        {
            return;
        }

        var replacement = ReplaceBox.Text;
        var replaced = 0;

        _internalEdit = true;
        BeginUndoBatch();
        try
        {
            // Rebuilt after each replacement and walked backwards from the end, so a
            // replacement of a different length cannot invalidate the next position.
            var index = DocumentTextIndex.Build(Editor.Document, _documentGeneration);
            var positions = new List<int>();
            var cursor = 0;

            while ((cursor = index.Text.IndexOf(needle, cursor, StringComparison.CurrentCultureIgnoreCase)) >= 0)
            {
                positions.Add(cursor);
                cursor += needle.Length;
            }

            foreach (var position in Enumerable.Reverse(positions))
            {
                if (index.RangeFor(position, needle.Length) is not { } range)
                {
                    continue;
                }

                range.Text = replacement;
                replaced++;
            }
        }
        finally
        {
            EndUndoBatch();
            _internalEdit = false;
        }

        _documentGeneration++;
        _index = null;
        MarkDirty();

        FindStatusText.Text = replaced == 0 ? "Не найдено" : $"Заменено: {replaced}";
        QueueAnalysis(TimeSpan.FromMilliseconds(150), fullDocument: true);
    }

    // ── Selection state ──────────────────────────────────────────────────────

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e) => UpdateFormattingState();

    /// <summary>
    /// Reflects the caret's formatting in the toolbar.
    /// </summary>
    /// <remarks>
    /// Guarded by <see cref="_suppressFormattingSync"/> because setting a combo box's
    /// selection raises its own change event, which would immediately re-apply the
    /// value to the selection and make the toolbar fight the caret.
    /// </remarks>
    private void UpdateFormattingState()
    {
        if (Editor is null || BoldToggle is null)
        {
            return;
        }

        _suppressFormattingSync = true;
        try
        {
            var selection = Editor.Selection;

            BoldToggle.IsChecked = selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight weight
                                   && weight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight();

            ItalicToggle.IsChecked = selection.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle style
                                     && style != FontStyles.Normal;

            var decorations = selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
            UnderlineToggle.IsChecked = decorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true;
            StrikeToggle.IsChecked = decorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true;

            if (selection.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily family)
            {
                FontFamilyBox.SelectedItem = family.Source;
            }

            if (selection.GetPropertyValue(TextElement.FontSizeProperty) is double pixels)
            {
                var points = Math.Round(FlowDocumentBridge.ToPoints(pixels));
                FontSizeBox.Text = points.ToString("0", CultureInfo.CurrentCulture);
            }

            var paragraph = Editor.CaretPosition?.Paragraph;
            var alignment = paragraph?.TextAlignment ?? TextAlignment.Left;
            AlignLeftToggle.IsChecked = alignment == TextAlignment.Left;
            AlignCenterToggle.IsChecked = alignment == TextAlignment.Center;
            AlignRightToggle.IsChecked = alignment == TextAlignment.Right;
            AlignJustifyToggle.IsChecked = alignment == TextAlignment.Justify;

            var list = (paragraph?.Parent as ListItem)?.Parent as WpfList;
            BulletToggle.IsChecked = list is { MarkerStyle: TextMarkerStyle.Disc or TextMarkerStyle.Circle or TextMarkerStyle.Square };
            NumberToggle.IsChecked = list is { MarkerStyle: TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin };

            StyleBox.SelectedIndex = (paragraph?.Tag as ParagraphMetadata)?.OutlineLevel is { } level
                ? Math.Clamp(level, 0, StyleBox.Items.Count - 1)
                : 0;
        }
        finally
        {
            _suppressFormattingSync = false;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// True when there is something to format.
    /// </summary>
    /// <remarks>
    /// An empty selection is still valid: WPF carries the applied property forward
    /// onto the next characters typed, which is what makes pressing Ctrl+B before
    /// typing work the way people expect.
    /// </remarks>
    private bool EnsureSelection()
    {
        if (!Editor.IsKeyboardFocusWithin)
        {
            Editor.Focus();
        }

        return true;
    }

    private void ApplyToSelection(Action apply)
    {
        _internalEdit = true;
        try
        {
            apply();
        }
        finally
        {
            _internalEdit = false;
        }

        _documentGeneration++;
        MarkDirty();
        UpdateFormattingState();
    }

    private void ApplyToParagraphs(Action<WpfParagraph> apply)
    {
        var paragraphs = SelectedParagraphs().ToArray();
        if (paragraphs.Length == 0)
        {
            return;
        }

        _internalEdit = true;
        BeginUndoBatch();
        try
        {
            foreach (var paragraph in paragraphs)
            {
                apply(paragraph);
            }
        }
        finally
        {
            EndUndoBatch();
            _internalEdit = false;
        }

        _documentGeneration++;
        MarkDirty();
    }

    /// <summary>Every paragraph the selection touches, or the caret's own.</summary>
    private IEnumerable<WpfParagraph> SelectedParagraphs()
    {
        var start = Editor.Selection.Start;
        var end = Editor.Selection.End;

        var first = start.Paragraph;
        if (first is null)
        {
            yield break;
        }

        var current = first;
        var guard = 0;

        while (current is not null && guard++ < 20_000)
        {
            yield return current;

            if (current.ContentEnd.CompareTo(end) >= 0)
            {
                yield break;
            }

            current = current.NextBlock as WpfParagraph
                      ?? current.ContentEnd.GetNextInsertionPosition(LogicalDirection.Forward)?.Paragraph;

            // NextBlock stops at the end of a list or a table cell; following the
            // insertion position instead walks into the next container.
            if (current is not null && current.ContentStart.CompareTo(end) > 0)
            {
                yield break;
            }
        }
    }

    private static void OpenMenu(FrameworkElement owner)
    {
        if (owner.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = owner;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
