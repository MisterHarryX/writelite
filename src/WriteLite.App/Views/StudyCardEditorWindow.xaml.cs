using System.Windows;
using System.Windows.Input;
using WriteLite.Resources;
using WriteLite.Services.Reading;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace WriteLite.Views;

/// <summary>
/// Writes one study card, by hand or with a draft from the local model.
/// </summary>
/// <remarks>
/// Manual first. The two fields are editable and complete on their own, and the AI
/// button only appears when a model is actually bound — it fills the fields in and
/// the reader keeps or rewrites what it produced. Nothing is ever saved without being
/// seen, which is the same rule the editor's rewrite preview follows.
/// </remarks>
public partial class StudyCardEditorWindow : Window
{
    private readonly StudyCardDraftService? _drafts;
    private readonly string _passage;
    private CancellationTokenSource? _draftCancellation;

    public StudyCardEditorWindow(string passage, StudyCardDraftService? drafts)
    {
        InitializeComponent();

        _passage = passage ?? string.Empty;
        _drafts = drafts;

        QuoteText.Text = _passage;
        QuoteBlock.Visibility = string.IsNullOrWhiteSpace(_passage) ? Visibility.Collapsed : Visibility.Visible;

        // The back of a card made from a passage starts as the passage: the most
        // common card is "what is this" on the front and the quote on the back, and
        // pre-filling it means the manual path is already half done.
        BackBox.Text = _passage;

        if (_drafts?.IsAvailable == true)
        {
            AiButton.Visibility = Visibility.Visible;
            LocalNote.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => FrontBox.Focus();

        // Closing the dialog cancels the request rather than leaving a task writing
        // into a window that is gone.
        Closed += (_, _) =>
        {
            _draftCancellation?.Cancel();
            _draftCancellation?.Dispose();
            _draftCancellation = null;
        };
    }

    public string Front { get; private set; } = string.Empty;

    public string Back { get; private set; } = string.Empty;

    /// <summary>True when the local model drafted the text that was saved.</summary>
    public bool UsedAi { get; private set; }

    /// <summary>Fills both sides, for editing a card that already exists.</summary>
    public void Prefill(string front, string back)
    {
        FrontBox.Text = front;
        BackBox.Text = back;
    }

    /// <summary>Asks the model for a draft as soon as the dialog opens.</summary>
    public void StartWithAiDraft()
    {
        if (_drafts?.IsAvailable == true)
        {
            Loaded += (_, _) => _ = DraftAsync();
        }
    }

    private void Ai_Click(object sender, RoutedEventArgs e) => _ = DraftAsync();

    private void CancelDraft_Click(object sender, RoutedEventArgs e) => _draftCancellation?.Cancel();

    /// <summary>
    /// Asks the local model for a draft, without ever making the reader wait for it.
    /// </summary>
    /// <remarks>
    /// Both fields stay editable and both buttons stay live throughout: the model is an
    /// offer, not a modal step, and a card written by hand while the model was thinking
    /// is a perfectly good card. Nothing here blocks the dispatcher — the await is a
    /// real await and the only synchronous work is setting two strings when it returns.
    ///
    /// The reader's own text is never overwritten. Whatever has been typed into a field
    /// wins over the draft, because losing someone's sentence to a suggestion they did
    /// not ask for is worse than not filling the field in.
    /// </remarks>
    private async Task DraftAsync()
    {
        if (_drafts is null || string.IsNullOrWhiteSpace(_passage))
        {
            return;
        }

        _draftCancellation?.Cancel();
        _draftCancellation?.Dispose();
        _draftCancellation = new CancellationTokenSource();
        var token = _draftCancellation.Token;

        SetDrafting(true);

        // Remembered before the wait: if the reader typed a question while the model was
        // working, that question is theirs and the draft must not take it.
        var frontWasEmpty = string.IsNullOrWhiteSpace(FrontBox.Text);
        var backWasPassage = string.Equals(BackBox.Text?.Trim(), _passage.Trim(), StringComparison.Ordinal);

        var result = await _drafts.DraftAsync(_passage, token);

        // The window may have gone while the model was thinking.
        if (!IsLoaded || token.IsCancellationRequested)
        {
            SetDrafting(false);
            return;
        }

        SetDrafting(false);

        if (!result.IsSuccess)
        {
            Controls.Type.SetTracked(StatusText, StatusFor(result));
            return;
        }

        var draft = result.Draft!.Value;

        if (frontWasEmpty)
        {
            FrontBox.Text = draft.Front;
        }

        // The back starts life as the passage, so replacing it is filling in a
        // placeholder rather than overwriting anything the reader wrote.
        if (backWasPassage && !string.IsNullOrWhiteSpace(draft.Back))
        {
            BackBox.Text = draft.Back;
        }

        UsedAi = true;
        Controls.Type.SetTracked(StatusText, Strings.Study_DraftStatus);
    }

    private void SetDrafting(bool drafting)
    {
        AiButton.IsEnabled = !drafting;
        CancelDraftButton.Visibility = drafting ? Visibility.Visible : Visibility.Collapsed;

        if (drafting)
        {
            Controls.Type.SetTracked(StatusText, Strings.Study_DraftingStatus);
        }
    }

    /// <summary>What to tell the reader when there is no draft. Never silence.</summary>
    private static string StatusFor(StudyCardDraftResult result) => result.Status switch
    {
        StudyCardDraftStatus.Cancelled => Strings.Study_StatusCancelled,
        StudyCardDraftStatus.NotConfigured => Strings.Study_StatusNotConfigured,
        StudyCardDraftStatus.ModelUnavailable => Strings.Study_StatusUnavailable,
        StudyCardDraftStatus.Unusable => Strings.Study_StatusUnusable,
        _ => Strings.Study_StatusFailed
    };

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Front = (FrontBox.Text ?? string.Empty).Trim();
        Back = (BackBox.Text ?? string.Empty).Trim();

        if (Front.Length == 0 && Back.Length == 0)
        {
            Controls.Type.SetTracked(StatusText, Strings.Study_StatusFillRequired);
            FrontBox.Focus();
            return;
        }

        // A card with only a back is still useful — it is a saved quote — so the
        // front falls back to the first words of it rather than blocking the save.
        if (Front.Length == 0)
        {
            Front = Back.Length <= 60 ? Back : Back[..60].TrimEnd() + "…";
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
