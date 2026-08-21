using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using WriteLite.Controls;
using WriteLite.Services;
using WriteLite.Services.Ai;
using WriteLite.Services.Documents;
using WriteLite.Services.Lexical;
using MessageBox = System.Windows.MessageBox;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
// WinForms is enabled in this project and ships same-named menu and font types;
// the WPF ones are always what a FlowDocument surface means.
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;
using FontStyle = System.Windows.FontStyle;

namespace WriteLite.Views.Pages;

/// <summary>
/// The AI context menu and the navigable word panel.
/// </summary>
/// <remarks>
/// Two guarantees hold across everything here.
///
/// <em>Nothing leaves the machine.</em> Rewriting goes to the loopback model the
/// product already ships; lookups go to dictionary packs on disk. There is no code
/// path from this file to a network.
///
/// <em>Nothing is replaced without being seen.</em> Every AI result goes through
/// <see cref="AiPreviewWindow"/>, and the only way text reaches the document is an
/// outcome the user chose there. A cancelled operation leaves the document byte-for-byte
/// as it was.
/// </remarks>
public partial class EditorPage
{
    /// <summary>Trail length for word navigation. Long enough to retrace, short enough to stay a line.</summary>
    private const int MaxWordHistory = 30;

    private readonly List<WordVisit> _wordHistory = [];

    private IEditorAiService? _editorAi;
    private LexicalLookupService? _lexical;
    private CancellationTokenSource? _lookupCancellation;

    /// <summary>Wires the local model and the dictionary packs once the shell has them.</summary>
    public void BindAi(IEditorAiService? editorAi, LexicalLookupService? lexical)
    {
        _editorAi = editorAi;
        _lexical = lexical;
    }

    /// <summary>
    /// Wires the application's checking stack into the editor's own analysis loop.
    /// </summary>
    /// <remarks>
    /// <para>Without this the editor checked documents with a private, deliberately minimal
    /// analyzer while the full stack — the form index, the language engine, the user
    /// dictionary and the ignore list, the punctuation model and WriteAI — existed a few
    /// metres away in the same process, wired only to the system-wide field monitor. The
    /// system tray watched other applications' text boxes with everything WriteLite has; the
    /// product's own editor watched with a fraction of it, and said «ЛОКАЛЬНАЯ ПРОВЕРКА»
    /// either way, because that label is a constant and not a report about a backend.</para>
    ///
    /// <para>Passing null restores the fallback, which is what an unbound host gets.</para>
    /// </remarks>
    /// <param name="fastAnalyzer">
    /// The native deterministic lane — rules and spelling only. Published first, on its own
    /// timescale, so the sidebar does not wait for the language engine or the model. Null
    /// leaves the editor single-lane, which is what it was before.
    /// </param>
    public void BindAnalyzer(ITextAnalyzer? analyzer, ITextAnalyzer? fastAnalyzer = null)
    {
        _analyzer = analyzer;
        _fastAnalyzer = fastAnalyzer;
        CompatibilityLogger.Technical(
            "editor-analyzer-bound",
            $"analyzer={analyzer?.GetType().Name ?? "none"} fast={fastAnalyzer?.GetType().Name ?? "none"} "
            + $"staged={(analyzer is IStagedTextAnalyzer ? 1 : 0)}");

        // What is on screen was checked by whatever was bound before. Re-check it against
        // the stack that is bound now, or the document open at startup keeps the fallback's
        // answer until the next keystroke.
        if (analyzer is not null)
        {
            _lastAnalyzedText = string.Empty;
            _lastAnalyzedIssues = [];
            _ = ScheduleAnalysisAsync(TimeSpan.FromMilliseconds(150));
        }
    }

    // ── Context menu ─────────────────────────────────────────────────────────

    private void Editor_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-clicking inside a selection must not collapse it: the selection is
        // what the menu is about to act on.
        var position = Editor.GetPositionFromPoint(e.GetPosition(Editor), snapToText: true);
        if (position is null)
        {
            Editor.ContextMenu = BuildContextMenu();
            return;
        }

