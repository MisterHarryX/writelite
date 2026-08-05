using WriteLite.Services;

namespace WriteLite.Tests;

[TestClass]
public sealed class UiInteractionStateMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void FocusAloneDoesNotShowMainUi()
    {
        var sm = new UiInteractionStateMachine(TimeSpan.FromSeconds(8));
        sm.OnEditableFocused("field-1", textVersion: 1, T0);

        Assert.AreEqual(UiInteractionState.EditableFocused, sm.State);
        Assert.IsFalse(sm.ShouldShowMainUi);
    }

    [TestMethod]
    public void TextChangeShowsMainUiAsTyping()
    {
        var sm = new UiInteractionStateMachine(TimeSpan.FromSeconds(8));
        sm.OnEditableFocused("field-1", 1, T0);
        sm.OnTextChanged("field-1", 2, TextChangeSource.UserInput, T0.AddSeconds(1));

        Assert.AreEqual(UiInteractionState.Typing, sm.State);
        Assert.IsTrue(sm.ShouldShowMainUi);
    }

    [TestMethod]
    public void IdleAfterGraceKeepsCompletedCheckVisible()
    {
        var grace = TimeSpan.FromSeconds(3);
        var sm = new UiInteractionStateMachine(grace);
        sm.OnEditableFocused("field-1", 1, T0);
        sm.OnTextChanged("field-1", 2, TextChangeSource.AutomationTextChange, T0);

        sm.OnIdleTick(T0.AddSeconds(1));
        Assert.AreEqual(UiInteractionState.IdleEditing, sm.State);
        Assert.IsTrue(sm.ShouldShowMainUi);

        sm.OnIdleTick(T0 + grace + TimeSpan.FromMilliseconds(50));
        Assert.AreEqual(UiInteractionState.IdleEditing, sm.State);
        Assert.IsTrue(sm.ShouldShowMainUi);
    }

    [TestMethod]
    public void DefaultIdleGraceIsFourSeconds()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(4), UiInteractionStateMachine.DefaultIdleGrace);
        var sm = new UiInteractionStateMachine();
        Assert.AreEqual(TimeSpan.FromSeconds(4), sm.IdleGrace);
    }

    [TestMethod]
    public void UnsupportedTargetHidesImmediatelyWithoutDismissingPopupFlag()
    {
        var sm = new UiInteractionStateMachine();
        sm.OnEditableFocused("field-1", 1, T0);
        sm.OnTextChanged("field-1", 2, TextChangeSource.UserInput, T0);
        sm.OnUnsupportedOrReadOnlyTarget(T0.AddSeconds(1));
        Assert.AreEqual(UiInteractionState.Hidden, sm.State);
        Assert.IsFalse(sm.ShouldShowMainUi);
    }

    [TestMethod]
    public void PopupStaysOpenDuringInteractionWhileMainMayHide()
    {
        var grace = TimeSpan.FromSeconds(2);
        var sm = new UiInteractionStateMachine(grace);
        sm.OnEditableFocused("field-1", 1, T0);
        sm.OnTextChanged("field-1", 2, TextChangeSource.UserInput, T0);
        sm.OnPopupOpened(T0.AddSeconds(0.5));

        Assert.AreEqual(UiInteractionState.PopupInteraction, sm.State);
        Assert.IsTrue(sm.ShouldKeepPopup);
        Assert.IsTrue(sm.ShouldShowMainUi);

        sm.OnIdleTick(T0 + grace + TimeSpan.FromSeconds(5));
        Assert.AreEqual(UiInteractionState.PopupInteraction, sm.State);
        Assert.IsTrue(sm.ShouldKeepPopup);

        sm.OnPopupClosed(T0 + grace + TimeSpan.FromSeconds(6));
        Assert.AreEqual(UiInteractionState.IdleEditing, sm.State);
        Assert.IsFalse(sm.ShouldKeepPopup);
        Assert.IsTrue(sm.ShouldShowMainUi);
    }

    [TestMethod]
    public void CorrectionApplyCountsAsEditing()
    {
        var sm = new UiInteractionStateMachine();
        sm.OnEditableFocused("field-1", 1, T0);
        sm.OnCorrectionApplied("field-1", 2, T0.AddMilliseconds(100));

        Assert.AreEqual(UiInteractionState.Typing, sm.State);
        Assert.IsTrue(sm.ShouldShowMainUi);
    }

    [TestMethod]
    public void TargetLostClearsEditingSession()
    {
        var sm = new UiInteractionStateMachine();
        sm.OnEditableFocused("field-1", 1, T0);
        sm.OnTextChanged("field-1", 2, TextChangeSource.UserInput, T0);
        sm.OnTargetLost(T0.AddSeconds(1));

        Assert.AreEqual(UiInteractionState.NoTarget, sm.State);
        Assert.IsFalse(sm.ShouldShowMainUi);
    }

    [TestMethod]
    public void SwitchingTargetsDoesNotInheritTypingVisibility()
    {
        var sm = new UiInteractionStateMachine();
        sm.OnEditableFocused("field-1", 1, T0);
        sm.OnTextChanged("field-1", 2, TextChangeSource.UserInput, T0);
        Assert.IsTrue(sm.ShouldShowMainUi);

        sm.OnEditableFocused("field-2", 1, T0.AddSeconds(1));
        Assert.AreEqual(UiInteractionState.EditableFocused, sm.State);
        Assert.IsFalse(sm.ShouldShowMainUi);
    }

    [TestMethod]
    public void StaleTextVersionIsIgnored()
    {
        var sm = new UiInteractionStateMachine();
        sm.OnEditableFocused("field-1", 5, T0);
        sm.OnTextChanged("field-1", 6, TextChangeSource.UserInput, T0);
        var state = sm.OnTextChanged("field-1", 4, TextChangeSource.UserInput, T0.AddSeconds(1));

        Assert.AreEqual(UiInteractionState.Typing, state);
        Assert.AreEqual(6, sm.TextVersion);
    }

    [TestMethod]
    public void ReadingStaticFocusNeverOpensUi()
    {
        var sm = new UiInteractionStateMachine();
        // Simulate: user focuses message list / read-only area repeatedly — only EditableFocused if policy allows.
        sm.OnEditableFocused("composer", 1, T0);
        sm.OnIdleTick(T0.AddSeconds(10));
        sm.OnEditableFocused("composer", 1, T0.AddSeconds(11));

        Assert.IsFalse(sm.ShouldShowMainUi);
        Assert.AreEqual(UiInteractionState.EditableFocused, sm.State);
    }

    [TestMethod]
    public void PopupCloseWithinGraceReturnsToIdleEditing()
    {
        var sm = new UiInteractionStateMachine(TimeSpan.FromSeconds(8));
        sm.OnEditableFocused("field-1", 1, T0);
        sm.OnTextChanged("field-1", 2, TextChangeSource.ClipboardEdit, T0);
        sm.OnPopupOpened(T0.AddSeconds(1));
        sm.OnPopupClosed(T0.AddSeconds(2));

        Assert.AreEqual(UiInteractionState.IdleEditing, sm.State);
        Assert.IsTrue(sm.ShouldShowMainUi);
    }
}
