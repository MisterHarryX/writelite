using System.Windows;
using System.Windows.Automation;

namespace WriteLite.Services;

/// <summary>
/// How WriteLite reads a control's text and where to anchor UI against it.
/// </summary>
/// <remarks>
/// Reading only. Writing used to live here too, which forced every adapter to answer
/// "do you support direct write?" — and made the answer for the read-only-value adapter a
/// flat no, which the apply path then reported to the user as "this field is read-only".
/// Whether a control can be written to is a property of the control, not of the adapter that
/// happened to be picked for reading it, and it is now decided by
/// <see cref="TextTargetCapabilityPolicy"/> and carried out by
/// <see cref="Writing.ExternalTextWriter"/>.
/// </remarks>
public interface ITextTargetAdapter
{
    string Name { get; }

    bool CanHandle(AutomationElement element);

    Task<(bool Succeeded, string Text)> ReadTextAsync(AutomationElement element, CancellationToken cancellationToken = default);

    Task<Rect> GetAnchorRectangleAsync(AutomationElement element, CancellationToken cancellationToken = default);
}
