using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Threading;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace WriteLite.Services.Lexical;

public sealed class WordDoubleClickedEventArgs : EventArgs
{
    public required WordRange Range { get; init; }
    public required string FullText { get; init; }
    public required AutomationElement Element { get; init; }
    public required string TargetId { get; init; }
    public required int GenerationId { get; init; }
    public required long TextVersion { get; init; }
    public required Rect Anchor { get; init; }
    public required long RequestId { get; init; }
    public bool SupportsDirectWrite { get; init; }
}

/// <summary>
/// Observes system double-clicks and resolves the word under the pointer in editable fields.
/// Does not make the WriteLite overlay full-surface clickable — uses a low-level mouse hook
/// and UIA after the host app has selected the word.
/// </summary>
public sealed class DoubleClickWordObserver : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmLButtonDown = 0x0201;
    private static readonly TimeSpan SelectionSettle = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(450);

    private readonly Dispatcher _dispatcher;
    private readonly IWordRangeResolver _rangeResolver;
    private readonly Func<AutomationElement?> _getFocusedElement;
    private readonly Func<(string? TargetId, int GenerationId, long TextVersion, string? LastText)> _getTargetState;
    private readonly int _ownProcessId = Environment.ProcessId;

    private IntPtr _hook;
    private LowLevelMouseProc? _proc;
    private CancellationTokenSource? _pendingCts;
    private DateTimeOffset _lastDownUtc = DateTimeOffset.MinValue;
    private Point _lastDownPoint;
    private long _requestSeq;
    private bool _disposed;

    public DoubleClickWordObserver(
        Dispatcher dispatcher,
        IWordRangeResolver rangeResolver,
        Func<AutomationElement?> getFocusedElement,
        Func<(string? TargetId, int GenerationId, long TextVersion, string? LastText)> getTargetState)
    {
        _dispatcher = dispatcher;
        _rangeResolver = rangeResolver;
        _getFocusedElement = getFocusedElement;
        _getTargetState = getTargetState;
    }

    public event EventHandler<WordDoubleClickedEventArgs>? WordDoubleClicked;

    /// <summary>
    /// Raised, on the dispatcher, for every primary-button press anywhere on the desktop,
    /// with the screen point in physical pixels.
    /// </summary>
    /// <remarks>
    /// <para><b>Why dismissal is a click and not an inference.</b> The lexical card used to
    /// close when a field snapshot stopped matching the one it was opened for. That is a
    /// proxy for "the user has moved on", and it is a bad one: the monitor republishes its
    /// snapshot whenever the interaction state changes and restamps it with the live text
    /// version, and it publishes a null snapshot whenever the tracked field changes — none of
    /// which the user did, and all of which made the card vanish on its own. The click that
    /// actually dismisses a popup is a fact this hook already has.</para>
    ///
    /// <para>The press is reported rather than the release, so the card is gone before the
    /// host application acts on the click. Both clicks of a double-click are reported; the
    /// first lands outside whatever card is open and closes it, and the second opens the new
    /// one about 90 ms later, so a double-click on a second word reads as a replacement
    /// rather than as a flicker.</para>
    /// </remarks>
    public event EventHandler<Point>? PrimaryButtonPressed;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = HookCallback;
        _hook = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            CompatibilityLogger.Technical("lexical-dblclick-hook-failed", "code=SetWindowsHookEx");
        }
        else
        {
            CompatibilityLogger.Technical("lexical-dblclick-hook-started", "ok=1");
        }
    }

    public void Stop()
    {
        _pendingCts?.Cancel();
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            if (msg is WmLButtonDblClk or WmLButtonDown)
            {
                var info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                var point = new Point(info.Pt.X, info.Pt.Y);
                if (msg == WmLButtonDown)
                {
                    RaisePrimaryButtonPressed(point);

                    var now = DateTimeOffset.UtcNow;
                    // Synthetic double-click detection (some apps swallow WM_LBUTTONDBLCLK).
                    if (now - _lastDownUtc <= DoubleClickWindow
                        && Math.Abs(point.X - _lastDownPoint.X) <= 6
                        && Math.Abs(point.Y - _lastDownPoint.Y) <= 6)
                    {
                        ScheduleResolve(point);
                    }

                    _lastDownUtc = now;
                    _lastDownPoint = point;
                }
                else
                {
                    ScheduleResolve(point);
                }
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Hands the press to the dispatcher without letting a subscriber run inside the hook.
    /// </summary>
    /// <remarks>
    /// This callback is on the low-level mouse hook's thread and inside the system's
    /// <c>LowLevelHooksTimeout</c> budget for the whole desktop: work done here delays every
    /// application's mouse input, and exceeding the budget makes Windows silently remove the
    /// hook, which would end double-click detection for the rest of the session. So the
    /// point is posted and the hook returns.
    /// </remarks>
    private void RaisePrimaryButtonPressed(Point screenPoint)
    {
        if (PrimaryButtonPressed is null) return;

        _ = _dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => PrimaryButtonPressed?.Invoke(this, screenPoint)));
    }

    private void ScheduleResolve(Point screenPoint)
    {
        _pendingCts?.Cancel();
        _pendingCts?.Dispose();
        _pendingCts = new CancellationTokenSource();
        var token = _pendingCts.Token;
        var requestId = Interlocked.Increment(ref _requestSeq);

        _ = _dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(SelectionSettle, token).ConfigureAwait(true);
                if (token.IsCancellationRequested) return;
                await ResolveAsync(screenPoint, requestId, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // superseded double-click
            }
            catch (Exception ex)
            {
                CompatibilityLogger.AccessError("lexical-dblclick", null, ex);
            }
        }, DispatcherPriority.Input);
    }

    /// <summary>
    /// Upper bound for the whole cross-process UIA conversation of one resolve.
    /// A large document in a slow host (Chromium contenteditable) made the
    /// unbounded version run for seconds; past this point the double-click is
    /// simply dropped — a missing lexical card is a non-event, a frozen UI is
    /// not.
    /// </summary>
    private static readonly TimeSpan UiaResolveTimeout = TimeSpan.FromMilliseconds(700);

    private async Task ResolveAsync(Point screenPoint, long requestId, CancellationToken token)
    {
        // The resolve is 30+ synchronous cross-process COM calls, including a
        // full DocumentRange.GetText(-1) of the foreign document. It used to
        // resume on the dispatcher after the first await (ConfigureAwait(true)
        // in ScheduleResolve), which parked the UI for the whole conversation.
        // Now the entire body runs on a worker; only the event is raised back
        // here, because its subscriber shows the lexical popup.
        WordDoubleClickedEventArgs? args;
        try
        {
            args = await Task.Run(() => ResolveCore(screenPoint, requestId, token), token)
                .WaitAsync(UiaResolveTimeout, token)
                .ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            CompatibilityLogger.Technical("lexical-dblclick-ignored", "reason=uia-timeout");
            return;
        }

        if (args is null || token.IsCancellationRequested) return;

        WordDoubleClicked?.Invoke(this, args);

        CompatibilityLogger.Technical(
            "lexical-dblclick-resolved",
            $"request={requestId} len={args.Range.Length} generation={args.GenerationId}");
    }

    /// <summary>
    /// The editable field the user double-clicked in, given the point they clicked.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the hit element is a starting point and never the answer.</b>
    /// <c>AutomationElement.FromPoint</c> returns the deepest node under the cursor, and in
    /// every document-model host that node is a fragment of the field's interior rather than
    /// the field. Measured over Discord's composer, a point in the middle of the text
    /// resolves to an anonymous <c>Group</c> with no text provider, no value provider and
    /// <c>IsKeyboardFocusable=false</c> — a Slate wrapper div. Asking that node whether it is
    /// an editable target and giving up when it says no is what produced 409 consecutive
    /// <c>reason=not-editable</c> rejections in the log against three successful lookups: the
    /// walk that was meant to handle exactly this case sat below an early return and never
    /// ran.</para>
    ///
    /// <para>So the walk comes first, and the hit node's own verdict is just its first
    /// iteration. When the whole chain refuses — a host whose hit-testing does not reach the
    /// composer at all — the field the monitor is already tracking is used instead, but only
    /// if the click landed inside it. That keeps the fallback from opening a card for a field
    /// on the other side of the screen.</para>
    /// </remarks>
    private AutomationElement? ResolveEditableUnderPoint(Point screenPoint, CancellationToken token)
    {
        AutomationElement? hit;
        try
        {
            hit = AutomationElement.FromPoint(new System.Windows.Point(screenPoint.X, screenPoint.Y));
        }
        catch
        {
            hit = null;
        }

        var candidate = hit;
        for (var depth = 0; depth < 8 && candidate is not null && !token.IsCancellationRequested; depth++)
        {
            // The full capability read is a dozen cross-process calls and this loop runs
            // inside a 700 ms budget for the whole conversation. Keyboard focus is one call
            // and is necessary for the answer to be yes, so it filters the interior nodes —
            // which is all of them but one — before anything expensive is asked.
            if (HasKeyboardFocus(candidate) && EditableTextTargetPolicy.IsEditableTextTarget(candidate))
            {
                return candidate;
            }

            try { candidate = TreeWalker.ControlViewWalker.GetParent(candidate); }
            catch { break; }
        }

        var focused = _getFocusedElement();
        if (focused is null)
        {
            CompatibilityLogger.Technical("lexical-dblclick-ignored", "reason=not-editable");
            return null;
        }

        if (!EditableTextTargetPolicy.IsEditableTextTarget(focused))
        {
            CompatibilityLogger.Technical("lexical-dblclick-ignored", "reason=not-editable");
            return null;
        }

        if (!ContainsPoint(focused, screenPoint))
        {
            CompatibilityLogger.Technical("lexical-dblclick-ignored", "reason=click-outside-target");
            return null;
        }

        CompatibilityLogger.Technical("lexical-dblclick-fallback", "source=focused-target");
        return focused;
    }

    private static bool HasKeyboardFocus(AutomationElement element)
    {
        try { return element.Current.HasKeyboardFocus; }
        catch { return false; }
    }

    private static bool ContainsPoint(AutomationElement element, Point screenPoint)
    {
        try
        {
            var bounds = element.Current.BoundingRectangle;
            return !bounds.IsEmpty && bounds.Contains(screenPoint);
        }
        catch
        {
            return false;
        }
    }

    private WordDoubleClickedEventArgs? ResolveCore(Point screenPoint, long requestId, CancellationToken token)
    {
        var element = ResolveEditableUnderPoint(screenPoint, token);
        if (element is null || token.IsCancellationRequested) return null;

        int processId;
        try { processId = element.Current.ProcessId; }
        catch { return null; }

        if (processId == _ownProcessId) return null;

        string text;
        WordRange? range;
        Rect anchor;
        bool supportsWrite;
        try
        {
            supportsWrite = element.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)
                            && vp is ValuePattern valuePattern && !valuePattern.Current.IsReadOnly;

            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var tp) && tp is TextPattern textPattern)
            {
                text = textPattern.DocumentRange.GetText(-1) ?? string.Empty;
                var selection = textPattern.GetSelection();
                if (selection is { Length: > 0 })
                {
                    var sel = selection[0];
                    var selText = sel.GetText(-1) ?? string.Empty;
                    // Map selection to document offsets via compare endpoints when possible.
                    range = ResolveSelectionRange(textPattern, sel, text, selText);
                    var rects = sel.GetBoundingRectangles();
                    anchor = RectsToAnchor(rects, element.Current.BoundingRectangle);
                }
                else
                {
                    // Fallback: RangeFromPoint
                    try
                    {
                        var pointRange = textPattern.RangeFromPoint(new System.Windows.Point(screenPoint.X, screenPoint.Y));
                        var wordRange = pointRange.Clone();
                        ExpandToWord(wordRange);
                        var wordText = wordRange.GetText(-1) ?? string.Empty;
                        range = _rangeResolver.ResolveFromText(text, ApproximateIndex(text, wordText));
                        var rects = wordRange.GetBoundingRectangles();
                        anchor = RectsToAnchor(rects, element.Current.BoundingRectangle);
                    }
                    catch
                    {
                        range = null;
                        anchor = element.Current.BoundingRectangle;
                    }
                }
            }
            else if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var onlyValue) && onlyValue is ValuePattern valueOnly)
            {
                text = valueOnly.Current.Value ?? string.Empty;
                // Without TextPattern we cannot map screen point → index reliably; skip.
                CompatibilityLogger.Technical("lexical-dblclick-ignored", "reason=no-text-pattern");
                return null;
            }
            else
            {
                return null;
            }
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }

        if (range is null || string.IsNullOrWhiteSpace(range.Word)) return null;

        var state = _getTargetState();
        // Prefer live text over cached when available.
        if (!string.IsNullOrEmpty(state.LastText)
            && !string.Equals(state.LastText, text, StringComparison.Ordinal)
            && state.LastText.Length > 0)
        {
            // Re-resolve against monitor text if same word still present at range.
            var re = _rangeResolver.ResolveFromText(state.LastText, range.Start);
            if (re is not null && string.Equals(re.Word, range.Word, StringComparison.Ordinal))
            {
                text = state.LastText;
                range = re;
            }
        }

        string runtimeId;
        try
        {
            runtimeId = string.Join(".", element.GetRuntimeId());
        }
        catch
        {
            runtimeId = state.TargetId ?? processId.ToString();
        }

        return new WordDoubleClickedEventArgs
        {
            Range = range,
            FullText = text,
            Element = element,
            TargetId = runtimeId,
            GenerationId = state.GenerationId,
            TextVersion = state.TextVersion,
            Anchor = anchor,
            RequestId = requestId,
            SupportsDirectWrite = supportsWrite
        };
    }

    private WordRange? ResolveSelectionRange(TextPattern document, TextPatternRange selection, string fullText, string selText)
    {
        var trimmed = selText.Trim();
        if (trimmed.Length == 0) return null;

        // Measure the selected range from the document start. This remains correct
        // when the same word occurs several times; IndexOf alone always picked the
        // first occurrence and could open a card for the wrong word.
        var idx = -1;
        try
        {
            var prefix = document.DocumentRange.Clone();
            prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
            idx = (prefix.GetText(-1) ?? string.Empty).Length;
        }
        catch
        {
            // Fall through to the provider-independent text search.
        }

        if (idx < 0 || idx > fullText.Length)
            idx = fullText.IndexOf(selText, StringComparison.Ordinal);
        if (idx < 0) idx = fullText.IndexOf(trimmed, StringComparison.Ordinal);
        if (idx < 0) return _rangeResolver.ResolveFromText(fullText, Math.Min(fullText.Length, fullText.Length / 2));

        return _rangeResolver.ResolveFromSelection(fullText, idx, Math.Min(selText.Length, fullText.Length - idx))
               ?? _rangeResolver.ResolveFromText(fullText, idx);
    }

    private static void ExpandToWord(TextPatternRange range)
    {
        try
        {
            range.ExpandToEnclosingUnit(TextUnit.Word);
        }
        catch
        {
            // some providers throw — keep original range
        }
    }

    private static int ApproximateIndex(string fullText, string word)
    {
        if (string.IsNullOrEmpty(word)) return 0;
        var idx = fullText.IndexOf(word, StringComparison.Ordinal);
        return idx >= 0 ? idx : 0;
    }

    private static Rect RectsToAnchor(Rect[] rects, Rect fallback)
    {
        if (rects is null || rects.Length == 0) return fallback;
        var r = rects[0];
        if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return fallback;
        return new Rect(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _pendingCts?.Dispose();
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointApi
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public PointApi Pt;
        public int MouseData;
        public int Flags;
        public int Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
