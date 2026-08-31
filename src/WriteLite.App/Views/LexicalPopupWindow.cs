using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Runtime.InteropServices;
using WriteLite.Services;
using WriteLite.Services.Lexical;
using Button = System.Windows.Controls.Button;
using TabControl = System.Windows.Controls.TabControl;
using TabItem = System.Windows.Controls.TabItem;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Size = System.Windows.Size;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MediaBrush = System.Windows.Media.Brush;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using ScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;
using DockPanel = System.Windows.Controls.DockPanel;
using StackPanel = System.Windows.Controls.StackPanel;
using Grid = System.Windows.Controls.Grid;
using ColumnDefinition = System.Windows.Controls.ColumnDefinition;
using Border = System.Windows.Controls.Border;
using TextBlock = System.Windows.Controls.TextBlock;

namespace WriteLite.Views;

/// <summary>
/// Reusable Russian dictionary card: morphology, synonyms, definitions and examples.
/// Positioned via SmartPopupPlacementService; Escape closes; reusable instance.
/// </summary>
public sealed class LexicalPopupWindow : Window
{
    private static readonly MediaBrush CardBackground = ThemeResource.Brush("WlSurface", Brushes.Black);
    private static readonly MediaBrush RaisedBackground = ThemeResource.Brush("WlRaised", Brushes.DarkSlateGray);
    private static readonly MediaBrush CardBorderBrush = ThemeResource.Brush("WlLineStrong", Brushes.DimGray);
    private static readonly MediaBrush TextBrush = ThemeResource.Brush("WlText", Brushes.White);
    private static readonly MediaBrush MutedBrush = ThemeResource.Brush("WlTextSecondary", Brushes.LightGray);
    private static readonly MediaBrush FaintBrush = ThemeResource.Brush("WlTextMuted", Brushes.Gray);
    private static readonly MediaBrush AccentBrush = ThemeResource.Brush("WlBrand", Brushes.Orange);
    private static readonly MediaBrush OnAccentBrush = ThemeResource.Brush("WlOnBrand", Brushes.Black);
    private static readonly MediaBrush HoverBrush = ThemeResource.Brush("WlHover", Brushes.DimGray);

    // The headword is the largest thing in the card, the way a dictionary entry reads.
    private readonly TextBlock _titleWord = new() { FontSize = 21, FontWeight = FontWeights.Medium, Foreground = TextBrush };
    private readonly TextBlock _subtitle = new() { FontSize = 12, Foreground = MutedBrush, Margin = new Thickness(0, 5, 0, 10) };
    private readonly TextBlock _status = new() { FontSize = 12, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _source = new() { FontSize = 11, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TabControl _tabs;
    private readonly StackPanel _synonymsPanel = new();
    private readonly StackPanel _definitionsPanel = new();
    private readonly StackPanel _examplesPanel = new();
    private readonly StackPanel _morphologyPanel = new();
    private readonly StackPanel _rolePanel = new();
    private readonly StackPanel _translationsPanel = new();
    private readonly TabItem _wordTab;
    private readonly TabItem _synonymsTab;
    private readonly TabItem _definitionsTab;
    private readonly TabItem _examplesTab;
    private readonly TabItem _roleTab;
    private readonly TabItem _translationsTab;

    private LexicalLookupResult? _result;
    private WordRange? _range;
    private string? _fullText;
    private string? _targetId;
    private int _generationId;
    private long _textVersion;
    private long _requestId;
    private bool _supportsWrite;
    private Rect _pendingAnchor;
    private bool _repositionPending;
    public LexicalPopupWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 380;
        MinHeight = 200;
        MaxHeight = 480;
        SizeToContent = SizeToContent.Height;
        FontFamily = ThemeResource.Font("WlFont", "Segoe UI Variable Text, Segoe UI");
        PreviewKeyDown += OnPreviewKeyDown;

        var card = new Border
        {
            Background = CardBackground,
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 13, 16, 13),
            Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 8, Direction = 270, Opacity = .6, Color = Colors.Black }
        };

