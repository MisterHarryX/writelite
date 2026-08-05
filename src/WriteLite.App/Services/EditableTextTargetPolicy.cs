using System.Windows.Automation;

namespace WriteLite.Services;

/// <summary>Conservative gate that prevents WriteLite from decorating ordinary page text.</summary>
public static class EditableTextTargetPolicy
{
    public static bool IsEditableTextTarget(AutomationElement element)
    {
        try
        {
            var valueEditable = element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
                && value is ValuePattern valuePattern && !valuePattern.Current.IsReadOnly;
            var isEditor = element.Current.ControlType == ControlType.Edit || element.Current.ControlType == ControlType.Document;
            var supportsText = element.TryGetCurrentPattern(TextPattern.Pattern, out _);
            return IsEditableTextTarget(valueEditable, supportsText, isEditor, element.Current.IsEnabled,
                element.Current.IsKeyboardFocusable, element.Current.HasKeyboardFocus, element.Current.IsOffscreen, element.Current.IsPassword);
        }
        catch (ElementNotAvailableException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public static bool IsEditableTextTarget(bool valueEditable, bool supportsText, bool isEditor, bool isEnabled,
        bool isKeyboardFocusable, bool hasKeyboardFocus, bool isOffscreen, bool isPassword)
        => isEnabled && !isOffscreen && !isPassword && hasKeyboardFocus && isKeyboardFocusable
           // TextPattern by itself is read-only. It can support analysis, but it
           // is never sufficient evidence that a correction may be written.
           && valueEditable;
}
