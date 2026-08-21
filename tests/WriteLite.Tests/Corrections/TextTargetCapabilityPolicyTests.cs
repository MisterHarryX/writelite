using WriteLite.Services;

namespace WriteLite.Tests.Corrections;

/// <summary>
/// §9 and §22: what counts as editable, what counts as read-only, and what counts as
/// "ask again later".
/// </summary>
[TestClass]
public sealed class TextTargetCapabilityPolicyTests
{
    [TestMethod]
    public void WritableValuePattern_IsEditable()
    {
        var capabilities = Base() with { HasValuePattern = true };
        Assert.AreEqual(EditabilityVerdict.Editable, TextTargetCapabilityPolicy.Evaluate(capabilities));
    }

    [TestMethod]
    public void EditableTargetWithoutValuePattern_IsNotReadOnly()
    {
        // The defect: Chromium inputs, Electron composers, WinUI boxes and WPF RichTextBox
        // all present a text provider with selection and no writable value.
        var capabilities = Base() with
        {
            HasValuePattern = false,
            HasTextPattern = true,
            SupportsTextSelection = true,
            IsTextControlType = true,
        };

        Assert.AreEqual(EditabilityVerdict.Editable, TextTargetCapabilityPolicy.Evaluate(capabilities));
        Assert.IsTrue(TextTargetCapabilityPolicy.HasAnyWriteRoute(capabilities));
    }

    [TestMethod]
    public void ReadOnlyValuePatternWithANativeEditWindow_IsStillEditable()
    {
        // A window whose class is an edit control answers to EM_REPLACESEL whatever the
        // automation provider claims about its value.
        var capabilities = Base() with
        {
            HasValuePattern = true,
            ValueIsReadOnly = true,
            HasNativeEditWindow = true,
        };

        Assert.AreEqual(EditabilityVerdict.Editable, TextTargetCapabilityPolicy.Evaluate(capabilities));
    }

    [TestMethod]
    public void TextWithNoWayToChangeIt_IsReadOnly()
    {
        // A sent message in a chat history, a rendered page, a label.
        var capabilities = Base() with
        {
            HasTextPattern = true,
            SupportsTextSelection = true,
            IsTextControlType = true,
            IsKeyboardFocusable = false,
        };

        Assert.AreEqual(EditabilityVerdict.ReadOnly, TextTargetCapabilityPolicy.Evaluate(capabilities));
    }

    [TestMethod]
    public void PasswordAndDisabledAndOffscreen_AreReadOnly()
    {
        var writable = Base() with { HasValuePattern = true };

        Assert.AreEqual(EditabilityVerdict.ReadOnly, TextTargetCapabilityPolicy.Evaluate(writable with { IsPassword = true }));
        Assert.AreEqual(EditabilityVerdict.ReadOnly, TextTargetCapabilityPolicy.Evaluate(writable with { IsEnabled = false }));
        Assert.AreEqual(EditabilityVerdict.ReadOnly, TextTargetCapabilityPolicy.Evaluate(writable with { IsOffscreen = true }));
    }

    [TestMethod]
    public void AControlThatAnswersNothing_IsUnavailableRatherThanReadOnly()
    {
        // §9: "temporarily unavailable" must never be reported to the user as read-only.
        Assert.AreEqual(
            EditabilityVerdict.Unavailable,
            TextTargetCapabilityPolicy.Evaluate(TextTargetCapabilities.Unknown));
    }

    [TestMethod]
    public void LosingKeyboardFocus_DoesNotMakeAFieldReadOnly()
    {
        // The exact sequence the popup used to cause: the user clicks «Исправить», the popup
        // takes the keyboard, and the field under it is suddenly reported read-only.
        var focused = Base() with { HasValuePattern = true, HasKeyboardFocus = true };
        var defocused = focused with { HasKeyboardFocus = false };

        Assert.AreEqual(EditabilityVerdict.Editable, TextTargetCapabilityPolicy.Evaluate(focused));
        Assert.AreEqual(EditabilityVerdict.Editable, TextTargetCapabilityPolicy.Evaluate(defocused));

        // Discovery still wants focus — that is how the watched field is identified.
        Assert.IsTrue(TextTargetCapabilityPolicy.IsDiscoverableTarget(focused));
        Assert.IsFalse(TextTargetCapabilityPolicy.IsDiscoverableTarget(defocused));
    }

    [TestMethod]
    public void StrategyPlan_FollowsTheControlAndNotTheApplication()
    {
        var writer = new Services.Writing.ExternalTextWriter(_ => Task.FromResult((true, string.Empty)));

        var win32 = Base() with { HasValuePattern = true, HasNativeEditWindow = true };
        CollectionAssert.AreEqual(
            new[] { "win32-edit", "value-pattern" },
            writer.PlanFor(win32).ToArray());

        var chromiumShape = Base() with
        {
            HasTextPattern = true,
            SupportsTextSelection = true,
            IsTextControlType = true,
        };
        CollectionAssert.AreEqual(new[] { "selection-paste" }, writer.PlanFor(chromiumShape).ToArray());

        Assert.AreEqual(0, writer.PlanFor(TextTargetCapabilities.Unknown).Count);
    }

    private static TextTargetCapabilities Base() => new(
        IsEnabled: true,
        IsOffscreen: false,
        IsPassword: false,
        IsKeyboardFocusable: true,
        HasKeyboardFocus: true,
        HasValuePattern: false,
        ValueIsReadOnly: false,
        HasTextPattern: false,
        SupportsTextSelection: false,
        HasNativeEditWindow: false,
        IsTextControlType: true);
}
