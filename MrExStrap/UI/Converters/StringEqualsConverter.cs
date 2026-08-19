using System.Globalization;
using System.Windows.Data;

namespace BeastStrap.UI.Converters
{
    // Two-way string == parameter converter, used to drive RadioButton "chip" filters on the News page.
    // IsChecked is true when the bound value equals the chip's ConverterParameter; checking a chip writes
    // that parameter back to the bound property. Unchecking (because a sibling was checked) is a no-op, so
    // the RadioButton group behaves like a single-select filter.
    public class StringEqualsConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? (parameter?.ToString() ?? "") : Binding.DoNothing;
    }
}
