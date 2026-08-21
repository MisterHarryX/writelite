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

    /// <summary>
    /// The exit button carries a hover transition, so its animation clock is live at
    /// the moment the user clicks it. That clock throwing as the dispatcher tears down
    /// must not be treated as the application failing.
    /// </summary>
    [TestMethod]
    public void AnimationClock_IsOnlyRecoverableDuringShutdown()
    {
        // AnimationException has no public constructor — WPF only ever raises it from
        // inside the animation system — and the policy classifies purely on type.
        var animationFailure = (Exception)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(System.Windows.Media.Animation.AnimationException));

        Assert.AreEqual(
            ApplicationFailureDisposition.RestartRecommended,
            ApplicationExceptionPolicy.Classify(animationFailure, shutdownRequested: false));
        Assert.AreEqual(
            ApplicationFailureDisposition.Recoverable,
            ApplicationExceptionPolicy.Classify(animationFailure, shutdownRequested: true));
    }

    [TestMethod]
    public void CorruptedProcessState_IsFatal()
    {
        Assert.AreEqual(
            ApplicationFailureDisposition.Fatal,
            ApplicationExceptionPolicy.Classify(new OutOfMemoryException(), shutdownRequested: false));
    }
}
