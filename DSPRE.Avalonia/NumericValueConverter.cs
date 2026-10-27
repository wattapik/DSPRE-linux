using Avalonia.Data;
using Avalonia.Data.Converters;
using System.Globalization;

namespace DSPRE.Avalonia;

/// <summary>For a NumericUpDown bound to a whole-number property. A cleared box has the value null,
/// which the binding cannot store in an int and reports as a conversion error; this leaves the property
/// as it was instead, and MainWindow puts the value back in the box when it loses focus.</summary>
public sealed class NumericValueConverter : IValueConverter
{
    public static readonly NumericValueConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? null : System.Convert.ToDecimal(value, culture);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return BindingOperations.DoNothing;
        Type target = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return System.Convert.ChangeType(Math.Round(System.Convert.ToDecimal(value, culture)), target, culture);
    }
}
