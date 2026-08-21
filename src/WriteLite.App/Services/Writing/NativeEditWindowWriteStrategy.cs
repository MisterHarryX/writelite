using System.Windows.Automation;
using WriteLite.Language.Core;

namespace WriteLite.Services.Writing;

/// <summary>
/// Replaces a range in a classic Win32 <c>EDIT</c> or <c>RichEdit</c> control through window
/// messages.
/// </summary>
/// <remarks>
/// <para>First in the chain because it is the least invasive route that exists: it changes
/// the control and nothing else, it leaves the caret and the scroll position where the user
/// would expect them, and on most edit classes it produces a real undo unit, so
/// <c>Ctrl+Z</c> puts the user's own word back.</para>
///
/// <para><b>What it will not do.</b> It refuses to set a selection it cannot first show
/// contains the text the correction claims. Offsets arrive from the UI Automation side of the
/// world and the selection they are handed to is the window's, and those two do not always
/// count line breaks the same way — see <see cref="Win32TextEdit.TryReplaceVerifiedRange"/>.
/// A strategy that wrote anyway would put the right word at the wrong place, which is worse
/// than not writing at all; refusing simply passes the correction to the next strategy.</para>
/// </remarks>
public sealed class NativeEditWindowWriteStrategy : IExternalWriteStrategy
{
    public string Name => "win32-edit";

    public bool Applies(in TextTargetCapabilities capabilities)
        => capabilities is { HasNativeEditWindow: true, IsPassword: false, IsEnabled: true };

    public Task<ExternalWriteResult> WriteAsync(
        AutomationElement element,
        CanonicalCorrection correction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Win32TextEdit.TryGetHwnd(element, out var hwnd))
        {
            return Task.FromResult(ExternalWriteResult.NotApplicable(Name));
        }

        var outcome = Win32TextEdit.TryReplaceVerifiedRange(
            hwnd,
            correction.Start,
            correction.Length,
            correction.Original,
            correction.Replacement);

        if (outcome != Win32RangeWriteOutcome.Written)
        {
            return Task.FromResult(ExternalWriteResult.Failed(Name, outcome.ToString()));
        }

        CompatibilityLogger.Technical(
            "write-path",
            Win32TextEdit.IsUndoUnreliable(hwnd)
                ? "win32-range undo=unreliable"
                : "win32-range undo=likely");
        TextWriteTelemetry.RecordWrite();
        return Task.FromResult(ExternalWriteResult.Ok(Name));
    }
}
