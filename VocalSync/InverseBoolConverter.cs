using System.Globalization;
using System.Windows.Data;

namespace VocalSync;

/// <summary>
/// Simple value converter: returns !value for bool→bool conversions.
/// Used to disable the device ComboBox and refresh button while capture is running.
/// </summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}
