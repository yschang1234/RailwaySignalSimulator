using System;
using System.Globalization;
using System.Windows.Data;

namespace TFMUMSimulator.Converters
{
    /// <summary>
    /// Converts between a <see cref="byte"/> value and its two-digit hexadecimal string
    /// (e.g. 1 ↔ "01", 255 ↔ "FF").
    /// </summary>
    [ValueConversion(typeof(byte), typeof(string))]
    public class ByteHexConverter : IValueConverter
    {
        public static readonly ByteHexConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is byte b)
                return b.ToString("X2");
            return "00";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string s)
            {
                string hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? s[2..] : s;
                if (byte.TryParse(hex, NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out byte result))
                    return result;
            }
            return (byte)0x00;
        }
    }
}
