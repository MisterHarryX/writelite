using System.Windows.Input;
using WriteLite.Services.Settings;

namespace WriteLite.Tests.Settings;

/// <summary>
/// The shortcut registry: what exists, what may be bound to it, and what is refused.
/// </summary>
/// <remarks>
/// Before this existed the shortcuts were nine <c>switch</c> statements across six files.
/// Nothing could enumerate them, so the product could not tell anyone what it responded to,
/// and nothing could compare them, so two handlers claiming the same keys meant whichever ran
/// first won — silently. These tests are about the two questions that were unanswerable:
/// what exists, and does this binding collide with something that does.
/// </remarks>
[TestClass]
public sealed class ShortcutRegistryTests
{
    [TestMethod]
    public void EveryShortcutHasAUniqueIdentifier()
    {
        // The identifier is persisted in settings.json, so a duplicate would silently make
        // two shortcuts share one binding for every user who customised either.
        var ids = ShortcutRegistry.Definitions.Select(definition => definition.Id).ToArray();
        Assert.AreEqual(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public void NoTwoShortcutsShipBoundToTheSameKeys()
    {
        // The condition the registry exists to make checkable, applied to the defaults.
        var byScope = ShortcutRegistry.Definitions.GroupBy(definition => definition.Scope);

        foreach (var scope in byScope)
        {
            var gestures = scope.Select(definition => definition.DefaultGesture).ToArray();
            CollectionAssert.AreEquivalent(
                gestures.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                gestures,
                $"two {scope.Key} shortcuts ship on the same keys");
        }
    }

    [TestMethod]
    public void EveryDefaultBindingCanBeReadBack()
    {
        foreach (var definition in ShortcutRegistry.Definitions)
        {
            Assert.IsTrue(
                ShortcutGesture.TryParse(definition.DefaultGesture, out var key, out var modifiers),
                $"{definition.Id}: '{definition.DefaultGesture}' does not parse");

            Assert.AreEqual(
                definition.DefaultGesture,
                ShortcutGesture.Format(key, modifiers),
                $"{definition.Id} does not survive a round trip");
        }
    }

    [TestMethod]
    public void EveryDefaultBindingIsOneTheUserWouldBeAllowedToChooseThemselves()
    {
        // Otherwise the settings page would refuse a binding it is simultaneously shipping,
        // and the reset button would produce a state the editor calls invalid.
        foreach (var definition in ShortcutRegistry.Definitions)
        {
            Assert.IsTrue(ShortcutGesture.TryParse(definition.DefaultGesture, out var key, out var modifiers));
            Assert.AreEqual(
                RebindRefusal.None,
                ShortcutGesture.Validate(key, modifiers, definition.Scope),
                $"{definition.Id} ships with a binding its own rules reject");
        }
    }

    // ── Matching ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void AKeypressResolvesToTheShortcutItIsBoundTo()
    {
        var registry = new ShortcutRegistry();

        Assert.AreEqual(
            ShortcutRegistry.SaveDocument,
            registry.Match(Key.S, ModifierKeys.Control, ShortcutScope.Application));

        Assert.AreEqual(
            ShortcutRegistry.SaveDocumentAs,
            registry.Match(Key.S, ModifierKeys.Control | ModifierKeys.Shift, ShortcutScope.Application));
    }

    [TestMethod]
    public void AKeypressBoundToNothingResolvesToNothing()
    {
        var registry = new ShortcutRegistry();
        Assert.IsNull(registry.Match(Key.Q, ModifierKeys.Control | ModifierKeys.Alt, ShortcutScope.Application));
    }

    [TestMethod]
    public void ScopesDoNotSeeEachOthersBindings()
    {
        var registry = new ShortcutRegistry();

        Assert.IsNull(
            registry.Match(Key.S, ModifierKeys.Control, ShortcutScope.Global),
            "an application shortcut must not answer a global lookup");
    }

    // ── Rebinding ────────────────────────────────────────────────────────────

    [TestMethod]
    public void ARebindTakesEffectImmediately()
    {
        var registry = new ShortcutRegistry();

        Assert.IsTrue(registry.TryRebind(ShortcutRegistry.Find, Key.G, ModifierKeys.Control).Succeeded);

        Assert.AreEqual("Ctrl+G", registry.GestureOf(ShortcutRegistry.Find));
        Assert.AreEqual(
            ShortcutRegistry.Find,
            registry.Match(Key.G, ModifierKeys.Control, ShortcutScope.Application));
        Assert.IsNull(
            registry.Match(Key.F, ModifierKeys.Control, ShortcutScope.Application),
            "the old binding must stop answering");
    }

    [TestMethod]
    public void ABindingAlreadyInUseIsRefusedAndSaysWhichShortcutHasIt()
    {
        var registry = new ShortcutRegistry();

        var result = registry.TryRebind(ShortcutRegistry.Find, Key.S, ModifierKeys.Control);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(RebindRefusal.Conflict, result.Refusal);
        Assert.AreEqual("Сохранить", result.ConflictsWith);
        Assert.Contains("Сохранить", result.Message);

        // …and nothing moved.
        Assert.AreEqual("Ctrl+F", registry.GestureOf(ShortcutRegistry.Find));
        Assert.AreEqual("Ctrl+S", registry.GestureOf(ShortcutRegistry.SaveDocument));
    }

    [TestMethod]
    public void RebindingAShortcutToWhatItAlreadyHasIsNotAConflictWithItself()
    {
        var registry = new ShortcutRegistry();
        Assert.IsTrue(registry.TryRebind(ShortcutRegistry.Find, Key.F, ModifierKeys.Control).Succeeded);
    }

    [TestMethod]
    public void ABareLetterIsRefused()
    {
        // It would make that letter untypeable everywhere in the application.
        var registry = new ShortcutRegistry();
        var result = registry.TryRebind(ShortcutRegistry.Find, Key.K, ModifierKeys.None);

        Assert.AreEqual(RebindRefusal.ModifierRequired, result.Refusal);
    }

    [TestMethod]
    public void ABareFunctionKeyIsAccepted()
    {
        // F-keys are not typing, so they may stand alone — which is how F11 ships.
        var registry = new ShortcutRegistry();
        Assert.IsTrue(registry.TryRebind(ShortcutRegistry.Find, Key.F9, ModifierKeys.None).Succeeded);
        Assert.AreEqual("F9", registry.GestureOf(ShortcutRegistry.Find));
    }

    [TestMethod]
    public void AModifierOnItsOwnIsRefused()
    {
        var registry = new ShortcutRegistry();

        Assert.AreEqual(
            RebindRefusal.NoKey,
            registry.TryRebind(ShortcutRegistry.Find, Key.LeftCtrl, ModifierKeys.Control).Refusal);

        Assert.AreEqual(
            RebindRefusal.NoKey,
            registry.TryRebind(ShortcutRegistry.Find, Key.None, ModifierKeys.Control).Refusal);
    }

    [TestMethod]
    public void TheWindowsKeyIsRefused()
    {
        // The shell takes these before any application sees them, so accepting one would
        // produce a shortcut that never fires and no way to discover why.
        var registry = new ShortcutRegistry();

        Assert.AreEqual(
            RebindRefusal.ReservedKey,
            registry.TryRebind(ShortcutRegistry.Find, Key.F, ModifierKeys.Windows).Refusal);
    }

    [TestMethod]
    public void AFixedShortcutCannotBeRebound()
    {
        var registry = new ShortcutRegistry();

        Assert.AreEqual(
            RebindRefusal.NotRebindable,
            registry.TryRebind(ShortcutRegistry.Dismiss, Key.F8, ModifierKeys.None).Refusal);

        Assert.AreEqual("Escape", registry.GestureOf(ShortcutRegistry.Dismiss));
    }

    [TestMethod]
    public void AGlobalShortcutNeedsCtrlOrAlt()
    {
        // Shift alone would leave every capital letter typed anywhere in Windows firing it.
        var registry = new ShortcutRegistry();

        Assert.AreEqual(
            RebindRefusal.ModifierRequired,
            registry.TryRebind(ShortcutRegistry.OpenWriteLite, Key.W, ModifierKeys.Shift).Refusal);

        Assert.IsTrue(
            registry.TryRebind(ShortcutRegistry.OpenWriteLite, Key.W, ModifierKeys.Alt | ModifierKeys.Shift)
                .Succeeded);
    }

    [TestMethod]
    public void AnUnknownShortcutIsRefusedRatherThanCreated()
    {
        var registry = new ShortcutRegistry();

        Assert.AreEqual(
            RebindRefusal.UnknownShortcut,
            registry.TryRebind("editor.invented", Key.J, ModifierKeys.Control).Refusal);
    }

    // ── Defaults and persistence ─────────────────────────────────────────────

    [TestMethod]
    public void OnlyChangedShortcutsArePersisted()
    {
        var registry = new ShortcutRegistry();
        Assert.IsEmpty(registry.Overrides, "a fresh registry has nothing to save");

        registry.TryRebind(ShortcutRegistry.Find, Key.G, ModifierKeys.Control);

        Assert.HasCount(1, registry.Overrides);
        Assert.AreEqual("Ctrl+G", registry.Overrides[ShortcutRegistry.Find]);
    }

    [TestMethod]
    public void BindingAShortcutBackToItsDefaultStopsRecordingIt()
    {
        // So a later release that changes this default still reaches this user.
        var registry = new ShortcutRegistry();

        registry.TryRebind(ShortcutRegistry.Find, Key.G, ModifierKeys.Control);
        registry.TryRebind(ShortcutRegistry.Find, Key.F, ModifierKeys.Control);

        Assert.IsEmpty(registry.Overrides);
        Assert.IsFalse(registry.IsCustomised(ShortcutRegistry.Find));
    }

    [TestMethod]
    public void SavedShortcutsComeBack()
    {
        var first = new ShortcutRegistry();
        first.TryRebind(ShortcutRegistry.Find, Key.G, ModifierKeys.Control);

        var second = new ShortcutRegistry(first.Overrides);

        Assert.AreEqual("Ctrl+G", second.GestureOf(ShortcutRegistry.Find));
        Assert.AreEqual(
            ShortcutRegistry.Find,
            second.Match(Key.G, ModifierKeys.Control, ShortcutScope.Application));
    }

    [TestMethod]
    public void ASettingsFileFromAnotherVersionDoesNotBreakTheLoad()
    {
        // A shortcut this build does not have, and a gesture that means nothing. Both are
        // dropped; everything else the user configured survives.
        var registry = new ShortcutRegistry(new Dictionary<string, string>
        {
            ["editor.removed-in-this-version"] = "Ctrl+Q",
            [ShortcutRegistry.Find] = "нажмите что-нибудь",
            [ShortcutRegistry.Bold] = "Ctrl+Alt+B"
        });

        Assert.AreEqual("Ctrl+F", registry.GestureOf(ShortcutRegistry.Find), "an unreadable gesture falls back");
        Assert.AreEqual("Ctrl+Alt+B", registry.GestureOf(ShortcutRegistry.Bold));
    }

    [TestMethod]
    public void ResettingOneShortcutLeavesTheOthersAlone()
    {
        var registry = new ShortcutRegistry();
        registry.TryRebind(ShortcutRegistry.Find, Key.G, ModifierKeys.Control);
        registry.TryRebind(ShortcutRegistry.Bold, Key.B, ModifierKeys.Control | ModifierKeys.Alt);

        registry.ResetToDefault(ShortcutRegistry.Find);

        Assert.AreEqual("Ctrl+F", registry.GestureOf(ShortcutRegistry.Find));
        Assert.AreEqual("Ctrl+Alt+B", registry.GestureOf(ShortcutRegistry.Bold));
    }

    [TestMethod]
    public void ResettingEverythingReturnsTheShippedSet()
    {
        var registry = new ShortcutRegistry();
        registry.TryRebind(ShortcutRegistry.Find, Key.G, ModifierKeys.Control);
        registry.TryRebind(ShortcutRegistry.Bold, Key.B, ModifierKeys.Control | ModifierKeys.Alt);

        registry.ResetAll();

        Assert.IsEmpty(registry.Overrides);
        foreach (var definition in ShortcutRegistry.Definitions)
        {
            Assert.AreEqual(definition.DefaultGesture, registry.GestureOf(definition.Id));
        }
    }

    // ── Gesture text ─────────────────────────────────────────────────────────

    [TestMethod]
    public void ModifiersAreAlwaysWrittenInTheSameOrder()
    {
        // Otherwise "Shift+Ctrl+S" and "Ctrl+Shift+S" would be two different strings for one
        // binding, and the conflict check compares strings.
        Assert.AreEqual(
            "Ctrl+Alt+Shift+K",
            ShortcutGesture.Format(Key.K, ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Control));
    }

    [TestMethod]
    public void GestureTextIsReadBackWhicheverOrderItIsWritten()
    {
        Assert.IsTrue(ShortcutGesture.TryParse("shift + ctrl + s", out var key, out var modifiers));
        Assert.AreEqual(Key.S, key);
        Assert.AreEqual(ModifierKeys.Control | ModifierKeys.Shift, modifiers);
    }

    [TestMethod]
    public void NonsenseIsNotAGesture()
    {
        Assert.IsFalse(ShortcutGesture.TryParse(null, out _, out _));
        Assert.IsFalse(ShortcutGesture.TryParse("", out _, out _));
        Assert.IsFalse(ShortcutGesture.TryParse("Ctrl", out _, out _), "a modifier alone is not a gesture");
        Assert.IsFalse(ShortcutGesture.TryParse("Ctrl+A+B", out _, out _), "two keys is not a gesture");
        Assert.IsFalse(ShortcutGesture.TryParse("Ctrl+Пробел", out _, out _));
    }

    [TestMethod]
    public void KeysAreNamedTheWayKeyboardsAreLabelled()
    {
        Assert.AreEqual("Ctrl+Enter", ShortcutGesture.Format(Key.Return, ModifierKeys.Control));
        Assert.AreEqual("Ctrl+1", ShortcutGesture.Format(Key.D1, ModifierKeys.Control));
        Assert.AreEqual("Ctrl+PageDown", ShortcutGesture.Format(Key.Next, ModifierKeys.Control));
        Assert.AreEqual("Escape", ShortcutGesture.Format(Key.Escape, ModifierKeys.None));
    }
}
