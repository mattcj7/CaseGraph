using System.Globalization;
using System.Windows.Data;

namespace CaseGraph.App.Converters;

public sealed class Sha256ShortConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string sha || string.IsNullOrWhiteSpace(sha))
        {
            return "(none)";
        }

        return sha.Length <= 12 ? sha : sha[..12];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
