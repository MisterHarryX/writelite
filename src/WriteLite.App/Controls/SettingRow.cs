using System.Windows;

namespace WriteLite.Controls;

/// <summary>
/// One row inside a settings group: label and explanation on the left, the control that
/// changes it on the right.
/// </summary>
/// <remarks>
/// Templated for the same reason as <see cref="SectionHeader"/>: a page needs to give the
/// control in <see cref="Control"/> an <c>x:Name</c> so its code-behind can read it, which
/// a UserControl's private namescope would forbid.
///
/// The row used to draw a hairline underneath itself. Across the five settings groups that
/// produced about twenty horizontal lines on one screen, which fragmented the page into
/// strips and made scanning it harder rather than easier. Grouping is now carried by the
/// numbered section headers and by vertical rhythm alone; the only rule left on the screen
/// is the one under each section title.
/// </remarks>
public sealed class SettingRow : System.Windows.Controls.Control
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(SettingRow),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow),
            new PropertyMetadata(null, OnDescriptionChanged));

    public static readonly DependencyProperty ControlProperty =
        DependencyProperty.Register(nameof(Control), typeof(object), typeof(SettingRow),
            new PropertyMetadata(null));

    private static readonly DependencyPropertyKey DescriptionVisibilityKey =
        DependencyProperty.RegisterReadOnly(nameof(DescriptionVisibility), typeof(Visibility), typeof(SettingRow),
            new PropertyMetadata(Visibility.Collapsed));

    public static readonly DependencyProperty DescriptionVisibilityProperty = DescriptionVisibilityKey.DependencyProperty;

    static SettingRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SettingRow),
            new FrameworkPropertyMetadata(typeof(SettingRow)));
        FocusableProperty.OverrideMetadata(
            typeof(SettingRow),
            new FrameworkPropertyMetadata(false));
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Short explanation of what the setting actually does.</summary>
    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>The control on the right: a toggle, a combo box or a small input.</summary>
    public object? Control
    {
        get => GetValue(ControlProperty);
        set => SetValue(ControlProperty, value);
    }

    public Visibility DescriptionVisibility => (Visibility)GetValue(DescriptionVisibilityProperty);

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.SetValue(DescriptionVisibilityKey, string.IsNullOrWhiteSpace(e.NewValue as string) ? Visibility.Collapsed : Visibility.Visible);
}
