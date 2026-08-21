using System.Windows;
using Control = System.Windows.Controls.Control;

namespace WriteLite.Controls;

/// <summary>
/// Numbered section header: <c>01  Проверка</c>, an optional description, trailing
/// actions and a hairline rule.
/// </summary>
/// <remarks>
/// A templated control rather than a UserControl on purpose. A UserControl defines its
/// own XAML namescope, so anything a page places in <see cref="Actions"/> could not carry
/// an <c>x:Name</c> — which every real use of the slot needs.
/// </remarks>
public sealed class SectionHeader : Control
{
    public static readonly DependencyProperty IndexProperty =
        DependencyProperty.Register(nameof(Index), typeof(string), typeof(SectionHeader),
            new PropertyMetadata(null, OnIndexChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SectionHeader),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SectionHeader),
            new PropertyMetadata(null, OnDescriptionChanged));

    public static readonly DependencyProperty ActionsProperty =
        DependencyProperty.Register(nameof(Actions), typeof(object), typeof(SectionHeader),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ShowRuleProperty =
        DependencyProperty.Register(nameof(ShowRule), typeof(bool), typeof(SectionHeader),
            new PropertyMetadata(true, OnShowRuleChanged));

    private static readonly DependencyPropertyKey IndexVisibilityKey =
        DependencyProperty.RegisterReadOnly(nameof(IndexVisibility), typeof(Visibility), typeof(SectionHeader),
            new PropertyMetadata(Visibility.Collapsed));

    private static readonly DependencyPropertyKey DescriptionVisibilityKey =
        DependencyProperty.RegisterReadOnly(nameof(DescriptionVisibility), typeof(Visibility), typeof(SectionHeader),
            new PropertyMetadata(Visibility.Collapsed));

    private static readonly DependencyPropertyKey RuleVisibilityKey =
        DependencyProperty.RegisterReadOnly(nameof(RuleVisibility), typeof(Visibility), typeof(SectionHeader),
            new PropertyMetadata(Visibility.Visible));

    public static readonly DependencyProperty IndexVisibilityProperty = IndexVisibilityKey.DependencyProperty;
    public static readonly DependencyProperty DescriptionVisibilityProperty = DescriptionVisibilityKey.DependencyProperty;
    public static readonly DependencyProperty RuleVisibilityProperty = RuleVisibilityKey.DependencyProperty;

    static SectionHeader()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SectionHeader),
            new FrameworkPropertyMetadata(typeof(SectionHeader)));
        FocusableProperty.OverrideMetadata(
            typeof(SectionHeader),
            new FrameworkPropertyMetadata(false));
    }

    /// <summary>Two-digit marker such as <c>03</c>. Leave empty to hide the marker.</summary>
    public string? Index
    {
        get => (string?)GetValue(IndexProperty);
        set => SetValue(IndexProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Trailing content, typically a button or a status label.</summary>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public bool ShowRule
    {
        get => (bool)GetValue(ShowRuleProperty);
        set => SetValue(ShowRuleProperty, value);
    }

    public Visibility IndexVisibility => (Visibility)GetValue(IndexVisibilityProperty);

    public Visibility DescriptionVisibility => (Visibility)GetValue(DescriptionVisibilityProperty);

    public Visibility RuleVisibility => (Visibility)GetValue(RuleVisibilityProperty);

    private static void OnIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.SetValue(IndexVisibilityKey, string.IsNullOrWhiteSpace(e.NewValue as string) ? Visibility.Collapsed : Visibility.Visible);

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.SetValue(DescriptionVisibilityKey, string.IsNullOrWhiteSpace(e.NewValue as string) ? Visibility.Collapsed : Visibility.Visible);

    private static void OnShowRuleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.SetValue(RuleVisibilityKey, e.NewValue is true ? Visibility.Visible : Visibility.Collapsed);
}
