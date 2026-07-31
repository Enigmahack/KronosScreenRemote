using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KronosScreenRemote;

// Null/empty string -> Collapsed, anything else -> Visible. Used by LibrarianShellWindow's
// warning banner (bound directly to the ViewModel's nullable WarningText - no separate
// "HasWarning" bool property needed).
public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
