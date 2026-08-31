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
    public void PasswordAndDisabled_AreReadOnly()
    {
        var writable = Base() with { HasValuePattern = true };

        Assert.AreEqual(EditabilityVerdict.ReadOnly, TextTargetCapabilityPolicy.Evaluate(writable with { IsPassword = true }));
        Assert.AreEqual(EditabilityVerdict.ReadOnly, TextTargetCapabilityPolicy.Evaluate(writable with { IsEnabled = false }));
    }

    [TestMethod]
    public void AControlReportedOffscreen_IsStillEditableAndStillDiscoverable()
    {
        // Discord's composer, measured: the field is on screen and being typed into, every
        // ancestor reports IsOffscreen=false, and the composer itself reports true because
        // Chromium marks the whole composer bar offscreen when its layout box runs past the
        // client area. Believing the flag dropped the field entirely — no underlines, no
        // lexical card, no corrections.
        var composer = Base() with
        {
            IsOffscreen = true,
            HasValuePattern = true,
            HasTextPattern = true,
            SupportsTextSelection = true,
        };

        Assert.AreEqual(EditabilityVerdict.Editable, TextTargetCapabilityPolicy.Evaluate(composer));
        Assert.IsTrue(TextTargetCapabilityPolicy.IsDiscoverableTarget(composer));
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

    [TestMethod]
    public void AChromiumComposer_TakesTheTargetedRouteBeforeTheWholeValueOne()
    {
        // The Electron composer: a writable value provider *and* a selectable text provider.
        // SetValue replaces the whole value, which left Slate's document model describing a
        // DOM that no longer existed — the composer kept the corrected text and stopped
        // honouring Backspace and Delete. The read-back could not see it, because the text
        // was right; only the editing behaviour was broken.
        var writer = new Services.Writing.ExternalTextWriter(_ => Task.FromResult((true, string.Empty)));

        var composer = Base() with
        {
            HasValuePattern = true,
            HasTextPattern = true,
            SupportsTextSelection = true,
            FrameworkId = "Chrome",
        };

        CollectionAssert.AreEqual(
            new[] { "selection-paste", "value-pattern" },
            writer.PlanFor(composer).ToArray());
    }

    [TestMethod]
    public void ANativeTextBoxWithTheSamePatterns_StillTakesTheCheapRouteFirst()
    {
        // A WPF TextBox offers exactly the patterns the composer above does, and for it the
        // value *is* the text: SetValue is both correct and four to forty times cheaper —
        // 28 ms against 241 ms measured through this path, 27 ms against 1 152 ms in a
        // browser field. Demoting the whole-value route for every control fixed Discord and
        // made every text box in Windows pay a clipboard round-trip, which
        // FieldMonitorApplyTests caught as p50 apply latency going from under 300 ms to
        // 384 ms. The framework is what separates the two, and it separates them by how the
        // control is built rather than by which application it belongs to.
        var writer = new Services.Writing.ExternalTextWriter(_ => Task.FromResult((true, string.Empty)));

        var textBox = Base() with
        {
            HasValuePattern = true,
            HasTextPattern = true,
            SupportsTextSelection = true,
            FrameworkId = "WPF",
        };

        CollectionAssert.AreEqual(
            new[] { "value-pattern", "selection-paste" },
            writer.PlanFor(textBox).ToArray());
    }

    [TestMethod]
    public void AChromiumFieldWithNothingSelectable_StillGetsTheWholeValueWrite()
    {
        // Demotion, not removal. For this control the whole-value write is the only route
        // there is, and a correction that cannot be applied at all is worse than one applied
        // bluntly.
        var writer = new Services.Writing.ExternalTextWriter(_ => Task.FromResult((true, string.Empty)));

        var field = Base() with { HasValuePattern = true, FrameworkId = "Chrome" };

        CollectionAssert.AreEqual(new[] { "value-pattern" }, writer.PlanFor(field).ToArray());
    }

    [TestMethod]
    public void AValueOnlyControl_GetsTheWholeValueWrite()
    {
        var writer = new Services.Writing.ExternalTextWriter(_ => Task.FromResult((true, string.Empty)));

        var valueOnly = Base() with { HasValuePattern = true };
        CollectionAssert.AreEqual(new[] { "value-pattern" }, writer.PlanFor(valueOnly).ToArray());
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
        IsTextControlType: true,
        FrameworkId: null);
}
