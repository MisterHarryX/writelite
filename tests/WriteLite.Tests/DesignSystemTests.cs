using System.Windows;
using System.Windows.Media;

namespace WriteLite.Tests;

/// <summary>
/// Guards the design system itself.
/// </summary>
/// <remarks>
/// XAML resource dictionaries are parsed at runtime, so a malformed template or a
/// broken <c>BasedOn</c> chain compiles cleanly and only fails when the window is
/// shown. These tests load the real theme and assert that the tokens every screen
/// depends on resolve, which turns that class of mistake back into a build failure.
///
/// Not parallelised: every UI test marshals onto the one shared dispatcher, and
/// pumping it from inside one test would otherwise run another test's body nested
/// inside this one.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class DesignSystemTests
{
    [TestMethod]
    public void Theme_loads_and_exposes_every_token_the_views_bind_to()
    {
        RunOnStaThread(() =>
        {
            var theme = LoadTheme();

            // Foundations
            AssertBrush(theme, "WlBgBase", "#FF080808");
            AssertBrush(theme, "WlSurface", "#FF151514");
            AssertBrush(theme, "WlText", "#FFF1EFEC");
            AssertBrush(theme, "WlBrand", "#FFEC6C08");
            AssertBrush(theme, "WlOnBrand", "#FF170C03");

            // "WlDanger" used to be requested by CorrectionPopupWindow without ever
            // being defined, so apply failures silently rendered in OrangeRed.
            AssertBrush(theme, "WlDanger", "#FFE3695A");

            foreach (var key in new[]
                     {
                         "WlBg", "WlBgSoft", "WlSurfaceHi", "WlRaised", "WlHover",
                         "WlLine", "WlLineSoft", "WlLineStrong", "WlLineAccent", "WlBorder",
                         "WlTextSecondary", "WlTextMuted", "WlTextSubtle", "WlDisabled",
                         "WlBrandHover", "WlBrandPressed", "WlBrandDim", "WlBrandSoft",
                         "WlSuccess", "WlWarning", "WlInfo", "WlError",
                         "WlCatSpelling", "WlCatGrammar", "WlCatPunctuation", "WlCatStyle", "WlCatFormatting"
                     })
            {
                Assert.IsInstanceOfType<Brush>(theme[key], $"Brush token '{key}' is missing.");
            }

            foreach (var key in new[]
                     {
                         "WlDisplay", "WlPageTitle", "WlSectionTitle", "WlCardTitle",
                         "WlBody", "WlBodyLarge", "WlSecondary", "WlCaption", "WlPageSubtitle",
                         "WlMonoLabel", "WlMonoNumber", "WlMonoValue", "WlSectionLabel"
                     })
            {
                Assert.IsInstanceOfType<Style>(theme[key], $"Type style '{key}' is missing.");
            }

            foreach (var key in new[]
                     {
                         "WlPrimaryButton", "WlSecondaryButton", "WlGhostButton", "WlTextButton",
                         "WlToolbarButton", "WlDangerButton", "WlIconButton", "WlWindowButton",
                         "WlWindowCloseButton", "WlCtaButton", "WlFooterPrimaryButton",
                         "WlPenButton", "WlBadgeButton", "WlBadgeButtonQuiet"
                     })
            {
                Assert.IsInstanceOfType<Style>(theme[key], $"Button style '{key}' is missing.");
            }

            foreach (var key in new[]
                     {
                         "WlTextInput", "WlSmallInput", "WlSearchInput",
                         "WlCheckBox", "WlToggleSwitch", "WlComboBox", "WlComboBoxItem"
                     })
            {
                Assert.IsInstanceOfType<Style>(theme[key], $"Input style '{key}' is missing.");
            }

            foreach (var key in new[]
                     {
                         "WlCard", "WlPremiumCard", "WlInsetPanel", "WlGroup", "WlHairline",
                         "WlPopoverSurface", "WlPopupSurface", "WlStatusChip",
                         "WlListBox", "WlListRow", "WlCardRow", "WlDataGrid",
                         "WlNavigationRail", "WlNavButton", "WlTabButton",
                         "WlContextMenu", "WlMenuItem", "WlMenuSeparator", "WlToolTip", "WlScrollViewer",
                         "WlCorrectionCard", "WlCategoryLabel", "WlOriginalText", "WlReplacementText",
                         "WlChangeArrow", "WlExplanation", "WlSuggestionChip",
                         "WlIcon", "WlIconSm", "WlIconLg",
                         // Dictionary surfaces and the ambience player's slider.
                         "WlWordTitle", "WlArticleBlock", "WlDisclosure", "WlSkeletonBar",
                         "WlSlider", "WlSliderThumb", "WlNavIndicator"
                     })
            {
                Assert.IsInstanceOfType<Style>(theme[key], $"Component style '{key}' is missing.");
            }
        });
    }

    /// <summary>
    /// Forces every resource in the theme to be created.
    /// </summary>
    /// <remarks>
    /// WPF realises dictionary entries lazily, so a style that cannot resolve its own
    /// references compiles, loads and only throws when the screen using it is first
    /// shown. Enumerating every key turns that into a test failure. It caught a real one:
    /// a <c>BasedOn="{StaticResource …}"</c> pointing at a sibling merged dictionary,
    /// which crashed the app the first time a grouped word list appeared.
    /// </remarks>
    [TestMethod]
    public void Every_resource_in_the_theme_can_be_realised()
    {
        RunOnStaThread(() =>
        {
            var theme = LoadTheme();
            var failures = new List<string>();

            void Walk(ResourceDictionary dictionary)
            {
                foreach (var child in dictionary.MergedDictionaries)
                {
                    Walk(child);
                }

                foreach (var key in dictionary.Keys)
                {
                    try
                    {
                        _ = dictionary[key];
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{key}: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
            }

            Walk(theme);

            Assert.IsEmpty(failures, $"Theme resources failed to realise:\n{string.Join("\n", failures)}");
        });
    }

    [TestMethod]
    public void Navigation_icons_are_all_present()
    {
        RunOnStaThread(() =>
        {
            var theme = LoadTheme();
            foreach (var key in new[]
                     {
                         "WlIconHome", "WlIconEditor", "WlIconDictionary", "WlIconWords",
                         "WlIconEngine", "WlIconSettings", "WlIconDiagnostics", "WlIconAbout",
                         "WlIconApps", "WlIconPacks", "WlIconMinimize", "WlIconMaximize",
                         "WlIconRestore", "WlIconClose", "WlIconSearch", "WlIconPlus",
                         "WlIconTrash", "WlIconCheck", "WlIconStatus", "WlIconChevronRight",
                         "WlIconChevronDown", "WlIconFilter", "WlIconRefresh", "WlIconCopy",
                         "WlIconFolder", "WlIconPrivacy", "WlIconLanguage", "WlIconUndo",
                         "WlIconImport", "WlIconExport", "WlIconIgnore", "WlIconMenu", "WlIconPanel",
                         "WlIconPlay", "WlIconPause", "WlIconPrevious", "WlIconNext", "WlIconVolume"
                     })
            {
                Assert.IsInstanceOfType<Geometry>(theme[key], $"Icon geometry '{key}' is missing.");
            }
        });
    }

    /// <summary>Radii stay restrained; nothing may drift back to the 18–24 px look.</summary>
    [TestMethod]
    public void Corner_radii_stay_within_the_editorial_range()
    {
        RunOnStaThread(() =>
        {
            var theme = LoadTheme();
            foreach (var key in new[] { "WlRadiusXs", "WlRadiusSm", "WlRadius", "WlRadiusMd", "WlRadiusLg" })
            {
                var radius = (CornerRadius)theme[key];
                Assert.IsTrue(
                    radius.TopLeft is >= 4 and <= 12,
                    $"'{key}' is {radius.TopLeft}; the design system caps panel radii at 12.");
            }
        });
    }

    /// <summary>Text on the orange accent must be the near-black brand foreground.</summary>
    [TestMethod]
    public void Primary_button_pairs_orange_with_the_dark_brand_foreground()
    {
        RunOnStaThread(() =>
        {
            var theme = LoadTheme();
            var style = (Style)theme["WlPrimaryButton"];

            var background = FindSetterValue(style, System.Windows.Controls.Control.BackgroundProperty);
            var foreground = FindSetterValue(style, System.Windows.Controls.Control.ForegroundProperty);

            Assert.IsNotNull(background, "WlPrimaryButton does not set a Background.");
            Assert.IsNotNull(foreground, "WlPrimaryButton does not set a Foreground.");
        });
    }

    private static object? FindSetterValue(Style style, DependencyProperty property)
    {
        foreach (var setterBase in style.Setters)
        {
            if (setterBase is Setter setter && setter.Property == property)
            {
                return setter.Value;
            }
        }

        return null;
    }

    private static void AssertBrush(ResourceDictionary theme, string key, string expected)
    {
        var brush = theme[key] as SolidColorBrush;
        Assert.IsNotNull(brush, $"Brush token '{key}' is missing.");
        Assert.AreEqual(expected, brush!.Color.ToString(), $"Brush token '{key}' drifted from the website palette.");
    }

    private static ResourceDictionary LoadTheme() => WpfTestHost.LoadTheme();

    private static void RunOnStaThread(Action action) => WpfTestHost.Run(action);
}
