using WriteLite.Language.Core;
using WriteLite.Models;
using WriteLite.Services.Writing;

namespace WriteLite.Services;

public enum CorrectionApplicationStatus
{
    CorrectionApplied,
    CorrectionStale,
    TargetUnavailable,
    RangeUnavailable,
    OriginalMismatch,
    ReadOnly,
    UnsupportedWritePattern,
    ProtectedTokenConflict,
    AmbiguousRelocation,
    VerificationFailed,
    Cancelled,
    Failed,
    Blocked
}

public sealed record CorrectionApplicationOutcome(
    CorrectionApplicationStatus Status,
    bool Succeeded,
    string UserMessage,
    int AppliedStart = -1,
    int AppliedLength = 0,
    string? AppliedReplacement = null,
    string? WritePath = null,
    bool OfferCopy = false,
    bool OfferRefresh = false)
{
    public static CorrectionApplicationOutcome FromStatus(CorrectionApplicationStatus status, string? message = null)
    {
        var (text, copy, refresh) = status switch
        {
            CorrectionApplicationStatus.CorrectionApplied => ("Исправление применено.", false, false),
            CorrectionApplicationStatus.CorrectionStale => ("Текст изменился. Обновите предложение.", false, true),
            CorrectionApplicationStatus.TargetUnavailable => ("Поле ввода временно недоступно.", false, true),
            CorrectionApplicationStatus.RangeUnavailable => ("Не удалось найти фрагмент для правки.", true, true),
            CorrectionApplicationStatus.OriginalMismatch => ("Текст изменился. Обновите предложение.", false, true),
            CorrectionApplicationStatus.ReadOnly => ("Поле только для чтения. Исправленный фрагмент можно скопировать.", true, false),
            CorrectionApplicationStatus.UnsupportedWritePattern => ("WriteLite не удалось изменить текст в этом поле. Исправление скопировано.", true, true),
            CorrectionApplicationStatus.ProtectedTokenConflict => ("Правка затронула защищённый фрагмент (код, URL или число).", true, false),
            CorrectionApplicationStatus.AmbiguousRelocation => ("Найдено несколько совпадений. Обновите предложение.", false, true),
            CorrectionApplicationStatus.VerificationFailed => ("Поле не подтвердило применённую правку. Обновите текст перед следующей попыткой.", false, true),
            CorrectionApplicationStatus.Cancelled => ("Операция отменена.", false, false),
            CorrectionApplicationStatus.Blocked => ("Предыдущая правка ещё выполняется.", false, false),
            _ => ("Не удалось применить исправление.", true, true)
        };
        return new CorrectionApplicationOutcome(
            status,
            Succeeded: status == CorrectionApplicationStatus.CorrectionApplied,
            UserMessage: message ?? text,
            OfferCopy: copy,
            OfferRefresh: refresh);
    }
}

public interface ICorrectionApplicationService
{
    Task<CorrectionApplicationOutcome> ApplyAsync(
        TextIssue issue,
        TextSnapshot snapshot,
        TextFieldMonitor monitor,
        AsyncOperationGate gate,
        CancellationToken cancellationToken = default);

