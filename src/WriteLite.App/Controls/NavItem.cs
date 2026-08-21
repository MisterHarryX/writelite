using System.Windows;
using System.Windows.Media;
using RadioButton = System.Windows.Controls.RadioButton;

namespace WriteLite.Controls;

/// <summary>
/// A single row in the navigation rail: mono index, outline icon, label.
/// </summary>
/// <remarks>
/// Derives from <see cref="RadioButton"/> so selection is mutually exclusive within
/// a <c>GroupName</c> and reaches assistive technology as a real selection, rather
/// than being faked with a <c>Tag</c> string on a button.
/// </remarks>
public sealed class NavItem : RadioButton
{
    public static readonly DependencyProperty IndexProperty =
        DependencyProperty.Register(nameof(Index), typeof(string), typeof(NavItem),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(NavItem),
            new PropertyMetadata(string.Empty, OnLabelChanged));

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(Geometry), typeof(NavItem),
            new PropertyMetadata(null));

    /// <summary>Collapses the rail to icons only.</summary>
    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(NavItem),
            new PropertyMetadata(false));

    static NavItem()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(NavItem),
            new FrameworkPropertyMetadata(typeof(NavItem)));
    }

    public string Index
    {
        get => (string)GetValue(IndexProperty);
        set => SetValue(IndexProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    // In the compact rail the label is not rendered, so the accessible name and the
    // tooltip have to carry it instead.
    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var label = e.NewValue as string ?? string.Empty;
        d.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, label);
        if (d is NavItem item && item.ToolTip is null)
        {
            item.ToolTip = label;
        }
    }
}
