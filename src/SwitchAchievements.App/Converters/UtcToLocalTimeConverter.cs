using System.Globalization;
using System.Windows.Data;

namespace SwitchAchievements.App.Converters;

/// <summary>
/// Presents a UTC <see cref="DateTime"/> in the user's local time zone (M5.7 Task 3).
/// The dashboard exposes UTC values; local time is a view concern. Null passes through
/// so the binding's TargetNullValue applies.
/// </summary>
public sealed class UtcToLocalTimeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTime utc ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime() : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
