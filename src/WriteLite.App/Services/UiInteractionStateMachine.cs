namespace WriteLite.Services;

/// <summary>
/// Explicit UI visibility state machine.
/// Focus alone never proves typing; confirmed text change (or a WriteLite apply) does.
/// Pure logic — safe for unit tests without WPF/UIA.
/// </summary>
public enum UiInteractionState
{
    NoTarget,
    EditableFocused,
    Typing,
    IdleEditing,
    PopupInteraction,
    Hidden
}

public enum TextChangeSource
{
    /// <summary>UIA poll / TextPattern / ValuePattern observed a content delta.</summary>
    AutomationTextChange,
    /// <summary>Keyboard / IME / dictation signal that precedes or accompanies a text delta.</summary>
    UserInput,
    /// <summary>WriteLite applied a correction into the field.</summary>
    CorrectionApply,
    /// <summary>Paste / cut detected via content delta (treated as user editing).</summary>
    ClipboardEdit
}

public sealed class UiInteractionStateMachine
{
    /// <summary>How long a confirmed edit is considered actively typing before becoming idle.</summary>
    public static readonly TimeSpan DefaultIdleGrace = WriteLiteDefaults.UiStateMachine.DefaultIdleGrace;

    /// <summary>Minimum idle grace to avoid flicker on brief pauses between keystrokes.</summary>
    public static readonly TimeSpan MinIdleGrace = WriteLiteDefaults.UiStateMachine.MinIdleGrace;

    /// <summary>Maximum idle grace (settings upper bound).</summary>
    public static readonly TimeSpan MaxIdleGrace = WriteLiteDefaults.UiStateMachine.MaxIdleGrace;

    /// <summary>Debounce rapid focus flicker before treating the target as fully lost.</summary>
    public static readonly TimeSpan DefaultFocusGrace = WriteLiteDefaults.UiStateMachine.DefaultFocusGrace;

    private TimeSpan _idleGrace;
    private string? _targetId;
    private long _textVersion;
    private DateTimeOffset _lastConfirmedEditAt = DateTimeOffset.MinValue;
    private bool _popupOpen;

    public UiInteractionStateMachine(TimeSpan? idleGrace = null)
    {
        _idleGrace = ClampIdleGrace(idleGrace ?? DefaultIdleGrace);
    }

    public TimeSpan IdleGrace => _idleGrace;

    public void SetIdleGrace(TimeSpan idleGrace) => _idleGrace = ClampIdleGrace(idleGrace);

    public static TimeSpan ClampIdleGrace(TimeSpan value)
    {
        if (value < MinIdleGrace) return MinIdleGrace;
        if (value > MaxIdleGrace) return MaxIdleGrace;
        return value;
    }

    public static TimeSpan FromSecondsClamped(double seconds)
        => ClampIdleGrace(TimeSpan.FromSeconds(seconds));

    public UiInteractionState State { get; private set; } = UiInteractionState.NoTarget;

    public string? TargetId => _targetId;

    public long TextVersion => _textVersion;

    public DateTimeOffset LastConfirmedEditAt => _lastConfirmedEditAt;

    /// <summary>Indicator, underlines, and suggestions panel may be shown.</summary>
    public bool ShouldShowMainUi => State is UiInteractionState.Typing
        or UiInteractionState.IdleEditing
        or UiInteractionState.PopupInteraction;

    /// <summary>Correction / lexical popups stay open while the user interacts with them.</summary>
    public bool ShouldKeepPopup => _popupOpen || State == UiInteractionState.PopupInteraction;

    public UiInteractionState OnTargetLost(DateTimeOffset? now = null)
    {
        _ = now;
        _targetId = null;
        _textVersion = 0;
        // Leaving editable context: hide main chrome immediately. Active popup stays until closed.
        if (_popupOpen)
        {
            State = UiInteractionState.PopupInteraction;
            return State;
        }

        State = UiInteractionState.NoTarget;
        return State;
    }

