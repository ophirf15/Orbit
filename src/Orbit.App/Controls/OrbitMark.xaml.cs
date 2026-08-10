using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Orbit_App.Controls;

public sealed partial class OrbitMark : UserControl
{
    public static readonly DependencyProperty MarkSizeProperty =
        DependencyProperty.Register(
            nameof(MarkSize),
            typeof(double),
            typeof(OrbitMark),
            new PropertyMetadata(28.0));

    public OrbitMark()
    {
        InitializeComponent();
    }

    public double MarkSize
    {
        get => (double)GetValue(MarkSizeProperty);
        set => SetValue(MarkSizeProperty, value);
    }
}
