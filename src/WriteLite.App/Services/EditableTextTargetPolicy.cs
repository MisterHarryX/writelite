using System.Windows.Automation;

namespace WriteLite.Services;

/// <summary>
/// Conservative gate that prevents WriteLite from decorating ordinary page text.
/// </summary>
/// <remarks>
/// Discovery only. This decides which control WriteLite watches, and it is entitled to be
/// strict — it requires keyboard focus, because WriteLite decorates the field the user is
/// typing into and focus is what identifies it.
///
/// It is deliberately no longer consulted when deciding whether a correction may be written.
/// That question is <see cref="TextTargetCapabilityPolicy.Evaluate"/>'s, and asking this one
/// instead is what made WriteLite call a focused, editable browser field read-only the moment
/// its own popup took the keyboard.
/// </remarks>
public static class EditableTextTargetPolicy
{
    public static bool IsEditableTextTarget(AutomationElement element)
    {
        try
        {
            return TextTargetCapabilityPolicy.IsDiscoverableTarget(TextTargetCapabilities.Read(element));
        }
        catch (ElementNotAvailableException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// The discovery decision expressed over the evidence a caller already has.
    /// </summary>
    /// <remarks>
    /// <paramref name="supportsText"/> means only that a text provider is present. Whether it
    /// can select — the thing that decides whether text can be written through it — is not
    /// knowable from this signature, so it is treated as absent, and a control whose only
    /// evidence is a text provider is not discovered. Callers with a real element get the
    /// full answer from <see cref="TextTargetCapabilities.Read"/>.
    /// </remarks>
    public static bool IsEditableTextTarget(bool valueEditable, bool supportsText, bool isEditor, bool isEnabled,
        bool isKeyboardFocusable, bool hasKeyboardFocus, bool isOffscreen, bool isPassword)
        => TextTargetCapabilityPolicy.IsDiscoverableTarget(new TextTargetCapabilities(
            IsEnabled: isEnabled,
            IsOffscreen: isOffscreen,
            IsPassword: isPassword,
            IsKeyboardFocusable: isKeyboardFocusable,
            HasKeyboardFocus: hasKeyboardFocus,
            HasValuePattern: valueEditable,
            ValueIsReadOnly: false,
            HasTextPattern: supportsText,
            SupportsTextSelection: false,
            HasNativeEditWindow: false,
            IsTextControlType: isEditor));
}
