using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using WriteLite.Language.Core;

namespace WriteLite.Services.Writing;

/// <summary>
/// Replaces a range in a control that exposes text and selection but no way to set a value:
/// select exactly the range through <c>TextPattern</c>, then paste over it.
/// </summary>
/// <remarks>
/// <para><b>Why this strategy is the answer to the read-only defect.</b> Chromium inputs and
/// textareas, <c>contenteditable</c> regions, Electron composers, WinUI text boxes and WPF
/// <c>RichTextBox</c> all present the same shape to automation: a text provider that can read
/// and select, and either no <c>ValuePattern</c> or one that declares itself read-only.
/// WriteLite read those fields happily and then told the user the field was read-only,
/// because the only write it had was <c>ValuePattern.SetValue</c>. Nothing about that shape
/// means the user cannot type — it means the application owns its own editing model, and the
/// way to reach an application's own editing model is to use the same input the user
/// does.</para>
///
/// <para><b>Why it is last.</b> It borrows the clipboard, which belongs to the user, and it
/// synthesises a keystroke, which goes wherever the keyboard is pointing. Both are fine when
/// nothing else works and unnecessary when something else does.</para>
///
/// <para><b>What it refuses to do.</b> It will not press a key unless the window in the
/// foreground belongs to the target's own process and the target has focus — otherwise the
/// paste would land in whatever the user switched to. It selects only after confirming the
/// range it built actually contains the word the correction names, so a provider that counts
/// characters differently produces no write rather than a misplaced one. It sends
/// <c>Ctrl+V</c> and nothing else: never Enter, never Tab, never a click.</para>
/// </remarks>
public sealed class SelectionPasteWriteStrategy : IExternalWriteStrategy
{
    /// <summary>
    /// How long the target is given to consume the paste before the clipboard goes back.
    /// </summary>
    /// <remarks>
    /// The keystroke is delivered to the target's message queue and handled on its own loop,
    /// so the clipboard has to stay put until it has been read. Restoring too early loses the
    /// correction; restoring too late leaves the user's clipboard wrong for longer than
    /// necessary. This is the shortest delay that survived every application in the tested
    /// matrix, and the restore happens regardless of whether the paste worked.
    /// </remarks>
    private static readonly TimeSpan PasteSettleDelay = TimeSpan.FromMilliseconds(120);

    public string Name => "selection-paste";

    public bool Applies(in TextTargetCapabilities capabilities)
        => capabilities is
        {
            HasTextPattern: true,
            SupportsTextSelection: true,
            IsPassword: false,
            IsEnabled: true,
            IsKeyboardFocusable: true,
            // A control that declares its value read-only and offers no edit window is
            // saying it does not accept typed input either; pasting into it would at best
            // do nothing and at worst overwrite a selection the user made for reading.
            ValueIsReadOnly: false,
        };

