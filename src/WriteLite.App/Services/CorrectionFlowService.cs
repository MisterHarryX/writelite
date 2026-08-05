using WriteLite.Models;

namespace WriteLite.Services;

public sealed class CorrectionFlowService
{
    public static readonly TimeSpan PostWriteSettleDelay = TimeSpan.FromMilliseconds(400);

    public async Task<CorrectionApplyResult> ApplyAllAsync(
        TextSnapshot snapshot,
        TextFieldMonitor monitor,
        AsyncOperationGate gate,
        CancellationToken cancellationToken = default)
    {
        if (!gate.TryEnter())
        {
            CompatibilityLogger.State("correction-blocked");
            return CorrectionApplyResult.Blocked;
        }

        monitor.BeginApplyingCorrection();
        CompatibilityLogger.State("correction-started");

        try
        {
            var read = await snapshot.Target.TryReadTextAsync().ConfigureAwait(false);
            if (!read.Succeeded)
            {
                CompatibilityLogger.State("correction-failed");
                return FailedResult(0);
            }

            var composed = TextCorrectionService.ComposeApplyAll(
                read.Text,
                snapshot.Issues,
                snapshot.Target.SupportsDirectWrite);

            if (!composed.CanApply)
            {
                CompatibilityLogger.State("correction-failed");
                return new CorrectionApplyResult(
                    Succeeded: false,
                    WasBlocked: false,
                    AppliedCount: 0,
                    SkippedCount: composed.SkippedCount,
                    RemainingRecommendations: composed.RemainingRecommendations,
                    WriteOperations: 0,
                    UserMessage: null);
            }

            var caret = TextCorrectionService.EstimateCaretAfterApplyAll(composed.AppliedIssues);
            var write = await snapshot.Target.TryReplaceAllAsync(composed.NewText, caret).ConfigureAwait(false);
            if (!write.Succeeded)
            {
                CompatibilityLogger.State("correction-failed");
                return FailedResult(TextWriteTelemetry.WriteCount);
            }

            CompatibilityLogger.State("correction-completed");
            await Task.Delay(PostWriteSettleDelay, cancellationToken).ConfigureAwait(false);
            await monitor.RefreshOnceAfterSuppressionAsync(cancellationToken).ConfigureAwait(false);

            return new CorrectionApplyResult(
                Succeeded: true,
                WasBlocked: false,
                AppliedCount: composed.AppliedCount,
                SkippedCount: composed.SkippedCount,
                RemainingRecommendations: composed.RemainingRecommendations,
                WriteOperations: TextWriteTelemetry.WriteCount,
                UserMessage: CorrectionApplyResult.FormatResultMessage(
                    composed.AppliedCount,
                    composed.RemainingRecommendations));
        }
        finally
        {
            monitor.EndApplyingCorrection();
            gate.Exit();
        }
    }

    public async Task<CorrectionApplyResult> ApplySingleAsync(
        TextIssue issue,
        TextSnapshot snapshot,
        TextFieldMonitor monitor,
        AsyncOperationGate gate,
        CancellationToken cancellationToken = default)
    {
        if (!gate.TryEnter())
        {
            CompatibilityLogger.State("correction-blocked");
            return CorrectionApplyResult.Blocked;
        }

        monitor.BeginApplyingCorrection();
        CompatibilityLogger.State("correction-started");

        try
        {
            var read = await snapshot.Target.TryReadTextAsync().ConfigureAwait(false);
            if (!read.Succeeded)
            {
                CompatibilityLogger.State("correction-failed");
                return FailedResult(0);
            }

            if (!TextCorrectionService.TryApplySingle(
                    read.Text,
                    issue,
                    snapshot.Target.SupportsDirectWrite,
                    out var newText,
                    out var caret))
            {
                CompatibilityLogger.State("correction-failed");
                return FailedResult(0);
            }

            var write = snapshot.Target.SupportsRangeReplacement
                ? await snapshot.Target.TryReplaceRangeAsync(issue.Start, issue.Length, issue.Replacement ?? "").ConfigureAwait(false)
                : await snapshot.Target.TryReplaceAllAsync(newText, caret).ConfigureAwait(false);
            if (!write.Succeeded && snapshot.Target.SupportsRangeReplacement)
            {
                write = await snapshot.Target.TryReplaceAllAsync(newText, caret).ConfigureAwait(false);
            }
            if (!write.Succeeded)
            {
                CompatibilityLogger.State("correction-failed");
                return FailedResult(TextWriteTelemetry.WriteCount);
            }

            CompatibilityLogger.State("correction-completed");
            await Task.Delay(PostWriteSettleDelay, cancellationToken).ConfigureAwait(false);
            await monitor.RefreshOnceAfterSuppressionAsync(cancellationToken).ConfigureAwait(false);

            return new CorrectionApplyResult(
                Succeeded: true,
                WasBlocked: false,
                AppliedCount: 1,
                SkippedCount: 0,
                RemainingRecommendations: Math.Max(0, snapshot.Issues.Count - 1),
                WriteOperations: TextWriteTelemetry.WriteCount,
                UserMessage: null);
        }
        finally
        {
            monitor.EndApplyingCorrection();
            gate.Exit();
        }
    }

    private static CorrectionApplyResult FailedResult(int writeOperations)
    {
        return new CorrectionApplyResult(
            Succeeded: false,
            WasBlocked: false,
            AppliedCount: 0,
            SkippedCount: 0,
            RemainingRecommendations: 0,
            WriteOperations: writeOperations,
            UserMessage: null);
    }
}
