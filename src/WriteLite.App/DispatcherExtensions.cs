using System.Windows.Threading;

namespace WriteLite;

/// <summary>
/// One-line substitutes for the duplicated «if on the UI thread do it, otherwise marshal»
/// fork that used to open every public entry point of the popup windows and the editor.
/// </summary>
public static class DispatcherExtensions
{
    /// <summary>
    /// Runs <paramref name="action"/> when the caller is already on the dispatcher thread and
    /// returns false; otherwise enqueues it and returns true. The idiomatic call site is
    /// <c>if (Dispatcher.InvokeIfNeeded(() => Do(it))) return;</c>.
    /// </summary>
    public static bool InvokeIfNeeded(this Dispatcher dispatcher, Action action)
    {
        if (dispatcher.CheckAccess()) return false;
        dispatcher.BeginInvoke(action);
        return true;
    }

    /// <summary>Invokes <paramref name="action"/> inline on the dispatcher thread, blocking otherwise.</summary>
    public static T InvokeIfNeeded<T>(this Dispatcher dispatcher, Func<T> action)
        => dispatcher.CheckAccess() ? action() : dispatcher.Invoke(action);

    /// <summary>Runs <paramref name="action"/> inline on the dispatcher thread, enqueues it otherwise.</summary>
    public static void InvokeOrBegin(this Dispatcher dispatcher, Action action)
    {
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}