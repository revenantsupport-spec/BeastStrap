using System;
using System.Globalization;
using System.Windows.Data;

using BeastStrap.Utility;

namespace BeastStrap.UI.Converters
{
    // Shows a check for flags confirmed to exist in Roblox's live client settings. Blank until the
    // known-flags list has loaded, or for flags that aren't in it (custom / not currently published).
    public class FlagKnownConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!KnownFlags.Loaded)
                return "";

            return KnownFlags.IsKnown(value as string) ? "✓" : "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
