using System.Windows;

namespace Ferry;

public static class ButtonHelper
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached(
            "CornerRadius",
            typeof(CornerRadius),
            typeof(ButtonHelper),
            new FrameworkPropertyMetadata(new CornerRadius(6.0), FrameworkPropertyMetadataOptions.Inherits));

    public static CornerRadius GetCornerRadius(DependencyObject obj) => (CornerRadius)obj.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject obj, CornerRadius value) => obj.SetValue(CornerRadiusProperty, value);
}
