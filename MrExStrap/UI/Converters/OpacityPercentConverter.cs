using System;
using System.Globalization;
using System.Windows.Data;

namespace BeastStrap.UI.Converters
{
    // Converts a 0..1 opacity double to a 0..100 slider value and back.
    internal class OpacityPercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is double d ? Math.Round(d * 100) : 100;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is double v ? v / 100 : 1.0;
    }
}