    public async Task<ExternalWriteResult> WriteAsync(
        AutomationElement element,
        CanonicalCorrection correction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject)
            || patternObject is not TextPattern textPattern)
        {
            return ExternalWriteResult.NotApplicable(Name);
        }

        if (!TryBuildRange(textPattern, correction, out var range) || range is null)
        {
            return ExternalWriteResult.Failed(Name, "range-not-addressable");
        }

        // The one check that makes an offset-built selection safe: the provider has to agree
        // that this range holds the word we are about to destroy.
        var selectedText = range.GetText(-1) ?? string.Empty;
        if (!string.Equals(selectedText, correction.Original, StringComparison.Ordinal))
        {
            CompatibilityLogger.Technical(
                "selection-paste-mismatch",
                $"expectedLength={correction.Original.Length} actualLength={selectedText.Length}");
            return ExternalWriteResult.Failed(Name, "selection-original-mismatch");
        }

        if (!TryFocusTarget(element, out var targetProcessId))
        {
            return ExternalWriteResult.Failed(Name, "focus-refused");
        }

        range.Select();
        cancellationToken.ThrowIfCancellationRequested();

        if (!ForegroundBelongsTo(targetProcessId))
        {
            return ExternalWriteResult.Failed(Name, "foreground-not-target");
        }

        var restore = ClipboardTransfer.Capture();
        try
        {
            if (!ClipboardTransfer.SetText(correction.Replacement))
            {
                return ExternalWriteResult.Failed(Name, "clipboard-unavailable");
            }

            if (!ForegroundBelongsTo(targetProcessId))
            {
                return ExternalWriteResult.Failed(Name, "foreground-changed");
            }

            KeyboardInput.SendPaste();
            TextWriteTelemetry.RecordWrite();
            CompatibilityLogger.Technical("write-path", "selection-paste undo=likely");
            await Task.Delay(PasteSettleDelay, cancellationToken).ConfigureAwait(false);
            return ExternalWriteResult.Ok(Name);
        }
        finally
        {
            restore.Restore();
        }
    }

    /// <summary>
    /// Builds the text range covering the correction's span, by moving endpoints only.
    /// </summary>
    /// <remarks>
    /// <para><b>Why <c>Move</c> is not used.</b> <c>TextPatternRange.Move</c> is specified to
    /// expand a degenerate range to one whole unit before moving it, so
    /// <c>Move(Character, 84)</c> does not leave an empty range at offset 84 — it leaves a
    /// one-character range there. Growing that by the correction's length then produced a
    /// range one character too long, and every selection came out shifted: measured against
    /// a WPF <c>RichTextBox</c>, asking for the eight characters of «роботает» at offset 84
    /// returned the nine characters «роботает&#160;».</para>
    ///
    /// <para>Moving the two endpoints from the collapsed document start is exact on every
    /// provider measured, and the order matters: the end goes out to
    /// <c>Start + Length</c> first, because dragging the start endpoint past the end would
    /// push the end along with it.</para>
    ///
    /// <para>The move counts are checked. A provider that could not move as far as asked has
    /// run out of document, and the range it produced is not the one requested — which the
    /// caller then confirms a second time by comparing the range's own text against the
    /// correction's original before anything is selected.</para>
    /// </remarks>
    private static bool TryBuildRange(
        TextPattern textPattern,
        CanonicalCorrection correction,
        out TextPatternRange? range)
    {
        range = null;
        try
        {
            var built = textPattern.DocumentRange.Clone();
            built.MoveEndpointByRange(TextPatternRangeEndpoint.Start, built, TextPatternRangeEndpoint.Start);
            built.MoveEndpointByRange(TextPatternRangeEndpoint.End, built, TextPatternRangeEndpoint.Start);

            var end = correction.Start + correction.Length;
            if (end > 0)
            {
                var moved = built.MoveEndpointByUnit(
                    TextPatternRangeEndpoint.End, TextUnit.Character, end);
                if (moved != end) return false;
            }

            if (correction.Start > 0)
            {
                var moved = built.MoveEndpointByUnit(
                    TextPatternRangeEndpoint.Start, TextUnit.Character, correction.Start);
                if (moved != correction.Start) return false;
            }

            range = built;
            return true;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException
                                              or InvalidOperationException
                                              or ArgumentException
                                              or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryFocusTarget(AutomationElement element, out int processId)
    {
        processId = 0;
        try
        {
            processId = element.Current.ProcessId;
            if (!element.Current.HasKeyboardFocus)
            {
                element.SetFocus();
            }

            return true;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException
                                              or InvalidOperationException)
        {
            CompatibilityLogger.AccessError("selection-paste-focus", processId, exception);
            return false;
        }
    }

    /// <summary>
    /// True when the window that would receive a keystroke belongs to the target's process.
    /// </summary>
    /// <remarks>
    /// §27: synthesised input goes to the foreground window, not to the element it was
    /// intended for. Checking the process rather than the exact window is deliberate — an
    /// application may host its edit control in a child window, or in a different top-level
    /// window of the same process — but crossing a process boundary is never legitimate here.
    /// </remarks>
    private static bool ForegroundBelongsTo(int targetProcessId)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        _ = GetWindowThreadProcessId(foreground, out var foregroundProcessId);
        if (foregroundProcessId == targetProcessId) return true;

        CompatibilityLogger.Technical(
            "selection-paste-blocked",
            $"target={targetProcessId} foreground={foregroundProcessId}");
        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
}
