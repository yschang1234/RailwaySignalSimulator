using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TFMUMSimulator.Converters
{
    /// <summary>
    /// Converts a <see cref="bool"/> to <see cref="Visibility"/>.
    /// True → Visible, False → Collapsed.
    /// </summary>
    [ValueConversion(typeof(bool), typeof(Visibility))]
    public class BoolToVisibilityConverter : IValueConverter
    {
        public static readonly BoolToVisibilityConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is Visibility.Visible;
    }
}