    /// <summary>
    /// Immediate hide when focus moves to an unsupported / read-only / password target.
    /// Does not dismiss an open correction/lexical popup the user is interacting with.
    /// </summary>
    public UiInteractionState OnUnsupportedOrReadOnlyTarget(DateTimeOffset? now = null)
    {
        _ = now;
        _targetId = null;
        _textVersion = 0;
        _lastConfirmedEditAt = DateTimeOffset.MinValue;
        if (_popupOpen)
        {
            State = UiInteractionState.PopupInteraction;
            return State;
        }

        State = UiInteractionState.Hidden;
        return State;
    }

    /// <summary>
    /// Editable field gained focus. Does not show main UI until a confirmed text change.
    /// </summary>
    public UiInteractionState OnEditableFocused(string targetId, long textVersion, DateTimeOffset? now = null)
    {
        _ = now;
        if (string.IsNullOrEmpty(targetId))
        {
            return OnTargetLost();
        }

        var sameTarget = string.Equals(_targetId, targetId, StringComparison.Ordinal);
        _targetId = targetId;

        if (!sameTarget)
        {
            _textVersion = textVersion;
            // Fresh focus on a different field: never inherit prior typing visibility.
            if (_popupOpen)
            {
                State = UiInteractionState.PopupInteraction;
            }
            else
            {
                State = UiInteractionState.EditableFocused;
            }

            return State;
        }

        // Same target re-focused: keep editing visibility if we were still in an editing session.
        if (State is UiInteractionState.Typing or UiInteractionState.IdleEditing or UiInteractionState.PopupInteraction)
        {
            return State;
        }

        _textVersion = textVersion;
        State = _popupOpen ? UiInteractionState.PopupInteraction : UiInteractionState.EditableFocused;
        return State;
    }

    /// <summary>
    /// Confirmed text change for the active field (poll delta, UIA event, apply, paste).
    /// </summary>
    public UiInteractionState OnTextChanged(
        string targetId,
        long textVersion,
        TextChangeSource source,
        DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.UtcNow;
        if (string.IsNullOrEmpty(targetId))
        {
            return OnTargetLost(stamp);
        }

        _targetId = targetId;
        if (textVersion < _textVersion)
        {
            // Stale observation — ignore.
            return State;
        }

        _textVersion = textVersion;
        _lastConfirmedEditAt = stamp;

        if (_popupOpen)
        {
            State = UiInteractionState.PopupInteraction;
        }
        else
        {
            State = UiInteractionState.Typing;
        }

        _ = source; // retained for telemetry/call-site clarity; all listed sources are user-linked.
        return State;
    }

    public UiInteractionState OnPopupOpened(DateTimeOffset? now = null)
    {
        _ = now;
        _popupOpen = true;
        if (State is not UiInteractionState.NoTarget)
        {
            State = UiInteractionState.PopupInteraction;
        }

        return State;
    }

    public UiInteractionState OnPopupClosed(DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.UtcNow;
        _popupOpen = false;
        if (State == UiInteractionState.NoTarget || _targetId is null)
        {
            State = UiInteractionState.NoTarget;
            return State;
        }

        // A completed check remains visible for the active edited field. This keeps
        // both underlines and the final green check stable until focus truly leaves.
        _ = stamp;
        State = UiInteractionState.IdleEditing;

        return State;
    }

    /// <summary>
    /// Called by a timer while focus remains on an editable field and no new edits arrive.
    /// </summary>
    public UiInteractionState OnIdleTick(DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.UtcNow;
        if (State is UiInteractionState.NoTarget or UiInteractionState.EditableFocused or UiInteractionState.Hidden)
        {
            return State;
        }

        if (_popupOpen)
        {
            State = UiInteractionState.PopupInteraction;
            return State;
        }

        if (State == UiInteractionState.Typing)
        {
            State = UiInteractionState.IdleEditing;
        }

        return State;
    }

    public UiInteractionState OnCorrectionApplied(string targetId, long textVersion, DateTimeOffset? now = null)
        => OnTextChanged(targetId, textVersion, TextChangeSource.CorrectionApply, now);

    public bool IsWithinIdleGrace(DateTimeOffset now)
        => _lastConfirmedEditAt > DateTimeOffset.MinValue
           && now - _lastConfirmedEditAt <= _idleGrace;
}
