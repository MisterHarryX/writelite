using System.Windows;
using System.Windows.Automation;
using WriteLite.Language.Core;
using WriteLite.Services.Writing;

namespace WriteLite.Services;

public sealed class AutomationTextTarget
{
    private static TimeSpan DefaultUiaTimeout => WriteLiteDefaults.Analysis.UiaOperationTimeout;
    private static UiaCircuitBreaker SharedBreaker = new();

    private readonly ExternalTextWriter _writer;

    public AutomationTextTarget(AutomationElement element, ITextTargetAdapter adapter)
    {
        Element = element;
        Adapter = adapter;
        ProcessId = element.Current.ProcessId;
        Bounds = element.Current.BoundingRectangle;
        Name = element.Current.Name ?? string.Empty;
        ControlTypeName = element.Current.LocalizedControlType ?? string.Empty;
        Identity = ActiveTextTargetIdentity.From(element);
        // Snapshotted HERE, on the monitor's bounded background worker, for the
        // same reason Bounds and Name are: the capability read is ~10 cross-process
        // UIA calls. As a live expression-bodied property it was re-evaluated on
        // the DISPATCHER at every snapshot publish — two to three times per
        // keystroke — and a busy Chromium target parked the UI for 5-9 s
        // (dump-confirmed under OnSnapshotChanged). Staleness is bounded by the
        // monitor's 800 ms poll, which already re-creates this target; the apply
        // path additionally re-reads capabilities through TryEvaluateWriteAsync.
        Capabilities = ReadCapabilities(element);
        WriteVerdict = TextTargetCapabilityPolicy.Evaluate(Capabilities);
        _writer = new ExternalTextWriter(token => TryReadTextAsync(cancellationToken: token));
    }

    private static TextTargetCapabilities ReadCapabilities(AutomationElement element)
    {
        try
        {
            return TextTargetCapabilities.Read(element);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException
                                              or InvalidOperationException)
        {
            return TextTargetCapabilities.Unknown;
        }
    }

    public static UiaCircuitBreaker CircuitBreaker => SharedBreaker;

    public static void ConfigureCircuitBreaker(UiaCircuitBreaker circuitBreaker)
        => SharedBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));

    public AutomationElement Element { get; }
    public ITextTargetAdapter Adapter { get; }
    public int ProcessId { get; }
    public Rect Bounds { get; }
    public string Name { get; }
    public string ControlTypeName { get; }
    public ActiveTextTargetIdentity Identity { get; }
    /// <summary>What the control said about itself when this target was created.</summary>
    public TextTargetCapabilities Capabilities { get; }

    /// <summary>Whether this control's text may be changed, as judged from its capabilities.</summary>
    public EditabilityVerdict WriteVerdict { get; }

    /// <summary>
    /// True when WriteLite has a way to change this control's text.
    /// </summary>
    /// <remarks>
    /// This used to be "the selected adapter is the ValuePattern one", which is a fact about
    /// WriteLite's internals rather than about the control, and it is what made a browser
    /// field report itself read-only in the sidebar banner and refuse corrections. It now
    /// answers the question it is named for: is there any strategy that could write here.
    /// </remarks>
    public bool SupportsDirectWrite => WriteVerdict == EditabilityVerdict.Editable;

    /// <summary>Retained name for the analysis and UI layers: the field is not read-only.</summary>
    public bool IsEditable => WriteVerdict == EditabilityVerdict.Editable;

    /// <summary>True when a correction can be applied without rewriting the whole value.</summary>
    public bool SupportsRangeReplacement => WriteVerdict == EditabilityVerdict.Editable;

    /// <summary>The strategies that would be tried for this control, in order.</summary>
    public IReadOnlyList<string> WriteStrategyPlan => _writer.PlanFor(Capabilities);

    public string AdapterName => Adapter.Name;

    public async Task<(bool Succeeded, string Text)> TryReadTextAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (SharedBreaker.IsOpen(Identity.RuntimeId))
        {
            CompatibilityLogger.Technical("uia-circuit-skip", "op=read");
            return (false, string.Empty);
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operation = Task.Run(async () =>
            await Adapter.ReadTextAsync(Element, operationCancellation.Token).ConfigureAwait(false),
            operationCancellation.Token);

        try
        {
            var result = await operation
                .WaitAsync(timeout ?? DefaultUiaTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                SharedBreaker.Reset(Identity.RuntimeId);
            }

            return result;
        }
        catch (TimeoutException)
        {
            operationCancellation.Cancel();
            SharedBreaker.Trip(Identity.RuntimeId);
            CompatibilityLogger.Operation("uia-timeout", ProcessId, false, "read");
            CompatibilityLogger.State("uia-timeout");
            return (false, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operationCancellation.Cancel();
            throw;
        }
        catch (Exception exception)
        {
            CompatibilityLogger.AccessError("read", ProcessId, exception);
            return (false, string.Empty);
        }
    }

    /// <summary>
    /// Re-reads the control's capabilities and says whether it may be written to now.
    /// </summary>
    /// <remarks>
    /// Separate from the snapshot taken at construction because a control can legitimately
    /// change — a form can disable a field, a document can be locked. What it must not do is
    /// change its answer because WriteLite's own popup took the keyboard, which is why the
    /// verdict this returns does not consider focus at all.
    /// </remarks>
    public async Task<EditabilityVerdict> TryEvaluateWriteAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (SharedBreaker.IsOpen(Identity.RuntimeId))
        {
            return EditabilityVerdict.Unavailable;
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operation = Task.Run(
            () => TextTargetCapabilityPolicy.Evaluate(TextTargetCapabilities.Read(Element)),
            operationCancellation.Token);
        try
        {
            var verdict = await operation
                .WaitAsync(timeout ?? DefaultUiaTimeout, cancellationToken)
                .ConfigureAwait(false);
            SharedBreaker.Reset(Identity.RuntimeId);
            return verdict;
        }
        catch (TimeoutException)
        {
            operationCancellation.Cancel();
            SharedBreaker.Trip(Identity.RuntimeId);
            CompatibilityLogger.Operation("uia-timeout", ProcessId, false, "validate-write");
            return EditabilityVerdict.Unavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operationCancellation.Cancel();
            throw;
        }
        catch (Exception exception)
        {
            CompatibilityLogger.AccessError("validate-write", ProcessId, exception);
            return EditabilityVerdict.Unavailable;
        }
    }

    /// <summary>
    /// Applies one canonical correction, trying every write strategy the control supports.
    /// </summary>
    /// <remarks>
    /// The single door through which every external edit now passes. Callers no longer choose
    /// between a range write and a whole-value write, because that choice was WriteLite
    /// guessing at a control's internals from the adapter it happened to pick; the writer
    /// asks the control what it supports and verifies the result whatever it did.
    /// </remarks>
    public async Task<ExternalWriteResult> TryApplyCorrectionAsync(
        CanonicalCorrection correction,
        string currentText,
        CancellationToken cancellationToken = default)
    {
        if (SharedBreaker.IsOpen(Identity.RuntimeId))
        {
            return ExternalWriteResult.Failed("none", "circuit-open");
        }

        var capabilities = ReadCapabilities(Element);
        var result = await _writer
            .ApplyAsync(Element, capabilities, correction, currentText, cancellationToken)
            .ConfigureAwait(false);
        CompatibilityLogger.Operation("write-range", ProcessId, result.Succeeded, result.StrategyName);
        if (result.Succeeded)
        {
            SharedBreaker.Reset(Identity.RuntimeId);
        }

        return result;
    }
}