        var root = new DockPanel();
        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // A quiet mono eyebrow rather than a brand badge: the word is the subject of
        // this card, not the product.
        var eyebrow = new TextBlock
        {
            FontSize = 10,
            FontFamily = ThemeResource.Font("WlFontMono", "Cascadia Mono, Consolas"),
            Foreground = ThemeResource.Brush("WlTextSubtle", Brushes.Gray),
            VerticalAlignment = VerticalAlignment.Center
        };
        Controls.Type.SetTracked(eyebrow, "СЛОВАРЬ");
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(eyebrow);
        // The card is a summary. This is the way out of it and into the full article, and it
        // leads to the same dictionary page every other «Открыть в словаре» in the product
        // leads to, rather than to a second reading surface built for popups.
        var openFull = CreateGhostButton("В словаре", double.NaN, 26);
        openFull.Padding = new Thickness(8, 0, 8, 0);
        openFull.FontSize = 11;
        openFull.ToolTip = "Открыть полную статью в WriteLite";
        openFull.Click += (_, _) =>
        {
            var word = _result?.Lemma ?? _range?.Word;
            if (!string.IsNullOrWhiteSpace(word))
            {
                FullArticleRequested?.Invoke(this, word);
            }
        };

        var close = CreateGhostButton("\u00d7", 26, 26);
        close.Click += (_, _) => Hide();

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(openFull);
        actions.Children.Add(close);
        Grid.SetColumn(actions, 1);
        header.Children.Add(titleRow);
        header.Children.Add(actions);

        var headBlock = new StackPanel();
        headBlock.Children.Add(header);
        headBlock.Children.Add(_titleWord);
        headBlock.Children.Add(_subtitle);
        DockPanel.SetDock(headBlock, Dock.Top);
        root.Children.Add(headBlock);

