using System.Windows.Data;

namespace BeastStrap.UI.Converters
{
    // The height of a strip that shows a wide image: its width times the image's height-to-width ratio, so the whole
    // image fits once the strip is wide enough, and never under Floor, where it crops the sides instead.
    internal class AspectHeightConverter : IValueConverter
    {
        public double Ratio { get; set; }

        public double Floor { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Math.Round(Math.Max(Floor, (double)value * Ratio));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
