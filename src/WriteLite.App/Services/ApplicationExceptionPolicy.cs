using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WriteLite.Services;

public enum ApplicationFailureDisposition
{
    Recoverable,
    RestartRecommended,
    Fatal
}

public static class ApplicationExceptionPolicy
{
    public static ApplicationFailureDisposition Classify(Exception exception, bool shutdownRequested)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException or TaskCanceledException)
        {
            return ApplicationFailureDisposition.Recoverable;
        }

        if (exception is ElementNotAvailableException or COMException)
        {
            return ApplicationFailureDisposition.Recoverable;
        }

        if (shutdownRequested && exception is ObjectDisposedException or InvalidOperationException)
        {
            return ApplicationFailureDisposition.Recoverable;
        }

        // A control's hover transition is running at the exact moment the user clicks
        // "Завершить WriteLite" — the pointer is on the button. If the dispatcher tears
        // down the animation clocks first, the clock throws on its next tick. Nothing is
        // wrong with the application at that point: it is already leaving.
        if (shutdownRequested && exception is System.Windows.Media.Animation.AnimationException)
        {
            return ApplicationFailureDisposition.Recoverable;
        }

        if (exception is OutOfMemoryException or AccessViolationException or StackOverflowException)
        {
            return ApplicationFailureDisposition.Fatal;
        }

        return ApplicationFailureDisposition.RestartRecommended;
    }
}
