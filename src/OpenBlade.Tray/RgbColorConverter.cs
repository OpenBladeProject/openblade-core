using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using OpenBlade.Contracts;
using WinUIColor = Windows.UI.Color;

namespace OpenBlade.Tray;

public sealed class RgbColorConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        string language)
    {
        if (value is not RgbColor rgb)
        {
            return DependencyProperty.UnsetValue;
        }

        WinUIColor color = WinUIColor.FromArgb(255, rgb.Red, rgb.Green, rgb.Blue);
        return (parameter as string) switch
        {
            "Brush" => new SolidColorBrush(color),
            "Hex" => $"#{rgb.Red:X2}{rgb.Green:X2}{rgb.Blue:X2}",
            _ => color,
        };
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        string language) => value is WinUIColor color
        ? new RgbColor(color.R, color.G, color.B)
        : DependencyProperty.UnsetValue;
}
