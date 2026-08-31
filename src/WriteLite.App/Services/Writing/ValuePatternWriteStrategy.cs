using System.Windows.Automation;
using WriteLite.Language.Core;

namespace WriteLite.Services.Writing;

/// <summary>
/// Replaces a range through <c>ValuePattern</c>, by reading the value, splicing the
/// correction into it and setting it back.
/// </summary>
/// <remarks>
/// <para>The route WriteLite always had, kept because it is the only one some value providers
/// offer, and because it is a single cross-process call — 28 ms against a selected paste's
/// 241 ms on the same WPF text box. Its cost is that <c>SetValue</c> replaces the entire value
/// rather than the corrected span: undo history is usually lost, the caret goes wherever the
/// provider decides, and a host that keeps its own document model behind the value is left
/// with a model that no longer matches its DOM. For that last class of control this strategy
/// is demoted to the end of the chain rather than removed from it — see
/// <c>ExternalTextWriter.PlanStrategies</c>.</para>
///
/// <para>The splice is done through <see cref="CanonicalCorrection.ApplyTo"/> against the
/// value read here and now, not against the snapshot the analysis was made from. If the
/// field changed underneath, <c>ApplyTo</c> is working from a string whose span no longer
/// holds the original, so the strategy re-binds first and gives up rather than writing into
/// text it has not checked.</para>
/// </remarks>
public sealed class ValuePatternWriteStrategy : IExternalWriteStrategy
{
    public string Name => "value-pattern";

    public bool Applies(in TextTargetCapabilities capabilities)
        => capabilities is { HasWritableValuePattern: true, IsPassword: false, IsEnabled: true };

    public Task<ExternalWriteResult> WriteAsync(
        AutomationElement element,
        CanonicalCorrection correction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObject)
            || patternObject is not ValuePattern valuePattern)
        {
            return Task.FromResult(ExternalWriteResult.NotApplicable(Name));
        }

        if (valuePattern.Current.IsReadOnly)
        {
            return Task.FromResult(ExternalWriteResult.Failed(Name, "value-read-only"));
        }

        var live = valuePattern.Current.Value ?? string.Empty;
        var status = CanonicalCorrection.TryBind(
            live, correction.Start, correction.Length, correction.Original, correction.Replacement, out var bound);
        if (status != CorrectionBindingStatus.Bound || bound is null)
        {
            return Task.FromResult(ExternalWriteResult.Failed(Name, status.ToString()));
        }

        cancellationToken.ThrowIfCancellationRequested();
        CompatibilityLogger.Technical("write-path", "valuepattern undo=unavailable");
        valuePattern.SetValue(bound.ApplyTo(live));
        TextWriteTelemetry.RecordWrite();
        return Task.FromResult(ExternalWriteResult.Ok(Name));
    }
}
