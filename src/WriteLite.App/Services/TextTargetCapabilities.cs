using System.Windows.Automation;

namespace WriteLite.Services;

/// <summary>What WriteLite concluded about whether a control can be written to.</summary>
public enum EditabilityVerdict
{
    /// <summary>The user can type here, and at least one write strategy applies.</summary>
    Editable,

    /// <summary>The control shows text the user cannot change. Nothing may be written.</summary>
    ReadOnly,

    /// <summary>
    /// The control could not be interrogated — it went away, its provider timed out, or the
    /// window changed underneath. Not a claim about the control, and never a reason to tell
    /// the user their field is read-only.
    /// </summary>
    Unavailable,
}

/// <summary>
/// Everything a control tells us about itself, read once.
/// </summary>
/// <remarks>
/// <para><b>Why a record rather than ten property reads at each decision.</b> Each field here
/// is a cross-process COM call. Reading them together, on a bounded background worker, is
/// what makes it affordable to base a decision on all of them rather than on whichever one
/// happened to be cheapest — which is how "does it have a writable ValuePattern" became the
/// definition of editable, and how every Chromium, Electron and WinUI text box came to be
/// reported as read-only.</para>
///
/// <para>The pure-data shape is also what makes the policy testable: the decisions in
/// <see cref="TextTargetCapabilityPolicy"/> take one of these and no automation element, so
/// every combination in §22 of the brief can be written down as a literal.</para>
/// </remarks>
public readonly record struct TextTargetCapabilities(
    bool IsEnabled,
    bool IsOffscreen,
    bool IsPassword,
    bool IsKeyboardFocusable,
    bool HasKeyboardFocus,
    bool HasValuePattern,
    bool ValueIsReadOnly,
    bool HasTextPattern,
    bool SupportsTextSelection,
    bool HasNativeEditWindow,
    bool IsTextControlType,
    string? FrameworkId = null)
{
    /// <summary>The unreadable control: every claim below is unknown rather than false.</summary>
    public static TextTargetCapabilities Unknown => default;

    /// <summary>ValuePattern is present and does not declare itself read-only.</summary>
    public bool HasWritableValuePattern => HasValuePattern && !ValueIsReadOnly;

    /// <summary>
    /// Whether this control's value is a rendering of a document it keeps somewhere else.
    /// </summary>
    /// <remarks>
    /// <para>The distinction matters for exactly one decision: whether replacing the whole
    /// value is a safe way to change part of it. For a native text box the value *is* the
    /// text, and handing it a new string is both correct and the cheapest write available.
    /// For a Chromium-hosted editor the value is a projection of a document model held by
    /// the page — Slate, Quill, ProseMirror — and <c>ValuePattern.SetValue</c> rewrites the
    /// projection while the model goes on describing what used to be there. The text reads
    /// back correctly, so the write verifies; the caret then has no valid position and
    /// Backspace and Delete stop removing what they point at. That was the reported Discord
    /// defect.</para>
    ///
    /// <para><b>Why the framework and not the application.</b> Naming applications would be
    /// the hack this codebase refuses. <c>FrameworkId</c> is not an application: every
    /// Chromium embedding — Chrome, Edge, Electron, CEF, and so every app built on any of
    /// them — reports <c>Chrome</c>, and reports it because of how the control is built,
    /// which is precisely the fact the decision turns on. An application not yet heard of,
    /// built on Electron tomorrow, is covered without being named.</para>
    ///
    /// <para>This does not decide whether a write happens, only what is tried first. A
    /// Chromium control with no selectable text still falls back to the whole-value write,
    /// because for that control it is the only route there is.</para>
    /// </remarks>
    public bool ValueIsProjectedFromDocumentModel =>
        string.Equals(FrameworkId, "Chrome", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a control's capabilities in one pass.</summary>
    public static TextTargetCapabilities Read(AutomationElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var current = element.Current;

        var hasValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject)
                       && valueObject is ValuePattern;
        var valueReadOnly = hasValue
                            && valueObject is ValuePattern valuePattern
                            && valuePattern.Current.IsReadOnly;

        var hasText = element.TryGetCurrentPattern(TextPattern.Pattern, out var textObject)
                      && textObject is TextPattern;
        var supportsSelection = hasText
                                && textObject is TextPattern textPattern
                                && textPattern.SupportedTextSelection != SupportedTextSelection.None;

        var controlType = current.ControlType;
        var isTextControl = controlType == ControlType.Edit
                            || controlType == ControlType.Document
                            || controlType == ControlType.ComboBox
                            || controlType == ControlType.Custom
                            || controlType == ControlType.Pane
                            || controlType == ControlType.Group;

        return new TextTargetCapabilities(
            IsEnabled: current.IsEnabled,
            IsOffscreen: current.IsOffscreen,
            IsPassword: current.IsPassword,
            IsKeyboardFocusable: current.IsKeyboardFocusable,
            HasKeyboardFocus: current.HasKeyboardFocus,
            HasValuePattern: hasValue,
            ValueIsReadOnly: valueReadOnly,
            HasTextPattern: hasText,
            SupportsTextSelection: supportsSelection,
            HasNativeEditWindow: Win32TextEdit.TryGetHwnd(element, out _),
            IsTextControlType: isTextControl,
            FrameworkId: SafeFrameworkId(current));
    }

    /// <summary>
    /// The control's UI framework, or null when the provider will not say.
    /// </summary>
    /// <remarks>
    /// Every other property here comes from a member that a disconnected element throws on,
    /// and the caller already handles that; this one is read separately because an absent
    /// framework id must degrade to "unknown" rather than lose the whole capability read.
    /// </remarks>
    private static string? SafeFrameworkId(AutomationElement.AutomationElementInformation current)
    {
        try
        {
            var id = current.FrameworkId;
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Decides, from capabilities alone, whether a control may be decorated and whether it may
/// be written to.
/// </summary>
/// <remarks>
/// <para>The two questions are deliberately separate, and answering them with one predicate
/// is what produced the «Поле только для чтения» defect on fields the user was actively
/// typing into.</para>
///
/// <para><b>Discovery</b> asks "is this the field the user is editing right now?" and is
/// entitled to require keyboard focus, because WriteLite decorates one field at a time and
/// focus is what identifies it.</para>
///
/// <para><b>Write</b> asks "may WriteLite change this control's text?", and keyboard focus is
/// not evidence either way. The popup itself takes focus when its button is clicked, so by
/// the time the question is asked the answer to "does the target have keyboard focus" is
/// routinely no — for a field that is perfectly editable and that the user is about to type
/// into again. Requiring focus here turned WriteLite's own popup into the reason WriteLite
/// could not write.</para>
/// </remarks>
public static class TextTargetCapabilityPolicy
{
    /// <summary>Whether this control is the editable field WriteLite should be watching.</summary>
    public static bool IsDiscoverableTarget(in TextTargetCapabilities capabilities)
        => capabilities.HasKeyboardFocus
           && capabilities.IsKeyboardFocusable
           && Evaluate(capabilities) == EditabilityVerdict.Editable;

    /// <summary>Whether this control's text may be changed, and how firmly that is known.</summary>
    /// <remarks>
    /// <para>Editable requires two things: the control must not be one of the categorically
    /// excluded kinds (disabled, offscreen, a password box), and there must be at least one
    /// route through which text could actually be written. The routes are the strategies in
    /// <see cref="Writing.ExternalTextWriter"/>, and they are the reason a control with no
    /// ValuePattern at all is still editable — a native edit window takes
    /// <c>EM_REPLACESEL</c>, and a text provider that supports selection takes a selected
    /// paste.</para>
    ///
    /// <para>Read-only is asserted only on positive evidence: the control exposes text and no
    /// way at all to change it. A control that answers nothing is
    /// <see cref="EditabilityVerdict.Unavailable"/>, not read-only.</para>
    ///
    /// <para><b>Why <c>IsOffscreen</c> is not consulted.</b> It answers "is this control
    /// currently rendered", which is not a claim about editability and is not even reliably
    /// a claim about visibility. Measured against Discord's composer: the field is on screen,
    /// the user is typing into it, every ancestor reports <c>IsOffscreen=false</c>, and the
    /// composer itself reports <c>true</c> — Chromium marks the whole composer bar offscreen
    /// whenever its layout box extends past the client area, which for a maximised window it
    /// routinely does. Treating that as read-only made WriteLite drop the field entirely: no
    /// underlines, no lexical card, no corrections, in the one application the flag happens
    /// to be wrong about. Geometry is checked where it belongs — the monitor rejects a
    /// candidate with no usable rectangle — and this decision is left to the facts that are
    /// actually about editing.</para>
    /// </remarks>
    public static EditabilityVerdict Evaluate(in TextTargetCapabilities capabilities)
    {
        if (capabilities is { HasTextPattern: false, HasValuePattern: false, HasNativeEditWindow: false })
        {
            return EditabilityVerdict.Unavailable;
        }

        if (!capabilities.IsEnabled || capabilities.IsPassword)
        {
            return EditabilityVerdict.ReadOnly;
        }

        if (HasAnyWriteRoute(capabilities))
        {
            return EditabilityVerdict.Editable;
        }

        // It exposes text and no route to change it. That is a genuine read-only control:
        // a message in a chat history, a rendered page, a label.
        return EditabilityVerdict.ReadOnly;
    }

    /// <summary>True when at least one write strategy could apply to this control.</summary>
    /// <remarks>
    /// A control that declares <c>ValuePattern.IsReadOnly</c> is taken at its word only when
    /// nothing else contradicts it. A native edit window does contradict it — the class is
    /// an edit control and <c>EM_REPLACESEL</c> reaches it — and so does a text provider that
    /// supports selection on a keyboard-focusable control, which is the shape every browser
    /// input and every Electron composer presents.
    /// </remarks>
    public static bool HasAnyWriteRoute(in TextTargetCapabilities capabilities)
    {
        if (capabilities.HasWritableValuePattern) return true;
        if (capabilities.HasNativeEditWindow) return true;

        // The Chromium / Electron / WinUI / WPF-RichTextBox shape: a text provider with
        // selection support on a control the user can tab into. ValuePattern is either
        // absent or read-only, and neither fact says the user cannot type here.
        return capabilities is
        {
            HasTextPattern: true,
            SupportsTextSelection: true,
            IsKeyboardFocusable: true,
            IsTextControlType: true,
            ValueIsReadOnly: false,
        };
    }
}
