using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Automation;
using WriteLite.Models;
using WriteLite.Resources;
using WriteLite.Services;
using WriteLite.Services.Rules;
using WriteLite.Services.Ai;
using WriteLite.Services.Audio;
using WriteLite.Services.Diagnostics;
using WriteLite.Services.Grammar;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Lexical;
using WriteLite.Services.Notes;
using WriteLite.Services.Reading;
using WriteLite.Language.Core;
using WriteLite.Language.Packs;
using WriteLite.Services.Settings;
using WriteLite.Services.Spelling;
using WriteLite.Views;
using MessageBox = System.Windows.MessageBox;

namespace WriteLite;

public partial class App : System.Windows.Application
{
    private BubbleWindow? _bubble;

    private SuggestionsWindow? _suggestions;


    private LexicalPopupWindow? _lexicalPopup;

    private InlineErrorOverlayController? _inlineOverlay;

    private OfflineLexicalKnowledgeService? _lexicalKnowledge;

    private TranslationIndex? _translations;

    private ILexicalReplacementService? _lexicalReplacement;

    private DoubleClickWordObserver? _doubleClickObserver;

    private CancellationTokenSource? _lexicalLookupCts;

    private void OpenSuggestions(object? sender, EventArgs e)
    {
        var snapshot = _corrections?.LatestSnapshot;
        if (snapshot is null || _suggestions is null || !snapshot.ShowMainUi) return;

        _bubble?.ClearUserHide();
        _monitor?.SetSuggestionsWindowOpen(true);
        _monitor?.SetPopupInteractionOpen(true);
        _suggestions.ShowSnapshot(snapshot);
    }


    private void OnInlineIssueClicked(object? sender, InlineIssueClickedEventArgs args)
    {
        var snapshot = _corrections?.LatestSnapshot;
        if (snapshot is null || !snapshot.Target.IsEditable || !snapshot.ShowMainUi || !snapshot.Issues.Contains(args.Issue)
            || !TextCorrectionService.IsRangeValid(snapshot.Text, args.Issue.Start, args.Issue.Length)
            || (args.Issue.Length > 0 && !string.Equals(snapshot.Text.Substring(args.Issue.Start, args.Issue.Length), args.Issue.Original, StringComparison.Ordinal)))
        {
            CompatibilityLogger.Technical("inline-stale-result-rejected", "reason=issue-not-current");
            return;
        }

        _corrections?.OpenCorrectionCard(snapshot, args.Issue, args.Anchor);
    }


    /// <summary>
    /// Closes the dictionary card when the user clicks anywhere that is not the card.
    /// </summary>
    /// <remarks>
    /// The dismissal the product promises — "click somewhere else and it goes away" — stated
    /// as the thing it actually is, rather than derived from field-monitor state. Both clicks
    /// of a double-click arrive here: the first closes whatever card is open, and the second
    /// is what opens the next one, so double-clicking a second word replaces the card instead
    /// of leaving the old one over the new word.
    /// </remarks>
    private void OnPrimaryButtonPressed(object? sender, System.Windows.Point physicalScreenPoint)
    {
        if (_lexicalPopup is not { IsVisible: true } popup) return;
        if (popup.ContainsPhysicalPoint(physicalScreenPoint)) return;

        _lexicalLookupCts?.Cancel();
        popup.Hide();
        CompatibilityLogger.Technical("lexical-popup-dismissed", "reason=click-outside");
    }


