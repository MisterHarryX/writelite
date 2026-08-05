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

        if (exception is OutOfMemoryException or AccessViolationException or StackOverflowException)
        {
            return ApplicationFailureDisposition.Fatal;
        }

        return ApplicationFailureDisposition.RestartRecommended;
    }
}