        DockPanel.SetDock(_source, Dock.Bottom);
        root.Children.Add(_source);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        _tabs = new TabControl
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            // Falls back to the default template when the theme is not loaded.
            Style = ThemeResource.Style("WlTabControl")
        };
        _wordTab = CreateTab("Слово", _morphologyPanel);
        _synonymsTab = CreateTab("Синонимы", _synonymsPanel);
        _definitionsTab = CreateTab("Значения", _definitionsPanel);
        _examplesTab = CreateTab("Примеры", _examplesPanel);
        _roleTab = CreateTab("Роль", _rolePanel);
        _translationsTab = CreateTab("Перевод", _translationsPanel);
        _tabs.Items.Add(_wordTab);
        _tabs.Items.Add(_translationsTab);
        _tabs.Items.Add(_synonymsTab);
        _tabs.Items.Add(_definitionsTab);
        _tabs.Items.Add(_examplesTab);
        _tabs.Items.Add(_roleTab);
        root.Children.Add(_tabs);

        card.Child = root;
        Content = card;
    }

    public event EventHandler<LexicalReplaceRequestedEventArgs>? ReplaceRequested;
    public event EventHandler? ClosedByUser;

    /// <summary>Raised with the headword when the user asks for the full dictionary article.</summary>
    public event EventHandler<string>? FullArticleRequested;

    public long CurrentRequestId => _requestId;

    public void ShowLoading(
        WordRange range,
        Rect physicalAnchor,
        long requestId,
        string targetId,
        int generationId,
        long textVersion,
        string fullText)
    {
        _range = range;
        _fullText = fullText;
        _requestId = requestId;
        _targetId = targetId;
        _generationId = generationId;
        _textVersion = textVersion;
        _titleWord.Text = range.Word;
        _subtitle.Text = "Загрузка…";
        _status.Text = "";
        _source.Text = "";
        ClearPanels();
        _synonymsPanel.Children.Add(Muted("Ищем в локальном словаре…"));
        SetTabVisibility(word: true, synonyms: true, definitions: false, examples: false, role: false);
        _pendingAnchor = physicalAnchor;
        EnsureVisible();
        PositionForAnchor(_pendingAnchor, new Size(Width, 200));
        ScheduleMeasuredReposition();
    }

    /// <summary>
    /// Whether a newly observed field state means this card no longer describes anything.
    /// </summary>
    /// <remarks>
    /// <para><b>What actually makes a card stale.</b> Two things: the word it is about has
    /// been edited, or the user has moved to a different field. Both are checked here against
    /// the facts they are about — the field's identity, and the text the card was built from.
    /// </para>
    ///
    /// <para><b>What used to be checked, and why it was wrong.</b> The card was invalidated
    /// whenever the monitor's <c>(target, generation, textVersion)</c> triple stopped matching
    /// the one captured when the card opened. Neither of the last two is a statement about the
    /// text. The monitor restamps every republished snapshot with the live text version, and it
    /// republishes whenever its interaction state changes — including on
    /// <c>SetPopupInteractionOpen</c>, which is called to open this very card. It also
    /// increments the version once when it first establishes a baseline for a field, which for
    /// a field the user has only just clicked into happens after the card is already open. Each
    /// of those made a card that nobody had touched disappear on its own, and in a Slate
    /// composer — where clicking about is what the user does — it happened constantly.</para>
    ///
    /// <para>A <see langword="null"/> state is likewise not a dismissal. The monitor publishes
    /// one when it stops tracking a field, which happens while switching between fields and
    /// while the tracked window loses activation; it means "nothing is being watched", not
    /// "the user is finished with this card". Dismissal by the user is handled where the user
    /// actually performs it — see <c>DoubleClickWordObserver.PrimaryButtonPressed</c>.</para>
    /// </remarks>
    /// <param name="targetId">Runtime identifier of the field now being tracked, if any.</param>
    /// <param name="text">That field's current text, if any.</param>
    public bool IsStaleFor(string? targetId, string? text)
    {
        if (string.IsNullOrWhiteSpace(_targetId))
        {
            // Nothing was recorded to compare against; there is no evidence of staleness.
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetId))
        {
            return false;
        }

        if (!string.Equals(_targetId, targetId, StringComparison.Ordinal))
        {
            // A different field is being tracked. The card describes a word the user is no
            // longer in.
            return true;
        }

        // Same field. Only an actual change to its text can have moved the word.
        return _fullText is not null
               && text is not null
               && !string.Equals(_fullText, text, StringComparison.Ordinal);
    }

    /// <summary>True when a screen point in physical pixels lands on this card.</summary>
    /// <remarks>
    /// The point comes from the low-level mouse hook and is therefore in physical pixels,
    /// while <see cref="Window.Left"/> and <see cref="Window.Top"/> are device-independent —
    /// the same mismatch <see cref="PositionForAnchor"/> resolves in the other direction, and
    /// resolved here with the same DPI so the two agree.
    /// </remarks>
    public bool ContainsPhysicalPoint(System.Windows.Point physicalScreenPoint)
    {
        if (!IsVisible) return false;

        var dpi = VisualTreeHelper.GetDpi(this);
        var bounds = new Rect(
            Left * dpi.DpiScaleX,
            Top * dpi.DpiScaleY,
            Math.Max(ActualWidth, Width) * dpi.DpiScaleX,
            Math.Max(ActualHeight, MinHeight) * dpi.DpiScaleY);

        return bounds.Contains(physicalScreenPoint);
    }

    /// <param name="translations">
    /// Cross-language glosses for the word, from the index the dictionary page and the
    /// editor's word panel already use. Empty when none are installed or none are known.
    /// </param>
    /// <inheritdoc cref="ShowLoading"/>
    public void ShowResult(
        LexicalLookupResult result,
        WordRange range,
        string fullText,
        string targetId,
        int generationId,
        long textVersion,
        bool supportsWrite,
        Rect physicalAnchor,
        long requestId,
        IReadOnlyList<string>? translations = null)
    {
        if (requestId != 0 && _requestId != 0 && requestId < _requestId)
        {
            CompatibilityLogger.Technical("lexical-stale-ui-rejected", $"request={requestId}");
            return;
        }

        _result = result;
        _range = range;
        _fullText = fullText;
        _targetId = targetId;
        _generationId = generationId;
        _textVersion = textVersion;
        _supportsWrite = supportsWrite;
        _requestId = requestId;
        _pendingAnchor = physicalAnchor;

        _titleWord.Text = result.Word;
        var pos = PosLabel(result.PartOfSpeech);

        // The card serves both installed packs, so the label has to say which one answered.
        // It was hard-coded to "RU", which meant an English word double-clicked in a chat
        // was presented as a Russian entry — with an English headword, English definitions
        // and Russian abbreviations for its part of speech.
        var lang = LanguageLabel(result.Language, range.Word);
        _subtitle.Text = string.IsNullOrEmpty(pos)
            ? $"{lang} · {result.Lemma}"
            : $"{lang} · {pos} · {result.Lemma}";

        _status.Text = result.StatusMessage ?? "";
        var glosses = translations ?? [];
        PopulateSource(result);
        PopulateMorphology(result);
        PopulateTranslations(glosses);
        PopulateSynonyms(result);
        PopulateDefinitions(result);
        PopulateExamples(result);
        PopulateRole(result);
        SetTabVisibility(
            word: true,
            synonyms: result.Synonyms.Count > 0 || result.Antonyms is { Count: > 0 },
            definitions: result.Definitions.Count > 0,
            examples: result.Examples.Count > 0,
            role: result.Syntax is not null,
            translations: glosses.Count > 0);

        EnsureVisible();
        PositionForAnchor(_pendingAnchor, new Size(Width, Math.Max(MinHeight, 220)));
        ScheduleMeasuredReposition();
    }

    public void ShowError(string message, long requestId)
    {
        if (requestId != 0 && _requestId != 0 && requestId < _requestId) return;
        ClearPanels();
        _status.Text = message;
        _source.Text = "";
        _synonymsPanel.Children.Add(Muted(message));
        SetTabVisibility(word: false, synonyms: true, definitions: false, examples: false, role: false);
        EnsureVisible();
    }

    public new void Hide()
    {
        if (!IsVisible)
        {
            return;
        }

        base.Hide();
        ClosedByUser?.Invoke(this, EventArgs.Empty);
    }

    private void PopulateMorphology(LexicalLookupResult result)
    {
        _morphologyPanel.Children.Clear();
        _morphologyPanel.Children.Add(new TextBlock
        {
            Text = $"Слово: {result.Word}",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush
        });
        _morphologyPanel.Children.Add(new TextBlock
        {
            Text = $"Лемма: {result.Lemma}",
            FontSize = 13,
            Foreground = TextBrush,
            Margin = new Thickness(0, 4, 0, 0)
        });
        if (!string.IsNullOrEmpty(result.SurfaceFormNote))
            _morphologyPanel.Children.Add(Muted(result.SurfaceFormNote));

        var pos = PosLabel(result.PartOfSpeech);
        if (!string.IsNullOrEmpty(pos))
            _morphologyPanel.Children.Add(new TextBlock { Text = $"Часть речи: {pos}", FontSize = 12, Foreground = MutedBrush, Margin = new Thickness(0, 6, 0, 0) });

        if (result.Morphology is { } m)
        {
            foreach (var line in m.ToDisplayList())
                _morphologyPanel.Children.Add(new TextBlock { Text = line, FontSize = 12, Foreground = TextBrush, Margin = new Thickness(0, 2, 0, 0) });
        }
    }

    private void PopulateRole(LexicalLookupResult result)
    {
        _rolePanel.Children.Clear();
        if (result.Syntax is null)
        {
            _rolePanel.Children.Add(Muted("Роль в предложении не определена."));
            return;
        }

        var s = result.Syntax;
        _rolePanel.Children.Add(new TextBlock
        {
            Text = RoleRu(s.Role),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = AccentBrush
        });
        _rolePanel.Children.Add(new TextBlock
        {
            Text = s.Explanation,
            FontSize = 13,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 8)
        });
        if (!string.IsNullOrEmpty(s.HeadWord))
            _rolePanel.Children.Add(Muted($"Связано с: {s.HeadWord}"));

        if (result.Relations is { Count: > 0 })
        {
            _rolePanel.Children.Add(new TextBlock
            {
                Text = "Связи",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                Margin = new Thickness(0, 8, 0, 4)
            });
            foreach (var r in result.Relations.Take(6))
                _rolePanel.Children.Add(Muted($"{r.RelationType}: {r.Value}" + (r.Label is null ? "" : $" ({r.Label})")));
        }
    }

    private static string RoleRu(SyntacticRole role) => role switch
    {
        SyntacticRole.Subject => "Подлежащее",
        SyntacticRole.Predicate => "Сказуемое",
        SyntacticRole.Object => "Дополнение",
        SyntacticRole.Attribute => "Определение",
        SyntacticRole.Adverbial => "Обстоятельство",
        SyntacticRole.PrepositionalObject => "Предложное дополнение",
        SyntacticRole.ConjunctionRole => "Союз",
        SyntacticRole.ParticleRole => "Служебное слово",
        _ => "Роль не определена"
    };

    private void PopulateSynonyms(LexicalLookupResult result)
    {
        _synonymsPanel.Children.Clear();
        if (result.Synonyms.Count == 0 && (result.Antonyms is null || result.Antonyms.Count == 0))
        {
            _synonymsPanel.Children.Add(Muted("Синонимы не найдены в локальном словаре."));
            return;
        }

        foreach (var s in result.Synonyms)
        {
            var row = new Border
            {
                Background = RaisedBackground,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = s.Value, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = TextBrush });
            var meta = string.Join(" · ", new[] { PosLabel(s.PartOfSpeech), s.Label }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrEmpty(meta))
                text.Children.Add(new TextBlock { Text = meta, FontSize = 11, Foreground = MutedBrush, Margin = new Thickness(0, 2, 0, 0) });
            grid.Children.Add(text);
            if (s.CanReplace && _supportsWrite)
            {
                var btn = CreateAccentButton("Заменить");
                var capture = s;
                btn.Click += (_, _) => RequestReplace(capture.Value);
                Grid.SetColumn(btn, 1);
                grid.Children.Add(btn);
            }
            else
            {
                var copy = CreateGhostButton("Копировать", double.NaN, 28);
                var capture = s.Value;
                copy.Click += (_, _) => CopyToClipboard(capture);
                Grid.SetColumn(copy, 1);
                grid.Children.Add(copy);
            }

            row.Child = grid;
            _synonymsPanel.Children.Add(row);
        }

        if (result.Antonyms is { Count: > 0 })
        {
            _synonymsPanel.Children.Add(new TextBlock
            {
                Text = "Антонимы",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                Margin = new Thickness(0, 10, 0, 6)
            });
            foreach (var a in result.Antonyms)
            {
                var row = new Border
                {
                    Background = RaisedBackground,
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    Child = new TextBlock { Text = a.Value, FontSize = 14, Foreground = TextBrush }
                };
                _synonymsPanel.Children.Add(row);
            }
        }
    }

    private void PopulateDefinitions(LexicalLookupResult result)
    {
        _definitionsPanel.Children.Clear();
        if (result.Definitions.Count == 0)
        {
            _definitionsPanel.Children.Add(Muted("Толкование не найдено."));
            return;
        }

        var i = 1;
        foreach (var d in result.Definitions)
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var head = $"{i}. {d.Definition}";
            block.Children.Add(new TextBlock { Text = head, FontSize = 13, Foreground = TextBrush, TextWrapping = TextWrapping.Wrap });
            var tags = new List<string?>();
            if (d.IsHistorical) tags.Add("историч.");
            if (!string.IsNullOrEmpty(d.EraLabel)) tags.Add(d.EraLabel);
            tags.Add(PosLabel(d.PartOfSpeech));
            tags.Add(d.UsageLabel);
            var meta = string.Join(" · ", tags.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrEmpty(meta))
                block.Children.Add(new TextBlock { Text = meta, FontSize = 11, Foreground = MutedBrush, Margin = new Thickness(0, 2, 0, 0) });
            _definitionsPanel.Children.Add(block);
            i++;
        }
    }

    private void PopulateExamples(LexicalLookupResult result)
    {
        _examplesPanel.Children.Clear();
        if (result.Examples.Count == 0)
        {
            _examplesPanel.Children.Add(Muted("Примеры отсутствуют в локальном пакете."));
            return;
        }

        foreach (var e in result.Examples)
        {
            var tb = new TextBlock
            {
                FontSize = 13,
                Foreground = TextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            HighlightWord(tb, e.Text, e.HighlightWord ?? result.Word);
            _examplesPanel.Children.Add(tb);
        }
    }

    private void RequestReplace(string value)
    {
        if (_range is null || _fullText is null || _targetId is null) return;
        ReplaceRequested?.Invoke(this, new LexicalReplaceRequestedEventArgs
        {
            TargetId = _targetId,
            GenerationId = _generationId,
            TextVersion = _textVersion,
            FullText = _fullText,
            Start = _range.Start,
            Length = _range.Length,
            OriginalWord = _range.Word,
            Replacement = value,
            SupportsDirectWrite = _supportsWrite,
            RequestId = _requestId
        });
    }

    private void EnsureVisible()
    {
        if (!IsVisible) Show();
    }

    private void ScheduleMeasuredReposition()
    {
        if (_repositionPending) return;
        _repositionPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            _repositionPending = false;
            if (!IsVisible) return;
            UpdateLayout();
            PositionForAnchor(_pendingAnchor, new Size(ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : MinHeight));
        });
    }

    private void PositionForAnchor(Rect physicalScreenAnchor, Size popupSize)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var anchor = SmartPopupPlacementService.Scale(physicalScreenAnchor, dpi.DpiScaleX, dpi.DpiScaleY);
        var workArea = GetCurrentWorkArea(physicalScreenAnchor, dpi.DpiScaleX, dpi.DpiScaleY);
        var placement = SmartPopupPlacementService.PlaceAnchoredPopup(
            new SmartPlacementContext(workArea, AnchorBounds: anchor, FieldBounds: null),
            popupSize);
        Left = placement.Location.X;
        Top = placement.Location.Y;
    }

    private static Rect GetCurrentWorkArea(Rect physicalAnchor, double dpiX, double dpiY)
    {
        var point = new NativePoint { X = (int)Math.Round(physicalAnchor.Left), Y = (int)Math.Round(physicalAnchor.Top) };
        var monitor = MonitorFromPoint(point, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            var work = new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
            return SmartPopupPlacementService.Scale(work, dpiX, dpiY);
        }

        return SystemParameters.WorkArea;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void ClearPanels()
    {
        _synonymsPanel.Children.Clear();
        _definitionsPanel.Children.Clear();
        _examplesPanel.Children.Clear();
        _morphologyPanel.Children.Clear();
        _rolePanel.Children.Clear();
    }

    private void SetTabVisibility(
        bool word,
        bool synonyms,
        bool definitions,
        bool examples,
        bool role,
        bool translations = false)
    {
        _wordTab.Visibility = word ? Visibility.Visible : Visibility.Collapsed;
        _translationsTab.Visibility = translations ? Visibility.Visible : Visibility.Collapsed;
        _synonymsTab.Visibility = synonyms ? Visibility.Visible : Visibility.Collapsed;
        _definitionsTab.Visibility = definitions ? Visibility.Visible : Visibility.Collapsed;
        _examplesTab.Visibility = examples ? Visibility.Visible : Visibility.Collapsed;
        _roleTab.Visibility = role ? Visibility.Visible : Visibility.Collapsed;

        // The order the tabs are offered in is the order they are considered in, so a card
        // that has nothing else opens on whichever tab does have something.
        var ordered = new[] { _wordTab, _translationsTab, _synonymsTab, _definitionsTab, _examplesTab, _roleTab };
        if (_tabs.SelectedItem is not TabItem selected || selected.Visibility != Visibility.Visible)
        {
            _tabs.SelectedItem = Array.Find(ordered, tab => tab.Visibility == Visibility.Visible) ?? _wordTab;
            _wordTab.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Fills the translation tab, which is the whole point of the card for a foreign word.
    /// </summary>
    /// <remarks>
    /// The values come from the same <c>TranslationIndex</c> the dictionary page and the
    /// editor's word panel read — the card composes the existing services rather than
    /// acquiring a dictionary of its own. Each translation is copyable, because the reason
    /// someone double-clicks an English word mid-sentence is usually to put its Russian in
    /// what they are writing.
    /// </remarks>
    private void PopulateTranslations(IReadOnlyList<string> translations)
    {
        _translationsPanel.Children.Clear();

        if (translations.Count == 0)
        {
            _translationsPanel.Children.Add(Muted("Перевода нет в установленных словарях."));
            return;
        }

        foreach (var value in translations.Take(12))
        {
            var row = new Border
            {
                Background = RaisedBackground,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 14,
                Foreground = TextBrush,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });

            var copy = CreateGhostButton("Копировать", double.NaN, 28);
            var captured = value;
            copy.Click += (_, _) => CopyToClipboard(captured);
            Grid.SetColumn(copy, 1);
            grid.Children.Add(copy);

            row.Child = grid;
            _translationsPanel.Children.Add(row);
        }
    }

    private void PopulateSource(LexicalLookupResult result)
    {
        // Pack identifiers, repository URLs and licence strings are legal/build
        // metadata, not dictionary content. Attribution remains in
        // ThirdParty/THIRD_PARTY_NOTICES.txt; the everyday lookup popup stays lexical.
        _source.Inlines.Clear();
        _source.Visibility = Visibility.Collapsed;
    }

    private static TabItem CreateTab(string header, StackPanel content)
    {
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 280,
            Padding = new Thickness(0, 8, 0, 0)
        };
        return new TabItem
        {
            Header = header,
            Content = scroll,
            Style = ThemeResource.Style("WlTabItem")
        };
    }

    /// <summary>
    /// Puts a word on the clipboard, and does nothing visible when the clipboard refuses.
    /// </summary>
    /// <remarks>
    /// <c>Clipboard.SetText</c> throws when another process is holding the clipboard open,
    /// which on a desktop with a clipboard manager running is a normal transient condition
    /// rather than a fault. Failing the copy silently is the right outcome: the alternative
    /// is an error dialog over someone else's window because a synonym did not copy.
    /// </remarks>
    private static void CopyToClipboard(string value)
    {
        try
        {
            System.Windows.Clipboard.SetText(value);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
    }

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = MutedBrush,
        TextWrapping = TextWrapping.Wrap
    };

    private static void HighlightWord(TextBlock block, string text, string word)
    {
        if (string.IsNullOrEmpty(word) || string.IsNullOrEmpty(text))
        {
            block.Text = text;
            return;
        }

        var idx = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            block.Text = text;
            return;
        }

        if (idx > 0) block.Inlines.Add(new System.Windows.Documents.Run(text[..idx]) { Foreground = TextBrush });
        block.Inlines.Add(new System.Windows.Documents.Run(text.Substring(idx, word.Length))
        {
            Foreground = AccentBrush,
            FontWeight = FontWeights.SemiBold
        });
        if (idx + word.Length < text.Length)
            block.Inlines.Add(new System.Windows.Documents.Run(text[(idx + word.Length)..]) { Foreground = TextBrush });
    }

    /// <summary>
    /// Which pack answered, or which language the word is in when nothing answered.
    /// </summary>
    /// <remarks>
    /// A miss carries <see cref="LexicalLanguage.Unknown"/> because no entry was found to
    /// carry a language, and labelling a plainly English word "—" is less useful than saying
    /// what it evidently is. The script of the word itself is the fallback.
    /// </remarks>
    private static string LanguageLabel(LexicalLanguage language, string word)
    {
        if (language == LexicalLanguage.Unknown)
        {
            language = new LexicalLanguageDetector().DetectWord(word);
        }

        return language switch
        {
            LexicalLanguage.English => "EN",
            LexicalLanguage.Russian => "RU",
            _ => "—"
        };
    }

    private static string PosLabel(LexicalPartOfSpeech pos) => pos switch
    {
        LexicalPartOfSpeech.Noun => "сущ.",
        LexicalPartOfSpeech.Verb => "гл.",
        LexicalPartOfSpeech.Adjective => "прил.",
        LexicalPartOfSpeech.Adverb => "нар.",
        LexicalPartOfSpeech.Pronoun => "мест.",
        LexicalPartOfSpeech.Preposition => "предл.",
        LexicalPartOfSpeech.Conjunction => "союз",
        LexicalPartOfSpeech.Particle => "част.",
        LexicalPartOfSpeech.Interjection => "межд.",
        LexicalPartOfSpeech.Numeral => "числ.",
        _ => ""
    };

    private static Button CreateAccentButton(string text)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(8, 0, 0, 0),
            Background = AccentBrush,
            Foreground = OnAccentBrush,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FontSize = 12,
            FontWeight = FontWeights.Medium
        };
        button.Template = RoundedButtonTemplate(new CornerRadius(5), ThemeResource.Brush("WlBrandHover", AccentBrush));
        return button;
    }

    private static Button CreateGhostButton(string text, double width, double height)
    {
        var button = new Button
        {
            Content = text,
            Width = width,
            Height = height,
            Background = Brushes.Transparent,
            Foreground = MutedBrush,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FontSize = 14
        };
        button.Template = RoundedButtonTemplate(new CornerRadius(5), HoverBrush);
        return button;
    }

    /// <summary>
    /// Minimal rounded button template. This window is built without XAML, so the shape
    /// and hover surface are reproduced here from the same tokens Themes/Buttons.xaml uses.
    /// </summary>
    private static ControlTemplate RoundedButtonTemplate(CornerRadius radius, MediaBrush hoverBrush)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "border";
        border.SetValue(Border.CornerRadiusProperty, radius);
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, hoverBrush, "border"));
        template.Triggers.Add(hover);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "border"));
        template.Triggers.Add(disabled);

        return template;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}

public sealed class LexicalReplaceRequestedEventArgs : EventArgs
{
    public required string TargetId { get; init; }
    public required int GenerationId { get; init; }
    public required long TextVersion { get; init; }
    public required string FullText { get; init; }
    public required int Start { get; init; }
    public required int Length { get; init; }
    public required string OriginalWord { get; init; }
    public required string Replacement { get; init; }
    public required bool SupportsDirectWrite { get; init; }
    public long RequestId { get; init; }
}
