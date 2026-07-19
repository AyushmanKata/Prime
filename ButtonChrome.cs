using System.Windows;
using System.Windows.Media;

namespace WinCalc;

/// <summary>
/// Attached hover/press brush properties shared by the Btn/OpBtn/EqBtn styles
/// in App.xaml. Lets all calculator-button styles use a single ControlTemplate
/// (see "CalcButtonTemplate" in App.xaml) instead of each style redefining its
/// own copy just to change the hover/press color.
/// Add a new button variant by setting these two properties in a style —
/// no new ControlTemplate needed.
/// </summary>
public static class ButtonChrome
{
    /// <summary>Background brush applied while the mouse is over the button.</summary>
    public static readonly DependencyProperty HoverBrushProperty =
        DependencyProperty.RegisterAttached("HoverBrush", typeof(Brush), typeof(ButtonChrome));

    /// <summary>Background brush applied while the button is pressed.</summary>
    public static readonly DependencyProperty PressBrushProperty =
        DependencyProperty.RegisterAttached("PressBrush", typeof(Brush), typeof(ButtonChrome));

    public static Brush GetHoverBrush(DependencyObject obj) => (Brush)obj.GetValue(HoverBrushProperty);
    public static void SetHoverBrush(DependencyObject obj, Brush value) => obj.SetValue(HoverBrushProperty, value);

    public static Brush GetPressBrush(DependencyObject obj) => (Brush)obj.GetValue(PressBrushProperty);
    public static void SetPressBrush(DependencyObject obj, Brush value) => obj.SetValue(PressBrushProperty, value);
}
