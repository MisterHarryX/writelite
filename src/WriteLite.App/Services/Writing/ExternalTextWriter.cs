using System.Windows.Automation;
using WriteLite.Language.Core;

namespace WriteLite.Services.Writing;

/// <summary>The outcome of one attempt to change an external control's text.</summary>
/// <param name="Succeeded">The control was read back and holds the expected text.</param>
/// <param name="StrategyName">Which strategy produced this outcome, for logs and reports.</param>
/// <param name="Detail">Why it failed, when it did. Never shown to the user verbatim.</param>
public readonly record struct ExternalWriteResult(bool Succeeded, string StrategyName, string Detail)
{
    public static ExternalWriteResult NotApplicable(string strategy)
        => new(false, strategy, "not-applicable");

    public static ExternalWriteResult Failed(string strategy, string detail)
        => new(false, strategy, detail);

    public static ExternalWriteResult Ok(string strategy) => new(true, strategy, string.Empty);
}

/// <summary>
/// One way of getting replacement text into a control that belongs to another process.
/// </summary>
/// <remarks>
/// A strategy is chosen by what the control can do, never by what application it belongs to.
/// <see cref="Applies"/> is a capability question; if it returns true the strategy will be
/// tried, and if the write cannot be verified the next one is tried after it.
/// </remarks>
public interface IExternalWriteStrategy
{
    /// <summary>Name used in diagnostics and in the compatibility matrix.</summary>
    string Name { get; }

    /// <summary>True when this control exposes what the strategy needs.</summary>
    bool Applies(in TextTargetCapabilities capabilities);

    /// <summary>
    /// Replaces <paramref name="correction"/>'s span with its replacement.
    /// </summary>
    /// <remarks>
    /// The correction is already bound to the control's current text, so the strategy may
    /// rely on <see cref="CanonicalCorrection.Original"/> being exactly what stands at the
    /// span. Strategies that can check that cheaply on their own side do so anyway — a
    /// selection made by offset is the one thing that can be wrong without anyone noticing.
    /// </remarks>
    Task<ExternalWriteResult> WriteAsync(
        AutomationElement element,
        CanonicalCorrection correction,
        CancellationToken cancellationToken);
}

/// <summary>
/// Applies a correction to an external control by trying every strategy the control supports,
/// in order, until one of them can be verified.
/// </summary>
/// <remarks>
/// <para><b>Why a chain and not a single write.</b> The old path had exactly one route —
/// <c>ValuePattern</c>, with a Win32 fast path inside it — and treated the absence of that
/// route as proof the field was read-only. A control being editable and a control supporting
/// <c>ValuePattern.SetValue</c> are unrelated facts: Chromium exposes its inputs through
/// <c>TextPattern</c> with no writable value, Win32 edit controls answer to a window message
/// whether or not any pattern is present, and a WinUI box may offer both or neither.</para>
///
/// <para><b>Order.</b> Least invasive first. A window message and a pattern call change only
/// the control; a clipboard paste borrows a shared system resource and synthesises input, so
/// it goes last and only when nothing else applies or nothing else worked.</para>
///
/// <para><b>Verification is the contract.</b> A strategy reporting success is a claim, not
/// evidence. <see cref="ApplyAsync"/> re-reads the control after every attempt and only stops
/// when what it reads is what the correction said it would be — §11 of the brief. A strategy
/// whose write "succeeded" but did not change the text is a failed strategy and the chain
/// moves on.</para>
/// </remarks>
public sealed class ExternalTextWriter
{
    private readonly IReadOnlyList<IExternalWriteStrategy> _strategies;
    private readonly Func<CancellationToken, Task<(bool Succeeded, string Text)>> _readBack;

    public ExternalTextWriter(
        Func<CancellationToken, Task<(bool Succeeded, string Text)>> readBack,
        IReadOnlyList<IExternalWriteStrategy>? strategies = null)
    {
        _readBack = readBack ?? throw new ArgumentNullException(nameof(readBack));
        _strategies = strategies ??
        [
            new NativeEditWindowWriteStrategy(),
            new ValuePatternWriteStrategy(),
            new SelectionPasteWriteStrategy(),
        ];
    }

    /// <summary>The strategies that would be tried for a control, in the order they run.</summary>
    public IReadOnlyList<string> PlanFor(in TextTargetCapabilities capabilities)
    {
        var plan = new List<string>(_strategies.Count);
        foreach (var strategy in _strategies)
        {
            if (strategy.Applies(capabilities)) plan.Add(strategy.Name);
        }

        return plan;
    }

    /// <summary>
    /// Applies the correction, returning the strategy that worked or the reason none did.
    /// </summary>
    public async Task<ExternalWriteResult> ApplyAsync(
        AutomationElement element,
        TextTargetCapabilities capabilities,
        CanonicalCorrection correction,
        string currentText,
        CancellationToken cancellationToken = default)
    {
        var expected = correction.ApplyTo(currentText);
        var attempted = 0;
        var lastDetail = "no-strategy";

        var caps = capabilities;
        foreach (var strategy in _strategies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!strategy.Applies(caps)) continue;

            attempted++;
            ExternalWriteResult attempt;
            try
            {
                attempt = await strategy.WriteAsync(element, correction, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                CompatibilityLogger.AccessError("write-strategy-" + strategy.Name, null, exception);
                attempt = ExternalWriteResult.Failed(strategy.Name, exception.GetType().Name);
            }

            // The write may have committed even when the strategy could not confirm it, so
            // the read-back runs either way and is the only thing that decides.
            if (await VerifyAsync(expected, cancellationToken).ConfigureAwait(false))
            {
                CompatibilityLogger.Technical(
                    "external-write-verified",
                    $"strategy={strategy.Name} attempt={attempted}");
                return ExternalWriteResult.Ok(strategy.Name);
            }

            lastDetail = attempt.Succeeded ? "unverified" : attempt.Detail;
            CompatibilityLogger.Technical(
                "external-write-strategy-failed",
                $"strategy={strategy.Name} detail={lastDetail}");
        }

        return new ExternalWriteResult(
            false,
            attempted == 0 ? "none" : "exhausted",
            attempted == 0 ? "no-applicable-strategy" : lastDetail);
    }

    /// <summary>
    /// Re-reads the control until it shows the expected text, or the back-off runs out.
    /// </summary>
    /// <remarks>
    /// The first probe is immediate: a pattern write against a well-behaved control is
    /// visible on the next read, which is the common case and must not be taxed. The delays
    /// after it exist for providers that commit on their own message loop — a Chromium
    /// renderer, an Electron composer — and total well under a quarter of a second.
    /// </remarks>
    private async Task<bool> VerifyAsync(string expected, CancellationToken cancellationToken)
    {
        foreach (var delayMs in (int[])[0, 25, 70, 150])
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }

            var read = await _readBack(cancellationToken).ConfigureAwait(false);
            if (read.Succeeded && string.Equals(read.Text, expected, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
