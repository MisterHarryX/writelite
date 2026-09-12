using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace WriteLite;

/// <summary>Shared shape for the replicated «Enter commits a field» key handlers.</summary>
public static class KeyboardExtensions
{
    /// <summary>
    /// True when Enter was the key: the event is marked handled and the caller proceeds
    /// with the commit action. False (and untouched) for every other key.
    /// </summary>
    public static bool SubmitPressed(this KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return false;
        e.Handled = true;
        return true;
    }
}