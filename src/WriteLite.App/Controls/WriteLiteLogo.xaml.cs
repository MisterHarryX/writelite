using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Controls;

public partial class WriteLiteLogo : UserControl
{
    public static readonly DependencyProperty LogoSizeProperty = DependencyProperty.Register(
        nameof(LogoSize),
        typeof(double),
        typeof(WriteLiteLogo),
        new PropertyMetadata(32.0, OnLogoSizeChanged));

    public WriteLiteLogo()
    {
        InitializeComponent();
        ApplySize(LogoSize);
    }

    public double LogoSize
    {
        get => (double)GetValue(LogoSizeProperty);
        set => SetValue(LogoSizeProperty, value);
    }

    private static void OnLogoSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WriteLiteLogo logo)
        {
            logo.ApplySize((double)e.NewValue);
        }
    }

    private void ApplySize(double size)
    {
        Width = size;
        Height = size;
        if (LogoFrame is null)
        {
            return;
        }

        LogoFrame.Width = size;
        LogoFrame.Height = size;
        // UI Spec: ~27% of size
        LogoFrame.CornerRadius = new CornerRadius(Math.Round(size * 0.27));
    }
}