    private void OnWordDoubleClicked(object? sender, WordDoubleClickedEventArgs args)
    {
        var snapshot = _corrections?.LatestSnapshot;
        var lexicalCardAvailable = _settings.LexicalCardEnabled
                                   && _lexicalKnowledge is not null
                                   && _lexicalPopup is not null;
        // Double-click is reserved for the dictionary card. A single click on
        // an underline opens the correction card. Only fall back to correction
        // here when the lexical card is explicitly unavailable.
        if (!lexicalCardAvailable
            && snapshot is not null
            && snapshot.Target.IsEditable
            && snapshot.ShowMainUi
            && snapshot.Target.Identity.RuntimeId == args.TargetId
            && snapshot.GenerationId == args.GenerationId
            && snapshot.TextVersion == args.TextVersion)
        {
            var issue = CorrectionInteractionResolver.FindIssueAtRange(
                snapshot.Issues, args.Range.Start, args.Range.Length);
            if (issue is not null
                && TextCorrectionService.IsRangeValid(snapshot.Text, issue.Start, issue.Length)
                && (issue.Length == 0 || string.Equals(
                    snapshot.Text.Substring(issue.Start, issue.Length), issue.Original, StringComparison.Ordinal)))
            {
                _lexicalLookupCts?.Cancel();
                _lexicalPopup?.Hide();
                CompatibilityLogger.Technical("double-click-correction-priority",
                    $"rule={issue.RuleId} generation={snapshot.GenerationId}");
                OnInlineIssueClicked(this, new InlineIssueClickedEventArgs(issue, args.Anchor));
                return;
            }
        }

        if (!lexicalCardAvailable || _lexicalKnowledge is null || _lexicalPopup is null)
            return;

        _monitor?.SetPopupInteractionOpen(true);
        _lexicalPopup.ShowLoading(
            args.Range,
            args.Anchor,
            args.RequestId,
            args.TargetId,
            args.GenerationId,
            args.TextVersion,
            args.FullText);

        _lexicalLookupCts?.Cancel();
        _lexicalLookupCts?.Dispose();
        _lexicalLookupCts = new CancellationTokenSource();
        var token = _lexicalLookupCts.Token;
        var requestId = args.RequestId;

        _ = Task.Run(async () =>
        {
            try
            {
                // The word's own script decides which pack answers. Russian was passed here
                // unconditionally, which the service happened to survive because it re-detects
                // the language itself — but it meant the request said one thing and the answer
                // another, and anything downstream that trusted the request was wrong about
                // every English word.
                var request = new LexicalLookupRequest(
                    args.Range.Word,
                    args.Range.Sentence,
                    args.FullText,
                    args.Range.Start,
                    args.Range.Length,
                    args.Range.Language,
                    requestId,
                    args.GenerationId,
                    args.TextVersion,
                    args.TargetId);

                var result = await _lexicalKnowledge.LookupAsync(request, token).ConfigureAwait(true);
                if (token.IsCancellationRequested) return;

                // Read from the same index the dictionary page and the editor's word panel
                // use, on this worker rather than the dispatcher: it is a SQLite query, and
                // this path runs while the user is typing in someone else's window.
                var translations = ResolveCardTranslations(result, args.Range);
                if (token.IsCancellationRequested) return;
                if (_lexicalPopup.CurrentRequestId > requestId)
                {
                    CompatibilityLogger.Technical("lexical-stale-result-rejected", $"request={requestId}");
                    return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    _lexicalPopup.ShowResult(
                        result,
                        args.Range,
                        args.FullText,
                        args.TargetId,
                        args.GenerationId,
                        args.TextVersion,
                        args.SupportsDirectWrite,
                        args.Anchor,
                        requestId,
                        translations);
                });
            }
            catch (OperationCanceledException)
            {
                // newer lookup owns the UI
            }
            catch (Exception ex)
            {
                CompatibilityLogger.AccessError("lexical-lookup", null, ex);
                await Dispatcher.InvokeAsync(() =>
                    _lexicalPopup.ShowError(Strings.App_PopupLoadFailed, requestId));
            }
        }, token);
    }


    /// <summary>
    /// Takes the user from the card over another application to the full dictionary article.
    /// </summary>
    /// <remarks>
    /// Routed through <c>MainWindow.OpenWordInDictionary</c>, which is the one destination the
    /// editor, the reader and the notes board already navigate to. The card is deliberately
    /// dismissed on the way: it is a summary of the article now being opened, and leaving it
    /// floating over the main window would be two views of one word on screen at once.
    /// </remarks>
    private void OnLexicalFullArticleRequested(object? sender, string word)
    {
        _lexicalLookupCts?.Cancel();
        _lexicalPopup?.Hide();

        OpenMainWindow();
        _mainWindow?.OpenWordInDictionary(word);
        CompatibilityLogger.Technical("lexical-card-open-full-article", "ok=1");
    }


    /// <summary>
    /// The cross-language glosses for a card's word, or nothing when none are installed.
    /// </summary>
    /// <remarks>
    /// The lemma is tried before the surface form because the index is keyed by lemma, and
    /// the language the lookup actually resolved to is preferred over the word's script —
    /// they agree except where the pack knows better.
    /// </remarks>
    private IReadOnlyList<string> ResolveCardTranslations(LexicalLookupResult result, WordRange range)
    {
        if (_translations is null) return [];

        var language = result.Language == LexicalLanguage.Unknown ? range.Language : result.Language;
        if (language == LexicalLanguage.Unknown) return [];

        try
        {
            var values = _translations.Translate(result.Lemma, language);
            if (values.Count == 0 && !string.Equals(result.Lemma, range.Word, StringComparison.OrdinalIgnoreCase))
            {
                values = _translations.Translate(range.Word, language);
            }

            return values;
        }
        catch (Exception exception)
        {
            // A damaged translation file costs the card its translation tab, not the card.
            CompatibilityLogger.Technical("lexical-translations-failed", $"type={exception.GetType().Name}");
            return [];
        }
    }


    private async void OnLexicalReplaceRequested(object? sender, LexicalReplaceRequestedEventArgs args)
    {
        if (_lexicalReplacement is null) return;

        var snapshot = _corrections?.LatestSnapshot;
        if (snapshot is null)
        {
            CompatibilityLogger.Technical("lexical-replace-rejected", "reason=no-snapshot");
            return;
        }

        // Prefer live target identity from the active snapshot.
        if (!LexicalRequestValidator.IsCurrent(
                snapshot.Target.Identity.RuntimeId,
                snapshot.GenerationId,
                snapshot.TextVersion,
                args.TargetId,
                args.GenerationId,
                args.TextVersion))
        {
            CompatibilityLogger.Technical("lexical-replace-rejected", "reason=stale-target-generation-or-text");
            return;
        }

        var read = await snapshot.Target.TryReadTextAsync();
        var liveText = read.Succeeded ? read.Text : snapshot.Text;
        var supportsWrite = snapshot.Target.SupportsDirectWrite;

        var replaceRequest = new LexicalReplacementRequest(
            args.TargetId,
            args.GenerationId,
            args.TextVersion,
            liveText,
            args.Start,
            args.Length,
            args.OriginalWord,
            args.Replacement,
            supportsWrite,
            IsPassword: false,
            IsReadOnly: !supportsWrite);

        var outcome = _lexicalReplacement.TryReplace(replaceRequest);
        if (!outcome.Success || outcome.NewText is null)
        {
            CompatibilityLogger.Technical("lexical-replace-rejected", $"reason={outcome.FailureReason}");
            if (outcome.OfferCopyOnly)
            {
                try { System.Windows.Clipboard.SetText(args.Replacement); } catch { /* ignore */ }
                MessageBox.Show(
                    Strings.App_ReplaceFailedCopied,
                    "WriteLite",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return;
        }

        if (_corrections is { } coordinator)
        {
            await coordinator.ReplaceTargetTextAsync(snapshot, liveText => CanonicalCorrection.TryBind(
                    liveText, args.Start, args.Length, args.OriginalWord, args.Replacement, out var correction)
                        == CorrectionBindingStatus.Bound && correction is not null
                ? correction
                : null);
        }

        _lexicalPopup?.Hide();
    }


}
