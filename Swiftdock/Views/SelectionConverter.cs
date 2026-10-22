using System.Globalization;
using System.Windows.Data;

namespace Swiftdock.Views;

public sealed class SelectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? targetType.IsEnum ? Enum.Parse(targetType, parameter.ToString()!)
            : System.Convert.ChangeType(parameter, targetType, CultureInfo.InvariantCulture) : Binding.DoNothing;
}
