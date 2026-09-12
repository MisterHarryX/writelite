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
/// <para><b>Order.</b> Chosen per control rather than fixed — see <c>PlanStrategies</c>.
/// Cheapest first for a control whose value is its text; the whole-value route demoted to
/// last for one that keeps a document model behind its value, because <c>SetValue</c> would
/// rewrite what the model no longer describes.</para>
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
        => [.. PlanStrategies(capabilities).Select(strategy => strategy.Name)];

    /// <summary>
    /// The routes available for one control, in the order they should be tried.
    /// </summary>
    /// <remarks>
    /// <para><b>The default order is by cost.</b> A window message and a pattern call change
    /// the control directly; a clipboard paste borrows a shared system resource and
    /// synthesises input, so it goes last. Measured through this path, a WPF text box takes a
    /// pattern write in 28 ms and a selected paste in 241 ms, and a browser field 27 ms
    /// against 1 152 ms — the difference between an apply that feels instant and one the user
    /// waits for.</para>
    ///
    /// <para><b>The exception is by scope.</b> <c>EM_REPLACESEL</c> and a selected paste
    /// replace the span the correction names; <c>ValuePattern.SetValue</c> replaces the whole
    /// value. Those are the same thing for a control whose value is its text and very
    /// different for one that keeps a document model behind it, where the whole-value write
    /// leaves the model describing text that is no longer there — see
    /// <see cref="TextTargetCapabilities.ValueIsProjectedFromDocumentModel"/>. For those
    /// controls the whole-value route is demoted to last rather than removed, because for a
    /// Chromium field with no selectable text it remains the only route there is, and a
    /// correction that cannot be applied at all is worse than one applied bluntly.</para>
    ///
    /// <para>An earlier fix for the same defect demoted the whole-value route for every
    /// control. It fixed Discord and made every ordinary text box in Windows pay a clipboard
    /// round-trip: <c>FieldMonitorApplyTests</c> measured p50 apply latency rising from under
    /// 300 ms to 384 ms. Ordering per control keeps both properties.</para>
    /// </remarks>
    private IReadOnlyList<IExternalWriteStrategy> PlanStrategies(TextTargetCapabilities capabilities)
    {
        var applicable = new List<IExternalWriteStrategy>(_strategies.Count);
        foreach (var strategy in _strategies)
        {
            if (strategy.Applies(capabilities)) applicable.Add(strategy);
        }

        if (!capabilities.ValueIsProjectedFromDocumentModel || applicable.Count < 2)
        {
            return applicable;
        }

        var wholeValue = applicable.FindIndex(strategy => strategy is ValuePatternWriteStrategy);
        if (wholeValue < 0 || wholeValue == applicable.Count - 1)
        {
            return applicable;
        }

        var demoted = applicable[wholeValue];
        applicable.RemoveAt(wholeValue);
        applicable.Add(demoted);
        return applicable;
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

        foreach (var strategy in PlanStrategies(capabilities))
        {
            cancellationToken.ThrowIfCancellationRequested();

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
        foreach (var delayMs in WriteLiteDefaults.Analysis.ExternalWriteVerifyBackoffMs)
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
