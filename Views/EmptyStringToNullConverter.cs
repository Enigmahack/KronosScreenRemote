using System.Globalization;
using System.Windows.Data;

namespace KronosScreenRemote;

// Null/empty string -> null, so a ToolTip bound directly to a status-text property doesn't pop
// up an empty bubble while there's nothing to say (WPF only suppresses a ToolTip when its value
// is null, not when it's ""). Anything else passes through unchanged.
public sealed class EmptyStringToNullConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? null : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