    Task<CorrectionApplicationOutcome> ApplyAllSafeAsync(
        TextSnapshot snapshot,
        TextFieldMonitor monitor,
        AsyncOperationGate gate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Unified apply path for panel, popup, inline and punctuation corrections.
/// Never silently fails: every outcome has a user-visible status.
/// </summary>
public sealed class CorrectionApplicationService : ICorrectionApplicationService
{
    /// <summary>
    /// How long the background re-read waits for the target application to settle.
    /// </summary>
    /// <remarks>
    /// No longer on any path the user waits on — see <see cref="ScheduleBackgroundRefresh"/> —
    /// so it is a property of the target application's behaviour rather than a latency budget.
    /// </remarks>
    public static readonly TimeSpan PostWriteSettleDelay = TimeSpan.FromMilliseconds(350);

    private readonly UiaCircuitBreaker _circuitBreaker;
    private readonly Func<TextSnapshot, TextFieldMonitor, CorrectionApplicationStatus> _snapshotValidator;

    public CorrectionApplicationService(
        UiaCircuitBreaker? circuitBreaker = null,
        Func<TextSnapshot, TextFieldMonitor, CorrectionApplicationStatus>? snapshotValidator = null)
    {
        _circuitBreaker = circuitBreaker ?? AutomationTextTarget.CircuitBreaker;
        AutomationTextTarget.ConfigureCircuitBreaker(_circuitBreaker);
        _snapshotValidator = snapshotValidator ?? ValidateCurrentSnapshot;
    }

    /// <summary>
    /// Whether the monitor's view of the world still agrees with the card the user pressed.
    /// </summary>
    /// <remarks>
    /// <para>A cheap pre-check, not the authority. The authority is the live read a few
    /// steps later, which compares the control's actual text against the snapshot; this
    /// exists to reject an obviously stale card before paying for a cross-process read.</para>
    ///
    /// <para><b>Why "the monitor has no target" is not stale.</b> The monitor follows
    /// keyboard focus and drops its target the moment focus leaves an editable field —
    /// which happens routinely while a card is open, including because of WriteLite's own
    /// popup. Treating that as staleness refused corrections that were perfectly valid and
    /// reported it as «Текст изменился», which is simply untrue: nothing had changed. With
    /// no current target there is nothing to compare against, so the decision is deferred to
    /// the live read.</para>
    ///
    /// <para>A <em>different</em> target is still stale — the user moved to another field,
    /// and the card no longer describes what is in front of them.</para>
    /// </remarks>
    public static CorrectionApplicationStatus ValidateSnapshotState(
        string snapshotTargetId,
        int snapshotGeneration,
        long snapshotTextVersion,
        string snapshotText,
        string? currentTargetId,
        int currentGeneration,
        long currentTextVersion,
        string? currentText)
    {
        if (string.IsNullOrEmpty(currentTargetId))
        {
            return CorrectionApplicationStatus.CorrectionApplied;
        }

        return !string.Equals(snapshotTargetId, currentTargetId, StringComparison.Ordinal)
               || snapshotGeneration != currentGeneration
               || snapshotTextVersion != currentTextVersion
               || !string.Equals(snapshotText, currentText, StringComparison.Ordinal)
            ? CorrectionApplicationStatus.CorrectionStale
            : CorrectionApplicationStatus.CorrectionApplied;
    }

    private static CorrectionApplicationStatus ValidateCurrentSnapshot(
        TextSnapshot snapshot,
        TextFieldMonitor monitor)
    {
        var current = monitor.GetTargetState();
        return ValidateSnapshotState(
            snapshot.Target.Identity.RuntimeId,
            snapshot.GenerationId,
            snapshot.TextVersion,
            snapshot.Text,
            current.TargetId,
            current.GenerationId,
            current.TextVersion,
            current.LastText);
    }

    public async Task<CorrectionApplicationOutcome> ApplyAsync(
        TextIssue issue,
        TextSnapshot snapshot,
        TextFieldMonitor monitor,
        AsyncOperationGate gate,
        CancellationToken cancellationToken = default)
    {
        if (!gate.TryEnter())
        {
            CompatibilityLogger.State("correction-blocked");
            return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.Blocked);
        }

        monitor.BeginApplyingCorrection();
        CompatibilityLogger.State("correction-started");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshotStatus = _snapshotValidator(snapshot, monitor);
            if (snapshotStatus != CorrectionApplicationStatus.CorrectionApplied)
            {
                return CorrectionApplicationOutcome.FromStatus(snapshotStatus);
            }

            var targetId = snapshot.Target.Identity.RuntimeId;
            if (_circuitBreaker.IsOpen(targetId))
            {
                return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.TargetUnavailable);
            }

            var verdict = await snapshot.Target
                .TryEvaluateWriteAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (StatusFor(verdict) is { } refusal)
            {
                return CorrectionApplicationOutcome.FromStatus(refusal);
            }

            if (string.IsNullOrEmpty(issue.Replacement))
            {
                return CorrectionApplicationOutcome.FromStatus(
                    CorrectionApplicationStatus.Failed,
                    "Нет текста замены для этой рекомендации.");
            }

            if (CorrectionCandidateValidityPolicy.IsIdenticalCorrection(issue.Original, issue.Replacement)
                || CorrectionCandidateValidityPolicy.IsMeaninglessSpellingCandidate(issue.Original, issue.Replacement))
            {
                return CorrectionApplicationOutcome.FromStatus(
                    CorrectionApplicationStatus.Failed,
                    "Предложенный вариант совпадает с исходным текстом и не будет применён.");
            }

            var read = await snapshot.Target
                .TryReadTextAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!read.Succeeded)
            {
                _circuitBreaker.Trip(targetId);
                return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.TargetUnavailable);
            }

            var live = read.Text;
            if (!string.Equals(live, snapshot.Text, StringComparison.Ordinal))
            {
                return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.CorrectionStale);
            }

            var binding = CanonicalCorrection.TryBind(
                live, issue.Start, issue.Length, issue.Original, issue.Replacement, out var correction);
            if (binding != CorrectionBindingStatus.Bound || correction is null)
            {
                CompatibilityLogger.Technical("correction-apply-failed", $"binding={binding}");
                return CorrectionApplicationOutcome.FromStatus(StatusFor(binding));
            }

            if (ProtectedTextSpans.Overlaps(
                    correction.Start,
                    Math.Max(correction.Length, correction.Replacement.Length),
                    ProtectedTextSpans.Find(live)))
            {
                // Allow pure punctuation insert at end of non-protected letter.
                if (!(correction.IsInsertion && IsSimplePunctuationInsert(correction.Replacement)))
                {
                    return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.ProtectedTokenConflict);
                }
            }

            snapshotStatus = _snapshotValidator(snapshot, monitor);
            if (snapshotStatus != CorrectionApplicationStatus.CorrectionApplied)
            {
                return CorrectionApplicationOutcome.FromStatus(snapshotStatus);
            }

            var write = await snapshot.Target
                .TryApplyCorrectionAsync(correction, live, cancellationToken)
                .ConfigureAwait(false);
            if (!write.Succeeded)
            {
                CompatibilityLogger.Technical(
                    "correction-verify-mismatch",
                    $"strategy={write.StrategyName} detail={write.Detail}");
                return CorrectionApplicationOutcome.FromStatus(
                    write.Detail == "no-applicable-strategy"
                        ? CorrectionApplicationStatus.UnsupportedWritePattern
                        : CorrectionApplicationStatus.VerificationFailed);
            }

            var start = correction.Start;
            var length = correction.Length;
            var replacement = correction.Replacement;
            var writePath = write.StrategyName;
            CompatibilityLogger.State("correction-completed");
            CompatibilityLogger.Technical("correction-applied", $"path={writePath} start={start} len={length}");

            // §4/§16: the write is verified, so the correction is done and the caller may
            // return control. The re-read and the analysis that follows it are a background
            // correctness pass, and awaiting them here is what made a click that had already
            // succeeded keep the popup up for the better part of a second — 350 ms of settle
            // plus 450 ms of suppression plus a full re-read and a whole fast analysis, all
            // after the target application had already shown the corrected text.
            ScheduleBackgroundRefresh(monitor);

            return new CorrectionApplicationOutcome(
                CorrectionApplicationStatus.CorrectionApplied,
                Succeeded: true,
                UserMessage: "Исправление применено.",
                AppliedStart: start,
                AppliedLength: length,
                AppliedReplacement: replacement,
                WritePath: writePath);
        }
        catch (OperationCanceledException)
        {
            return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.Cancelled);
        }
        catch (Exception ex)
        {
            CompatibilityLogger.AccessError("correction-apply", snapshot.Target.ProcessId, ex);
            return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.Failed, ex.Message);
        }
        finally
        {
            monitor.EndApplyingCorrection();
            gate.Exit();
        }
    }

    public async Task<CorrectionApplicationOutcome> ApplyAllSafeAsync(
        TextSnapshot snapshot,
        TextFieldMonitor monitor,
        AsyncOperationGate gate,
        CancellationToken cancellationToken = default)
    {
        if (!gate.TryEnter())
        {
            return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.Blocked);
        }

        monitor.BeginApplyingCorrection();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshotStatus = _snapshotValidator(snapshot, monitor);
            if (snapshotStatus != CorrectionApplicationStatus.CorrectionApplied)
            {
                return CorrectionApplicationOutcome.FromStatus(snapshotStatus);
            }

            var verdict = await snapshot.Target
                .TryEvaluateWriteAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (StatusFor(verdict) is { } refusal)
            {
                return CorrectionApplicationOutcome.FromStatus(refusal);
            }

            var read = await snapshot.Target
                .TryReadTextAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!read.Succeeded)
            {
                return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.TargetUnavailable);
            }

            if (!string.Equals(read.Text, snapshot.Text, StringComparison.Ordinal))
            {
                return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.CorrectionStale);
            }

            var composed = TextCorrectionService.ComposeApplyAll(
                read.Text, snapshot.Issues, snapshot.Target.SupportsDirectWrite);
            if (!composed.CanApply)
            {
                return CorrectionApplicationOutcome.FromStatus(
                    CorrectionApplicationStatus.Failed,
                    "Нет безопасных автоматических исправлений.");
            }

            snapshotStatus = _snapshotValidator(snapshot, monitor);
            if (snapshotStatus != CorrectionApplicationStatus.CorrectionApplied)
            {
                return CorrectionApplicationOutcome.FromStatus(snapshotStatus);
            }

            // Apply-all is one edit spanning from the first change to the last, expressed
            // as a canonical correction so that it goes through the same verified strategy
            // chain as a single card rather than through a second, unchecked write path.
            var span = SpanningCorrection(read.Text, composed.NewText);
            if (span is null)
            {
                return CorrectionApplicationOutcome.FromStatus(
                    CorrectionApplicationStatus.Failed,
                    "Нет безопасных автоматических исправлений.");
            }

            var write = await snapshot.Target
                .TryApplyCorrectionAsync(span, read.Text, cancellationToken)
                .ConfigureAwait(false);
            if (!write.Succeeded)
            {
                return CorrectionApplicationOutcome.FromStatus(
                    write.Detail == "no-applicable-strategy"
                        ? CorrectionApplicationStatus.UnsupportedWritePattern
                        : CorrectionApplicationStatus.VerificationFailed);
            }

            ScheduleBackgroundRefresh(monitor);
            return new CorrectionApplicationOutcome(
                CorrectionApplicationStatus.CorrectionApplied,
                Succeeded: true,
                UserMessage: CorrectionApplyResult.FormatResultMessage(
                    composed.AppliedCount, composed.RemainingRecommendations),
                WritePath: write.StrategyName);
        }
        catch (OperationCanceledException)
        {
            return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.Cancelled);
        }
        finally
        {
            monitor.EndApplyingCorrection();
            gate.Exit();
        }
    }

    /// <summary>
    /// Resolves only the exact snapshot range. A correction is never relocated:
    /// moving it to a similar nearby token can mutate text the user did not select.
    /// </summary>
    public static (bool Ok, CorrectionApplicationStatus Status, int Start, int Length) ResolveRange(
        string liveText,
        TextIssue issue)
    {
        var binding = CanonicalCorrection.TryBind(
            liveText, issue.Start, issue.Length, issue.Original, issue.Replacement, out var correction);
        return binding == CorrectionBindingStatus.Bound && correction is not null
            ? (true, CorrectionApplicationStatus.CorrectionApplied, correction.Start, correction.Length)
            : (false, StatusFor(binding), -1, 0);
    }

    /// <summary>The user-facing status for a correction that would not bind to the live text.</summary>
    public static CorrectionApplicationStatus StatusFor(CorrectionBindingStatus binding) => binding switch
    {
        CorrectionBindingStatus.OutOfRange => CorrectionApplicationStatus.RangeUnavailable,
        // A zero-length span carrying a non-empty original claims that empty string is
        // «abc». That is the same user-facing situation as any other mismatch: the finding
        // no longer describes the text, and the answer is to re-analyse rather than to hunt
        // for a range that was never there.
        _ => CorrectionApplicationStatus.OriginalMismatch,
    };

    /// <summary>
    /// The refusal a write verdict implies, or null when the write may go ahead.
    /// </summary>
    /// <remarks>
    /// The whole point of the three-valued verdict: a control that could not be interrogated
    /// produces "temporarily unavailable, try again", never "your field is read-only". The
    /// old boolean could not tell those apart, and reported the second for both — which is
    /// what the user saw every time the popup took the keyboard away from a perfectly
    /// editable field.
    /// </remarks>
    public static CorrectionApplicationStatus? StatusFor(EditabilityVerdict verdict) => verdict switch
    {
        EditabilityVerdict.Editable => null,
        EditabilityVerdict.ReadOnly => CorrectionApplicationStatus.ReadOnly,
        _ => CorrectionApplicationStatus.TargetUnavailable,
    };

    /// <summary>
    /// The single replacement that turns <paramref name="before"/> into <paramref name="after"/>.
    /// </summary>
    /// <remarks>
    /// Apply-all composes a whole new string, and writing a whole new string is the most
    /// destructive thing this code can do — it loses the caret, loses undo, and on a control
    /// with no settable value it cannot be done at all. Reducing the composition to the span
    /// that actually differs turns it back into an ordinary range replacement, which every
    /// strategy can perform and which leaves the rest of the document untouched.
    /// </remarks>
    public static CanonicalCorrection? SpanningCorrection(string before, string after)
    {
        var dirty = DirtyTextRangeTracker.Calculate(before, after);
        if (dirty.RemovedLength == 0 && dirty.InsertedLength == 0) return null;

        var original = before.Substring(dirty.Start, dirty.RemovedLength);
        var replacement = after.Substring(dirty.Start, dirty.InsertedLength);
        return CanonicalCorrection.TryBind(
                   before, dirty.Start, dirty.RemovedLength, original, replacement, out var correction)
               == CorrectionBindingStatus.Bound
            ? correction
            : null;
    }

    /// <summary>
    /// Starts the post-apply re-read without waiting for it.
    /// </summary>
    /// <remarks>
    /// Deliberately not awaited, and deliberately not cancellable by the caller's token: the
    /// correction is already committed and verified in the target application, and the only
    /// thing left to decide is when WriteLite's own view of that field catches up. Failure is
    /// logged and swallowed for the same reason it always was — a refresh that did not happen
    /// must not turn a correction that did into an error message.
    /// </remarks>
    private static void ScheduleBackgroundRefresh(TextFieldMonitor monitor)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PostWriteSettleDelay).ConfigureAwait(false);
                await monitor.RefreshOnceAfterSuppressionAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                CompatibilityLogger.Technical(
                    "correction-refresh-failed",
                    $"type={exception.GetType().Name}");
            }
        });
    }

    private static bool IsSimplePunctuationInsert(string replacement)
        => replacement is "." or "," or "!" or "?" or ":" or ";" or "…" or "—";
}
