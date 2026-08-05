using System.Runtime.InteropServices;
using System.Windows.Automation;
using WriteLite.Services;

namespace WriteLite.Tests;

[TestClass]
public sealed class ApplicationExceptionPolicyTests
{
    [TestMethod]
    public void Cancellation_IsRecoverable()
    {
        Assert.AreEqual(
            ApplicationFailureDisposition.Recoverable,
            ApplicationExceptionPolicy.Classify(new TaskCanceledException(), shutdownRequested: false));
    }

    [TestMethod]
    public void AutomationFailures_AreRecoverable()
    {
        Assert.AreEqual(
            ApplicationFailureDisposition.Recoverable,
            ApplicationExceptionPolicy.Classify(new ElementNotAvailableException(), shutdownRequested: false));
        Assert.AreEqual(
            ApplicationFailureDisposition.Recoverable,
            ApplicationExceptionPolicy.Classify(new COMException(), shutdownRequested: false));
    }

    [TestMethod]
    public void ObjectDisposed_IsOnlyRecoverableDuringShutdown()
    {
        Assert.AreEqual(
            ApplicationFailureDisposition.RestartRecommended,
            ApplicationExceptionPolicy.Classify(new ObjectDisposedException("service"), shutdownRequested: false));
        Assert.AreEqual(
            ApplicationFailureDisposition.Recoverable,
            ApplicationExceptionPolicy.Classify(new ObjectDisposedException("service"), shutdownRequested: true));
    }

    [TestMethod]
    public void CorruptedProcessState_IsFatal()
    {
        Assert.AreEqual(
            ApplicationFailureDisposition.Fatal,
            ApplicationExceptionPolicy.Classify(new OutOfMemoryException(), shutdownRequested: false));
    }
}
