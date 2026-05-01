using System;
using System.Globalization;
using System.Windows.Data;

namespace SSISimulator.Converters
{
    /// <summary>
    /// Converts between a <see cref="byte"/> value and its two-digit hexadecimal string
    /// representation (e.g. 1 ↔ "01", 255 ↔ "FF").
    /// </summary>
    [ValueConversion(typeof(byte), typeof(string))]
    public class ByteHexConverter : IValueConverter
    {
        public static readonly ByteHexConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is byte b)
                return b.ToString("X2");
            return "01";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string s && byte.TryParse(s.TrimStart('0', 'x', 'X'),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte result))
                return result;
            return (byte)0x01; // default on parse failure
        }
    }
}
