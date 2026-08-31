using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WriteLite.Services.Settings;
using WriteLite.Views.Pages;
using Button = System.Windows.Controls.Button;

namespace WriteLite.Tests.Settings;

/// <summary>
/// The shortcuts section of the settings page, through the real control.
/// </summary>
/// <remarks>
/// <see cref="ShortcutRegistryTests"/> covers the rules. These cover that the page actually
/// shows every shortcut the product has — the brief asked for a management interface rather
/// than a hand-written list, and a hand-written list is exactly what this drifts into the
/// moment the page stops being generated from the registry.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ShortcutSettingsSectionTests
{
    [TestMethod]
    public void EveryShortcutInTheProductAppearsOnThePage()
    {
        WithPage((page, _) =>
        {
            var texts = Descendants<TextBlock>(page).Select(block => block.Text).ToArray();

            foreach (var definition in ShortcutRegistry.Definitions)
            {
                Assert.Contains(
                    definition.Name,
                    texts,
                    $"{definition.Id} is in the registry but not on the settings page");
            }
        });
    }

    [TestMethod]
    public void EachShortcutShowsWhatItIsBoundTo()
    {
        WithPage((page, registry) =>
        {
            var captions = GestureButtons(page).Select(button => button.Content as string).ToArray();

            foreach (var definition in ShortcutRegistry.Definitions)
            {
                Assert.Contains(registry.GestureOf(definition.Id), captions!, definition.Id);
            }
        });
    }

    [TestMethod]
    public void AFixedShortcutIsShownButCannotBeEdited()
    {
        // Escape is listed because "what does this do when I press Escape" is a question the
        // page exists to answer; it is disabled because the answer is not a preference.
        WithPage((page, _) =>
        {
            var escape = GestureButtons(page)
                .Single(button => (string?)button.Tag == ShortcutRegistry.Dismiss);

            Assert.IsFalse(escape.IsEnabled);
            Assert.AreEqual("Escape", escape.Content);
        });
    }

    [TestMethod]
    public void GlobalAndLocalShortcutsAreLabelledDifferently()
    {
        WithPage((page, _) =>
        {
            var texts = Descendants<TextBlock>(page).Select(block => block.Text).ToArray();

            Assert.IsTrue(
                texts.Any(text => text.Contains("Системное", StringComparison.Ordinal)),
                "a global shortcut must say that it works outside WriteLite");
            Assert.IsTrue(texts.Any(text => text.Contains("Внутри WriteLite", StringComparison.Ordinal)));
        });
    }

    [TestMethod]
    public void ResettingEverythingPutsThePageBackToTheShippedBindings()
    {
        WithPage((page, registry) =>
        {
            registry.TryRebind(ShortcutRegistry.Find, Key.G, ModifierKeys.Control);
            page.BindShortcuts(registry);

            Assert.Contains(
                "Ctrl+G",
                GestureButtons(page).Select(button => button.Content as string).ToArray()!);

            Press(page, "Сбросить все сочетания");

            var captions = GestureButtons(page).Select(button => button.Content as string).ToArray();
            Assert.Contains("Ctrl+F", captions!);
            Assert.DoesNotContain("Ctrl+G", captions!);
            Assert.IsEmpty(registry.Overrides);
        });
    }

    [TestMethod]
    public void AChangedShortcutIsWrittenIntoSettings()
    {
        WpfTestHost.Run(() =>
        {
            WpfTestHost.EnsureThemeApplied();

            var settings = new WriteLiteAppSettings();
            var page = new SettingsPage();
            page.Bind(settings, new NoAutostart());

            var registry = new ShortcutRegistry(settings.Shortcuts);
            page.BindShortcuts(registry);

            registry.TryRebind(ShortcutRegistry.Find, Key.G, ModifierKeys.Control);
            page.BindShortcuts(registry);
            Press(page, "Сбросить все сочетания");

            // The reset went through the page, so it must have reached the settings object
            // the shell persists — otherwise the change survives only until the next launch.
            Assert.IsEmpty(settings.Shortcuts);
        });
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static void WithPage(Action<SettingsPage, ShortcutRegistry> assert)
    {
        WpfTestHost.Run(() =>
        {
            WpfTestHost.EnsureThemeApplied();

            var page = new SettingsPage();
            page.Bind(new WriteLiteAppSettings(), new NoAutostart());

            var registry = new ShortcutRegistry();
            page.BindShortcuts(registry);

            assert(page, registry);
        });
    }

    private static Button[] GestureButtons(SettingsPage page) =>
        [.. Descendants<Button>(page).Where(button => button.Tag is string)];

    private static void Press(SettingsPage page, string content)
    {
        var button = Descendants<Button>(page).First(b => (b.Content as string) == content);
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    private static List<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        var seen = new HashSet<DependencyObject>();
        Walk(root);
        return found;

        void Walk(DependencyObject node)
        {
            // Both trees are walked and most nodes appear in both, so without this every
            // such node is reported twice.
            if (!seen.Add(node)) return;

            if (node is T match) found.Add(match);

            // Only a Visual has a visual tree. A Grid's ColumnDefinitions are logical
            // children and throw when asked for visual ones, which is why the guard is here
            // rather than around the logical walk below.
            if (node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
            {
                var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < count; i++)
                {
                    Walk(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
                }
            }

            // The page is built partly in code and not yet arranged, so some rows exist only
            // in the logical tree.
            foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject dependency && !ReferenceEquals(dependency, node))
                {
                    Walk(dependency);
                }
            }
        }
    }

    private sealed class NoAutostart : IWriteLiteAutostartService
    {
        public bool CanEnableForCurrentBinary => false;

        public string StatusMessage => "Тест";

        public bool IsEnabled => false;

        public void SetEnabled(bool enabled)
        {
        }
    }
}
