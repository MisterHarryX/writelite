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
/// <para><b>Why it runs ahead of the value write.</b> It borrows the clipboard, which belongs
/// to the user, and it synthesises a keystroke, which goes wherever the keyboard is pointing
/// — so it was placed last, behind <c>ValuePattern.SetValue</c>. That was the wrong axis to
/// rank on. This strategy changes the span the correction names and leaves the rest of the
/// host's editing model alone; <c>SetValue</c> replaces the whole value and, in the very
/// hosts this strategy exists for, leaves the field unable to edit itself afterwards. The
/// clipboard is captured and put back either way; a broken composer is not recoverable that
/// cheaply.</para>
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

        if (!TryBuildRange(textPattern, correction.Start, correction.Length, out var range) || range is null)
        {
            return ExternalWriteResult.Failed(Name, "range-not-addressable");
        }

        // The one check that makes an offset-built selection safe: the provider has to agree
        // that this range holds the word we are about to destroy.
        if (!Holds(range, correction.Original)
            && !TryRealignToProviderText(textPattern, correction, ref range))
        {
            CompatibilityLogger.Technical(
                "selection-paste-mismatch",
                $"expectedLength={correction.Original.Length} start={correction.Start}");
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
        int start,
        int length,
        out TextPatternRange? range)
    {
        range = null;
        if (start < 0 || length < 0) return false;

        try
        {
            var built = textPattern.DocumentRange.Clone();
            built.MoveEndpointByRange(TextPatternRangeEndpoint.Start, built, TextPatternRangeEndpoint.Start);
            built.MoveEndpointByRange(TextPatternRangeEndpoint.End, built, TextPatternRangeEndpoint.Start);

            var end = start + length;
            if (end > 0)
            {
                var moved = built.MoveEndpointByUnit(
                    TextPatternRangeEndpoint.End, TextUnit.Character, end);
                if (moved != end) return false;
            }

            if (start > 0)
            {
                var moved = built.MoveEndpointByUnit(
                    TextPatternRangeEndpoint.Start, TextUnit.Character, start);
                if (moved != start) return false;
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

    /// <summary>True when the provider agrees the range holds exactly this text.</summary>
    private static bool Holds(TextPatternRange range, string expected)
    {
        try
        {
            return string.Equals(range.GetText(-1), expected, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException
                                              or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Rebuilds the range from an offset measured in the text provider's own document, when
    /// the offset the correction carries was measured somewhere else.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the two can disagree.</b> A correction's offsets are measured against the
    /// text WriteLite read, and the adapter that read it may have used <c>ValuePattern</c>
    /// while this strategy addresses <c>TextPattern</c>. Those are two strings from two
    /// providers on the same control, and they are not obliged to match character for
    /// character. Discord's composer is the case in hand: Slate keeps a zero-width no-break
    /// space in the value, so the value read is <c>"﻿…"</c> and every offset taken from
    /// it sits one character to the right of the same word in the document range. Built
    /// blind, the selection covers the wrong span; the check above catches that and this
    /// recovers from it rather than handing the correction to the whole-value write.</para>
    ///
    /// <para><b>Why it is not a text search with a shrug.</b> The word being corrected is
    /// usually a common one and usually occurs more than once. The occurrence nearest the
    /// offset the correction already carries is the one meant — the offset is wrong by a
    /// marker or two, not by a sentence — so a tie between two equally near occurrences is
    /// genuine ambiguity and is refused. The rebuilt range is then put through the same
    /// verification as the first one, because an offset agreeing with the document text still
    /// says nothing about a provider that counts its <c>Character</c> unit differently.</para>
    /// </remarks>
    private static bool TryRealignToProviderText(
        TextPattern textPattern,
        CanonicalCorrection correction,
        ref TextPatternRange? range)
    {
        string document;
        try
        {
            document = textPattern.DocumentRange.GetText(-1) ?? string.Empty;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException
                                              or InvalidOperationException)
        {
            return false;
        }

        var start = NearestOccurrence(document, correction.Original, correction.Start);
        if (start < 0 || start == correction.Start) return false;

        if (!TryBuildRange(textPattern, start, correction.Original.Length, out var rebuilt)
            || rebuilt is null
            || !Holds(rebuilt, correction.Original))
        {
            return false;
        }

        CompatibilityLogger.Technical(
            "selection-paste-realigned",
            $"from={correction.Start} to={start}");
        range = rebuilt;
        return true;
    }

    /// <summary>
    /// The occurrence of <paramref name="needle"/> closest to <paramref name="preferred"/>,
    /// or -1 when there is none or when two are equally close.
    /// </summary>
    internal static int NearestOccurrence(string haystack, string needle, int preferred)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;

        var best = -1;
        var bestDistance = int.MaxValue;
        var tied = false;

        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = haystack.IndexOf(needle, index + 1, StringComparison.Ordinal))
        {
            var distance = Math.Abs(index - preferred);
            if (distance < bestDistance)
            {
                best = index;
                bestDistance = distance;
                tied = false;
            }
            else if (distance == bestDistance)
            {
                tied = true;
            }
        }

        return tied ? -1 : best;
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
