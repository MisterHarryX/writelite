using WriteLite.Language.Core;
using WriteLite.Models;

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
            CorrectionApplicationStatus.UnsupportedWritePattern => ("Прямая запись не поддерживается. Скопируйте исправление.", true, false),
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

    public static CorrectionApplicationStatus ValidateSnapshotState(
        string snapshotTargetId,
        int snapshotGeneration,
        long snapshotTextVersion,
        string snapshotText,
        string? currentTargetId,
        int currentGeneration,
        long currentTextVersion,
        string? currentText)
        => !string.Equals(snapshotTargetId, currentTargetId, StringComparison.Ordinal)
           || snapshotGeneration != currentGeneration
           || snapshotTextVersion != currentTextVersion
           || !string.Equals(snapshotText, currentText, StringComparison.Ordinal)
            ? CorrectionApplicationStatus.CorrectionStale
            : CorrectionApplicationStatus.CorrectionApplied;

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

            if (!snapshot.Target.IsEditable || !snapshot.Target.SupportsDirectWrite)
            {
                return CorrectionApplicationOutcome.FromStatus(
                    snapshot.Target.IsEditable
                        ? CorrectionApplicationStatus.UnsupportedWritePattern
                        : CorrectionApplicationStatus.ReadOnly);
            }

            var liveWriteState = await snapshot.Target
                .TryValidateForWriteAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!liveWriteState.Succeeded)
            {
                return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.TargetUnavailable);
            }

            if (!liveWriteState.IsEditable)
            {
                return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.ReadOnly);
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

            var resolved = ResolveRange(live, issue);
            if (!resolved.Ok)
            {
                CompatibilityLogger.Technical("correction-apply-failed", $"status={resolved.Status}");
                return CorrectionApplicationOutcome.FromStatus(resolved.Status);
            }

            var start = resolved.Start;
            var length = resolved.Length;
            var replacement = issue.Replacement!;

            if (ProtectedTextSpans.Overlaps(start, Math.Max(length, replacement.Length), ProtectedTextSpans.Find(live)))
            {
                // Allow pure punctuation insert at end of non-protected letter.
                if (!(length == 0 && IsSimplePunctuationInsert(replacement)))
                {
                    return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.ProtectedTokenConflict);
                }
            }

            var expectedText = live.Remove(start, length).Insert(start, replacement);
            snapshotStatus = _snapshotValidator(snapshot, monitor);
            if (snapshotStatus != CorrectionApplicationStatus.CorrectionApplied)
            {
                return CorrectionApplicationOutcome.FromStatus(snapshotStatus);
            }

            liveWriteState = await snapshot.Target
                .TryValidateForWriteAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!liveWriteState.Succeeded)
            {
                return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.TargetUnavailable);
            }

            if (!liveWriteState.IsEditable)
            {
                return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.ReadOnly);
            }

            string writePath;
            (bool Succeeded, string Error) write;
            if (snapshot.Target.SupportsRangeReplacement)
            {
                write = await snapshot.Target
                    .TryReplaceRangeAsync(
                        start, length, replacement, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                writePath = write.Succeeded ? "range" : "range-failed";
            }
            else
            {
                var caret = start + replacement.Length;
                write = await snapshot.Target
                    .TryReplaceAllAsync(expectedText, caret, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                writePath = write.Succeeded ? "value-full" : "value-full-failed";
            }

            // A failed/timeout write is never followed by a second write: the
            // provider may have committed late. Verification is authoritative.
            var verified = await VerifyExpectedTextAsync(snapshot.Target, expectedText).ConfigureAwait(false);
            if (!verified)
            {
                CompatibilityLogger.Technical(
                    "correction-verify-mismatch",
                    $"path={writePath} writeReportedSuccess={(write.Succeeded ? 1 : 0)}");
                return CorrectionApplicationOutcome.FromStatus(
                    CorrectionApplicationStatus.VerificationFailed,
                    write.Succeeded ? null : write.Error);
            }

            CompatibilityLogger.State("correction-completed");
            CompatibilityLogger.Technical("correction-applied", $"path={writePath} start={start} len={length}");
            await RefreshAfterVerifiedWriteAsync(monitor).ConfigureAwait(false);

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

            if (!snapshot.Target.IsEditable || !snapshot.Target.SupportsDirectWrite)
            {
                return CorrectionApplicationOutcome.FromStatus(
                    snapshot.Target.IsEditable
                        ? CorrectionApplicationStatus.UnsupportedWritePattern
                        : CorrectionApplicationStatus.ReadOnly);
            }

            var liveWriteState = await snapshot.Target
                .TryValidateForWriteAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!liveWriteState.Succeeded)
            {
                return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.TargetUnavailable);
            }

            if (!liveWriteState.IsEditable)
            {
                return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.ReadOnly);
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

            liveWriteState = await snapshot.Target
                .TryValidateForWriteAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!liveWriteState.Succeeded)
            {
                return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.TargetUnavailable);
            }

            if (!liveWriteState.IsEditable)
            {
                return CorrectionApplicationOutcome.FromStatus(CorrectionApplicationStatus.ReadOnly);
            }

            var caret = TextCorrectionService.EstimateCaretAfterApplyAll(composed.AppliedIssues);
            var write = await snapshot.Target
                .TryReplaceAllAsync(composed.NewText, caret, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!await VerifyExpectedTextAsync(snapshot.Target, composed.NewText).ConfigureAwait(false))
            {
                return CorrectionApplicationOutcome.FromStatus(
                    CorrectionApplicationStatus.VerificationFailed,
                    write.Succeeded ? null : write.Error);
            }

            await RefreshAfterVerifiedWriteAsync(monitor).ConfigureAwait(false);
            return new CorrectionApplicationOutcome(
                CorrectionApplicationStatus.CorrectionApplied,
                Succeeded: true,
                UserMessage: CorrectionApplyResult.FormatResultMessage(
                    composed.AppliedCount, composed.RemainingRecommendations),
                WritePath: "apply-all");
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
        if (!TextCorrectionService.IsRangeValid(liveText, issue.Start, issue.Length))
        {
            return (false, CorrectionApplicationStatus.RangeUnavailable, -1, 0);
        }

        if (issue.Length == 0)
        {
            if (!string.IsNullOrEmpty(issue.Original))
            {
                return (false, CorrectionApplicationStatus.OriginalMismatch, -1, 0);
            }

            return (true, CorrectionApplicationStatus.CorrectionApplied, issue.Start, 0);
        }

        var slice = liveText.Substring(issue.Start, issue.Length);
        if (string.Equals(slice, issue.Original, StringComparison.Ordinal))
        {
            return (true, CorrectionApplicationStatus.CorrectionApplied, issue.Start, issue.Length);
        }

        return (false, CorrectionApplicationStatus.OriginalMismatch, -1, 0);
    }

    private static async Task<bool> VerifyExpectedTextAsync(
        AutomationTextTarget target,
        string expectedText)
    {
        var delays = new[] { 40, 90, 180 };
        foreach (var delayMs in delays)
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            var verify = await target.TryReadTextAsync().ConfigureAwait(false);
            if (verify.Succeeded
                && string.Equals(verify.Text, expectedText, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task RefreshAfterVerifiedWriteAsync(TextFieldMonitor monitor)
    {
        try
        {
            await Task.Delay(PostWriteSettleDelay).ConfigureAwait(false);
            await monitor.RefreshOnceAfterSuppressionAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The target already confirmed the write. Refresh failure must not
            // turn a successful, verified correction into an ambiguous failure.
            CompatibilityLogger.Technical(
                "correction-refresh-failed",
                $"type={exception.GetType().Name}");
        }
    }

    private static bool IsSimplePunctuationInsert(string replacement)
        => replacement is "." or "," or "!" or "?" or ":" or ";" or "…" or "—";
}
