using System.Windows;
using System.Windows.Automation;

namespace WriteLite.Services;

public sealed class AutomationTextTarget
{
    private static readonly TimeSpan DefaultUiaTimeout = TimeSpan.FromMilliseconds(750);
    private static UiaCircuitBreaker SharedBreaker = new();

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
        // same reason Bounds and Name are: the policy check is ~10 cross-process
        // UIA reads. As a live expression-bodied property it was re-evaluated on
        // the DISPATCHER at every snapshot publish — two to three times per
        // keystroke — and a busy Chromium target parked the UI for 5-9 s
        // (dump-confirmed under OnSnapshotChanged). Staleness is bounded by the
        // monitor's 800 ms poll, which already re-creates this target; the apply
        // path additionally re-validates through TryReadTextAsync.
        IsEditable = EditableTextTargetPolicy.IsEditableTextTarget(element);
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
    public bool SupportsDirectWrite => Adapter.SupportsDirectWrite;
    public bool IsEditable { get; }
    public bool SupportsRangeReplacement => Adapter.SupportsRangeReplacement;
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

    public async Task<(bool Succeeded, string Error)> TryReplaceAllAsync(
        string newText,
        int caretIndex = -1,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (SharedBreaker.IsOpen(Identity.RuntimeId))
        {
            return (false, "Target temporarily unavailable after UI Automation timeout.");
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operation = Task.Run(async () =>
        {
            if (Adapter is ValuePatternTextAdapter valueAdapter && caretIndex >= 0)
            {
                return await valueAdapter.ReplaceTextAsync(
                    Element, newText, caretIndex, operationCancellation.Token).ConfigureAwait(false);
            }

            return await Adapter.ReplaceTextAsync(Element, newText, operationCancellation.Token).ConfigureAwait(false);
        }, operationCancellation.Token);

        try
        {
            var result = await operation
                .WaitAsync(timeout ?? DefaultUiaTimeout, cancellationToken)
                .ConfigureAwait(false);
            CompatibilityLogger.Operation("write", ProcessId, result.Succeeded, Adapter.Name);
            return result;
        }
        catch (TimeoutException)
        {
            operationCancellation.Cancel();
            SharedBreaker.Trip(Identity.RuntimeId);
            CompatibilityLogger.Operation("uia-timeout", ProcessId, false, "write");
            CompatibilityLogger.State("uia-timeout");
            return (false, "WriteLite timed out while communicating with the target field.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operationCancellation.Cancel();
            throw;
        }
        catch (Exception exception)
        {
            CompatibilityLogger.AccessError("write", ProcessId, exception);
            return (false, "WriteLite could not write to the target field.");
        }
    }

    public async Task<(bool Succeeded, string Error)> TryReplaceRangeAsync(
        int start,
        int length,
        string replacement,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (SharedBreaker.IsOpen(Identity.RuntimeId))
        {
            return (false, "Target temporarily unavailable after UI Automation timeout.");
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operation = Task.Run(async () =>
            await Adapter.ReplaceRangeAsync(
                Element, start, length, replacement, operationCancellation.Token).ConfigureAwait(false),
            operationCancellation.Token);

        try
        {
            var result = await operation
                .WaitAsync(timeout ?? DefaultUiaTimeout, cancellationToken)
                .ConfigureAwait(false);
            CompatibilityLogger.Operation("write-range", ProcessId, result.Succeeded, Adapter.Name);
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
            CompatibilityLogger.Operation("uia-timeout", ProcessId, false, "write-range");
            CompatibilityLogger.State("uia-timeout");
            return (false, "WriteLite timed out while communicating with the target field.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operationCancellation.Cancel();
            throw;
        }
        catch (Exception exception)
        {
            CompatibilityLogger.AccessError("write-range", ProcessId, exception);
            return (false, "WriteLite could not write to the target field.");
        }
    }

    public async Task<(bool Succeeded, bool IsEditable)> TryValidateForWriteAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (SharedBreaker.IsOpen(Identity.RuntimeId))
        {
            return (false, false);
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operation = Task.Run(
            () => EditableTextTargetPolicy.IsEditableTextTarget(Element),
            operationCancellation.Token);
        try
        {
            var editable = await operation
                .WaitAsync(timeout ?? DefaultUiaTimeout, cancellationToken)
                .ConfigureAwait(false);
            SharedBreaker.Reset(Identity.RuntimeId);
            return (true, editable);
        }
        catch (TimeoutException)
        {
            operationCancellation.Cancel();
            SharedBreaker.Trip(Identity.RuntimeId);
            CompatibilityLogger.Operation("uia-timeout", ProcessId, false, "validate-write");
            return (false, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operationCancellation.Cancel();
            throw;
        }
        catch (Exception exception)
        {
            CompatibilityLogger.AccessError("validate-write", ProcessId, exception);
            return (false, false);
        }
    }
}