        var selection = Editor.Selection;
        if (!selection.IsEmpty
            && position.CompareTo(selection.Start) >= 0
            && position.CompareTo(selection.End) <= 0)
        {
            Editor.ContextMenu = BuildContextMenu();
            return;
        }

        // Otherwise select the word under the cursor, so "Синонимы" has a subject
        // without the user having to select first.
        Editor.CaretPosition = position;
        SelectWordAt(position);
        Editor.ContextMenu = BuildContextMenu();
    }

    private void Editor_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Keyboard invocation has no preview mouse event. Mouse invocation already
        // installed the menu before WPF chose which popup to open.
        Editor.ContextMenu ??= BuildContextMenu();
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu { Style = TryFindResource("WlContextMenu") as Style };
        var selection = Editor.Selection.Text;
        var hasSelection = !string.IsNullOrWhiteSpace(selection);

        var ai = new MenuItem
        {
            Header = "WriteLite AI",
            IsEnabled = hasSelection,
            Style = TryFindResource("WlMenuItem") as Style
        };

        // The word the selection is about, with the punctuation a double-click drags
        // along stripped off. Before this, «слова.» failed an all-letters test and the
        // lookup entries were simply absent from the menu.
        var word = WordNavigation.Normalize(selection);

        if (word is not null)
        {
            ai.Items.Add(MenuAction($"Значение «{word}»", () => ShowWordPanelAsync(word)));
            ai.Items.Add(Separator());
        }

        ai.Items.Add(MenuAction("Объяснить", () => RunRewriteAsync(RewriteOperation.Explain)));
        ai.Items.Add(MenuAction("Перевести на английский", () => RunRewriteAsync(RewriteOperation.Translate)));
        ai.Items.Add(Separator());
        ai.Items.Add(MenuAction("Переписать", () => RunRewriteAsync(RewriteOperation.Rewrite)));
        ai.Items.Add(MenuAction("Улучшить стиль", () => RunRewriteAsync(RewriteOperation.ImproveStyle)));
        ai.Items.Add(MenuAction("Сделать формальнее", () => RunRewriteAsync(RewriteOperation.Formal)));
        ai.Items.Add(MenuAction("Сделать проще", () => RunRewriteAsync(RewriteOperation.Casual)));
        ai.Items.Add(MenuAction("Упростить", () => RunRewriteAsync(RewriteOperation.Simplify)));
        ai.Items.Add(MenuAction("Сократить", () => RunRewriteAsync(RewriteOperation.Shorten)));
        ai.Items.Add(MenuAction("Расширить", () => RunRewriteAsync(RewriteOperation.Expand)));
        ai.Items.Add(MenuAction("Исправить грамматику", () => RunRewriteAsync(RewriteOperation.Grammar)));
        ai.Items.Add(Separator());
        ai.Items.Add(MenuAction("Своя инструкция…", () => RunRewriteAsync(RewriteOperation.Custom)));

        menu.Items.Add(ai);
        menu.Items.Add(Separator());

        // Top level, not inside the AI submenu: a dictionary lookup is not an AI
        // operation, and burying it two levels down is part of why the flow was never
        // found. The word is already normalised, so nothing has to be retyped.
        if (word is not null)
        {
            menu.Items.Add(MenuAction($"Открыть «{word}» в словаре", () =>
            {
                WordNavigationRequested?.Invoke(word);
                return Task.CompletedTask;
            }));

            menu.Items.Add(Separator());
        }

        menu.Items.Add(CommandItem("Вырезать", ApplicationCommands.Cut));
        menu.Items.Add(CommandItem("Копировать", ApplicationCommands.Copy));
        menu.Items.Add(CommandItem("Вставить", ApplicationCommands.Paste));
        menu.Items.Add(Separator());
        menu.Items.Add(CommandItem("Выделить всё", ApplicationCommands.SelectAll));

        return menu;
    }

    private MenuItem MenuAction(string header, Func<Task> action)
    {
        var item = new MenuItem
        {
            Header = header,
            Style = TryFindResource("WlMenuItem") as Style
        };

        item.Click += async (_, _) => await action();
        return item;
    }

    private MenuItem CommandItem(string header, RoutedUICommand command)
    {
        var item = new MenuItem
        {
            Header = header,
            Command = command,
            CommandTarget = Editor,
            Style = TryFindResource("WlMenuItem") as Style
        };

        return item;
    }

    private Separator Separator() => new() { Style = TryFindResource("WlMenuSeparator") as Style };

    /// <summary>
    /// Double-clicking a word fills the dictionary panel with it.
    /// </summary>
    /// <remarks>
    /// This is the gesture people already use, and until now it did nothing here: the
    /// panel could only be filled from a context-menu entry that itself rejected any
    /// selection carrying punctuation. The panel is only filled when it is already the
    /// visible tab — a double click in the middle of writing must not yank the side
    /// panel away from the issue list the writer was working through.
    /// </remarks>
    private void Editor_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_lexical is null || TabWord.IsChecked != true)
        {
            return;
        }

        if (WordNavigation.Normalize(Editor.Selection.Text) is { } word)
        {
            _ = LookupWordAsync(word, LexicalLanguage.Unknown, recordHistory: true);
        }
    }

    // ── Rewriting ────────────────────────────────────────────────────────────

    private void QuickAi_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<RewriteOperation>(tag, out var operation))
        {
            _ = RunRewriteAsync(operation);
        }
    }

    /// <summary>
    /// Runs one AI operation on the selection, previews it, and applies only what the user accepts.
    /// </summary>
    private async Task RunRewriteAsync(RewriteOperation operation)
    {
        if (_editorAi is null)
        {
            ShowError("AI недоступен", "Локальный движок ещё запускается. Попробуйте через несколько секунд.");
            return;
        }

        var selection = Editor.Selection;
        var text = selection.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            ShowError("Нечего обрабатывать", "Выделите слово, предложение или абзац.");
            return;
        }

        string? instruction = null;
        if (operation == RewriteOperation.Custom)
        {
            instruction = PromptForInstruction();
            if (string.IsNullOrWhiteSpace(instruction))
            {
                return;
            }
        }

        // Captured before the dialog opens: the pointers must describe the document
        // as it was when the user asked, and the selection can be lost to focus.
        var start = selection.Start;
        var end = selection.End;
        var request = new RewriteRequest(
            operation,
            text,
            ReadContext(start, before: true),
            ReadContext(end, before: false),
            instruction);

        var preview = new AiPreviewWindow(
            OperationLabel(operation, instruction),
            token => _editorAi.RewriteAsync(request, token))
        {
            Owner = Window.GetWindow(this)
        };

        var accepted = preview.ShowDialog() == true;

        // Cancelling — by button, by Escape, or by closing the window — leaves the
        // document untouched. This is the guarantee, expressed as an early return.
        if (!accepted || preview.AcceptedText is not { Length: > 0 } replacement)
        {
            return;
        }

        ApplyRewrite(start, end, replacement, preview.Outcome);
    }

    /// <summary>
    /// Writes an accepted suggestion into the document as a single undoable action.
    /// </summary>
    /// <remarks>
    /// One undo unit is the point: after accepting a rewrite, Ctrl+Z must restore
    /// exactly the original text in one press, not unwind it word by word.
    /// </remarks>
    private void ApplyRewrite(TextPointer start, TextPointer end, string replacement, AiPreviewOutcome outcome)
    {
        _internalEdit = true;
        BeginUndoBatch();

        try
        {
            if (outcome == AiPreviewOutcome.InsertBelow)
            {
                var paragraph = end.Paragraph;
                var host = paragraph is null ? Editor.Document.Blocks : HostCollectionOf(paragraph);
                var inserted = new System.Windows.Documents.Paragraph(new Run(replacement));

                if (paragraph is not null)
                {
                    host.InsertAfter(paragraph, inserted);
                }
                else
                {
                    host.Add(inserted);
                }

                Editor.CaretPosition = inserted.ContentEnd;
            }
            else
            {
                var range = new TextRange(start, end);
                var captured = CaptureFormatting(start);
                range.Text = replacement;
                ApplyCaptured(new TextRange(range.Start, range.End), captured);
                Editor.CaretPosition = range.End;
            }
        }
        catch (ArgumentException)
        {
            // The document changed under the preview; better to do nothing than to
            // write the suggestion into whatever now occupies those positions.
            ShowError("Текст изменился", "Документ был изменён во время обработки. Изменения не применены.");
        }
        finally
        {
            EndUndoBatch();
            _internalEdit = false;
        }

        _documentGeneration++;
        MarkDirty();
        _ = ScheduleAnalysisAsync(TimeSpan.FromMilliseconds(150));
    }

    /// <summary>Reads a bounded amount of text on one side of the selection.</summary>
    private static string ReadContext(TextPointer anchor, bool before)
    {
        try
        {
            var limit = TextRewriteService.MaxContextChars;
            var other = anchor.GetPositionAtOffset(before ? -limit * 2 : limit * 2, LogicalDirection.Forward)
                        ?? (before ? anchor.DocumentStart : anchor.DocumentEnd);

            var range = before ? new TextRange(other, anchor) : new TextRange(anchor, other);
            return range.Text;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private string? PromptForInstruction()
    {
        var dialog = new InstructionPrompt { Owner = Window.GetWindow(this) };
        return dialog.ShowDialog() == true ? dialog.Instruction : null;
    }

    private static string OperationLabel(RewriteOperation operation, string? instruction) => operation switch
    {
        RewriteOperation.Rewrite => "Переписать",
        RewriteOperation.ImproveStyle => "Улучшить стиль",
        RewriteOperation.Formal => "Формальнее",
        RewriteOperation.Casual => "Проще",
        RewriteOperation.Simplify => "Упростить",
        RewriteOperation.Shorten => "Сократить",
        RewriteOperation.Expand => "Расширить",
        RewriteOperation.Grammar => "Грамматика",
        RewriteOperation.Explain => "Объяснить",
        RewriteOperation.Translate => "Перевод",
        RewriteOperation.Custom => instruction is { Length: > 0 } ? Shorten(instruction) : "Своя инструкция",
        _ => "Обработка"
    };

    private static string Shorten(string text) =>
        text.Length <= 60 ? text : text[..57] + "…";

    // ── Formatting capture ───────────────────────────────────────────────────

    /// <summary>
    /// Records the character formatting in force at a position.
    /// </summary>
    /// <remarks>
    /// Needed because setting <see cref="TextRange.Text"/> discards the range's
    /// formatting. Without capturing and re-applying it, correcting a typo inside a
    /// bold heading leaves the corrected word in body text.
    /// </remarks>
    private static CapturedFormatting CaptureFormatting(TextPointer position)
    {
        var element = position.Parent as TextElement ?? position.Paragraph;

        return new CapturedFormatting(
            element?.FontWeight,
            element?.FontStyle,
            element?.FontFamily,
            element?.FontSize,
            element?.Foreground,
            (element as Inline)?.TextDecorations);
    }

    private static void ApplyCaptured(TextRange range, CapturedFormatting formatting)
    {
        if (formatting.Weight is { } weight)
        {
            range.ApplyPropertyValue(TextElement.FontWeightProperty, weight);
        }

        if (formatting.Style is { } style)
        {
            range.ApplyPropertyValue(TextElement.FontStyleProperty, style);
        }

        if (formatting.Family is { } family)
        {
            range.ApplyPropertyValue(TextElement.FontFamilyProperty, family);
        }

        if (formatting.Size is { } size)
        {
            range.ApplyPropertyValue(TextElement.FontSizeProperty, size);
        }

        if (formatting.Foreground is { } foreground)
        {
            range.ApplyPropertyValue(TextElement.ForegroundProperty, foreground);
        }

        if (formatting.Decorations is { } decorations)
        {
            range.ApplyPropertyValue(Inline.TextDecorationsProperty, decorations);
        }
    }

    private readonly record struct CapturedFormatting(
        FontWeight? Weight,
        FontStyle? Style,
        System.Windows.Media.FontFamily? Family,
        double? Size,
        System.Windows.Media.Brush? Foreground,
        TextDecorationCollection? Decorations);

    // ── Word panel ───────────────────────────────────────────────────────────

    /// <summary>Selects the whole word a position falls inside.</summary>
    private void SelectWordAt(TextPointer position)
    {
        var start = position.GetInsertionPosition(LogicalDirection.Backward);
        var end = position.GetInsertionPosition(LogicalDirection.Forward);

        if (start is null || end is null)
        {
            return;
        }

        // Walk out to the word boundaries one insertion position at a time; the
        // alternative, EditingCommands.SelectLeftByWord, moves the caret and fires
        // selection events the panel would react to twice.
        var guard = 0;
        while (guard++ < 64
               && start.GetPointerContext(LogicalDirection.Backward) == TextPointerContext.Text
               && start.GetTextInRun(LogicalDirection.Backward) is { Length: > 0 } backward
               && char.IsLetterOrDigit(backward[^1]))
        {
            start = start.GetPositionAtOffset(-1, LogicalDirection.Backward) ?? start;
        }

        guard = 0;
        while (guard++ < 64
               && end.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text
               && end.GetTextInRun(LogicalDirection.Forward) is { Length: > 0 } forward
               && char.IsLetterOrDigit(forward[0]))
        {
            end = end.GetPositionAtOffset(1, LogicalDirection.Forward) ?? end;
        }

        Editor.Selection.Select(start, end);
    }

    /// <summary>Opens the word panel for a word, recording the step in the trail.</summary>
    private async Task ShowWordPanelAsync(string word, LexicalLanguage language = LexicalLanguage.Unknown)
    {
        TabWord.IsChecked = true;
        await LookupWordAsync(word, language, recordHistory: true);
    }

    private async Task LookupWordAsync(string word, LexicalLanguage language, bool recordHistory)
    {
        if (_lexical is null)
        {
            ShowWordMessage("Словарь ещё загружается. Проверка текста продолжает работать.");
            return;
        }

        _lookupCancellation?.Cancel();
        _lookupCancellation?.Dispose();
        _lookupCancellation = new CancellationTokenSource();
        var token = _lookupCancellation.Token;

        try
        {
            var sentence = ReadSurroundingSentence();
            var card = await _lexical.LookupAsync(word, sentence, language, token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (recordHistory)
            {
                PushWordHistory(word, language);
            }

            RenderWordCard(card);
        }
        catch (OperationCanceledException)
        {
            // A newer lookup owns the panel now.
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("editor-lexical-failed", $"type={exception.GetType().Name}");
            ShowWordMessage("Не удалось найти слово. Словарные данные недоступны.");
        }
    }

    private string ReadSurroundingSentence()
    {
        var paragraph = Editor.Selection.Start.Paragraph;
        if (paragraph is null)
        {
            return string.Empty;
        }

        var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
        return text.Length <= 600 ? text : text[..600];
    }

    private void RenderWordCard(LexicalCard card)
    {
        WordEmptyState.Visibility = Visibility.Collapsed;
        WordMessageText.Visibility = Visibility.Collapsed;

        if (card.IsEmpty)
        {
            WordArticle.Visibility = Visibility.Collapsed;
            ShowWordMessage(card.StatusMessage ?? "Слова нет в установленных словарях.");

            // The AI actions stay reachable even without a dictionary entry: they
            // do not depend on the packs.
            WordActionsBlock.Visibility = Visibility.Visible;
            return;
        }

        WordArticle.Visibility = Visibility.Visible;
        WordTitleText.Text = card.Word;

        var grammar = new List<string>();
        if (card.PartOfSpeech.Length > 0)
        {
            grammar.Add(card.PartOfSpeech);
        }

        if (!string.Equals(card.Lemma, card.Word, StringComparison.CurrentCultureIgnoreCase))
        {
            grammar.Add($"нач. форма: {card.Lemma}");
        }

        WordGrammarText.Text = string.Join(" · ", grammar);
        WordGrammarText.Visibility = grammar.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var definitions = card.Definitions
            .Select((definition, index) => new { Number = (index + 1).ToString("00"), definition.Definition })
            .Select(entry => new { entry.Number, Text = entry.Definition })
            .ToArray();

        DefinitionsList.ItemsSource = definitions;
        DefinitionsBlock.Visibility = definitions.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        FillWordChips(SynonymsPanel, SynonymsBlock, card.Synonyms, canReplace: true);
        FillWordChips(AntonymsPanel, AntonymsBlock, card.Antonyms, canReplace: true);

        Controls.Type.SetTracked(
            TranslationsLabel,
            card.Language == LexicalLanguage.English ? "ПЕРЕВОД НА РУССКИЙ" : "ПЕРЕВОД НА АНГЛИЙСКИЙ");
        FillWordChips(TranslationsPanel, TranslationsBlock, card.Translations, canReplace: false);

        FormsText.Text = card.Forms.Count > 0 ? string.Join(" · ", card.Forms) : string.Empty;
        FormsBlock.Visibility = card.Forms.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        WordSourceText.Text = card.Provenance;
        WordActionsBlock.Visibility = Visibility.Visible;

        Motion.RevealSequence(
        [
            WordTitleText,
            DefinitionsBlock,
            SynonymsBlock,
            AntonymsBlock,
            TranslationsBlock,
            FormsBlock
        ]);
    }

    /// <summary>
    /// Builds one row of clickable related words.
    /// </summary>
    /// <remarks>
    /// Two gestures on purpose. A single click substitutes the word into the
    /// document, which is what someone reaching for a synonym while writing wants.
    /// A double click opens that word's own article, which is what turns the panel
    /// into a lexical database you can walk — the behaviour the dictionary page
    /// already has, brought to where the writing happens.
    /// </remarks>
    private void FillWordChips(
        System.Windows.Controls.Panel panel,
        FrameworkElement block,
        IReadOnlyList<LexicalWord> words,
        bool canReplace)
    {
        panel.Children.Clear();

        if (words.Count == 0)
        {
            block.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var word in words)
        {
            var chip = new WordChip
            {
                Word = word.Value,
                Language = word.Language,
                HasArticle = word.HasArticle,
                ToolTip = canReplace
                    ? "Клик — заменить в тексте, двойной клик — открыть статью"
                    : "Двойной клик — открыть статью"
            };

            chip.MouseDoubleClick += async (_, args) =>
            {
                args.Handled = true;
                await ShowWordPanelAsync(word.Value, word.Language);
            };

            if (canReplace)
            {
                chip.Click += (_, _) => ReplaceSelectionWith(word.Value);
            }
            else if (word.HasArticle)
            {
                chip.Click += async (_, _) => await ShowWordPanelAsync(word.Value, word.Language);
            }

            panel.Children.Add(chip);
        }

        block.Visibility = Visibility.Visible;
    }

    /// <summary>Substitutes a chosen synonym for the selected word, keeping its formatting.</summary>
    private void ReplaceSelectionWith(string replacement)
    {
        var selection = Editor.Selection;
        if (selection.IsEmpty)
        {
            return;
        }

        _internalEdit = true;
        BeginUndoBatch();
        try
        {
            var captured = CaptureFormatting(selection.Start);
            selection.Text = replacement;
            ApplyCaptured(new TextRange(selection.Start, selection.End), captured);
        }
        finally
        {
            EndUndoBatch();
            _internalEdit = false;
        }

        _documentGeneration++;
        MarkDirty();
        _ = ScheduleAnalysisAsync(TimeSpan.FromMilliseconds(150));
    }

    private void ShowWordMessage(string message)
    {
        WordEmptyState.Visibility = Visibility.Collapsed;
        WordMessageText.Text = message;
        WordMessageText.Visibility = Visibility.Visible;
    }

    // ── Word trail ───────────────────────────────────────────────────────────

    private void PushWordHistory(string word, LexicalLanguage language)
    {
        var visit = new WordVisit(word, language);

        if (_wordHistory.Count > 0 && _wordHistory[^1] == visit)
        {
            return;
        }

        _wordHistory.Add(visit);

        if (_wordHistory.Count > MaxWordHistory)
        {
            _wordHistory.RemoveAt(0);
        }

        UpdateWordTrail();
    }

    private async void WordBack_Click(object sender, RoutedEventArgs e)
    {
        if (_wordHistory.Count < 2)
        {
            return;
        }

        _wordHistory.RemoveAt(_wordHistory.Count - 1);
        var previous = _wordHistory[^1];
        UpdateWordTrail();
        await LookupWordAsync(previous.Word, previous.Language, recordHistory: false);
    }

    private void UpdateWordTrail()
    {
        WordBackButton.IsEnabled = _wordHistory.Count > 1;

        if (_wordHistory.Count <= 1)
        {
            WordTrailText.Visibility = Visibility.Collapsed;
            return;
        }

        var recent = _wordHistory.TakeLast(4).Select(visit => visit.Word);
        WordTrailText.Text = (_wordHistory.Count > 4 ? "… → " : string.Empty) + string.Join(" → ", recent);
        WordTrailText.Visibility = Visibility.Visible;
    }

    private readonly record struct WordVisit(string Word, LexicalLanguage Language);
}

/// <summary>
/// Asks for a free-form instruction.
/// </summary>
/// <remarks>
/// A window rather than a WinForms input box: this one has to look like the rest of
/// the product, and it is the only place the user types something the model will act on.
/// </remarks>
public sealed class InstructionPrompt : Window
{
    private readonly System.Windows.Controls.TextBox _input;

    public InstructionPrompt()
    {
        Title = "Своя инструкция";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;

        _input = new System.Windows.Controls.TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 76,
            Style = TryFindResource("WlTextInput") as Style
        };

        var hint = new TextBlock
        {
            Text = "Например: «перепиши этот абзац как университетское эссе» " +
                   "или «сделай описание увереннее».",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            Style = TryFindResource("WlCaption") as Style
        };

        var title = new TextBlock
        {
            Text = "Что сделать с выделенным текстом?",
            Margin = new Thickness(0, 0, 0, 8),
            Style = TryFindResource("WlCardTitle") as Style
        };

        var ok = new System.Windows.Controls.Button
        {
            Content = "Выполнить",
            IsDefault = true,
            MinHeight = 30,
            Style = TryFindResource("WlPrimaryButton") as Style
        };
        ok.Click += (_, _) => { DialogResult = true; };

        var cancel = new System.Windows.Controls.Button
        {
            Content = "Отмена",
            IsCancel = true,
            Margin = new Thickness(0, 0, 6, 0),
            Style = TryFindResource("WlTextButton") as Style
        };

        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        actions.Children.Add(cancel);
        actions.Children.Add(ok);

        var stack = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        stack.Children.Add(title);
        stack.Children.Add(hint);
        stack.Children.Add(_input);
        stack.Children.Add(actions);

        Content = new Border
        {
            Background = TryFindResource("WlBgBase") as System.Windows.Media.Brush,
            BorderBrush = TryFindResource("WlLineStrong") as System.Windows.Media.Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = stack
        };

        Loaded += (_, _) => _input.Focus();
    }

    public string Instruction => _input.Text.Trim();
}
