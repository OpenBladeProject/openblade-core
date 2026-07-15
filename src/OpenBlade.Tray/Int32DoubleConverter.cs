using Microsoft.UI.Xaml.Data;

namespace OpenBlade.Tray;

public sealed class Int32DoubleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int integer
            ? IntegerEditorValue.ToDouble(integer)
            : throw new ArgumentException("The editor source value must be an Int32.", nameof(value));

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is double number
            ? IntegerEditorValue.ToInt32(number)
            : throw new ArgumentException("The editor target value must be a Double.", nameof(value));
}
