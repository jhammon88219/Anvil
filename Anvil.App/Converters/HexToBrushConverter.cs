using System;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Anvil.Converters
{
	/// <summary>
	/// Converts a "#RRGGBB" (or "#AARRGGBB") hex string to a <see cref="SolidColorBrush"/>. Used by the SPC
	/// outlook legend swatches, whose colors come straight from the outlook GeoJSON's official SPC
	/// <c>fill</c>/<c>stroke</c> values. Malformed input degrades to transparent (never throws).
	/// </summary>
	public sealed class HexToBrushConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language) =>
			new SolidColorBrush(ParseHex(value as string));

		public static Color ParseHex(string? hex)
		{
			if (string.IsNullOrWhiteSpace(hex)) { return Colors.Transparent; }
			var s = hex.TrimStart('#');
			try
			{
				if (s.Length == 6)
				{
					return Color.FromArgb(255, Hex(s, 0), Hex(s, 2), Hex(s, 4));
				}
				if (s.Length == 8)
				{
					return Color.FromArgb(Hex(s, 0), Hex(s, 2), Hex(s, 4), Hex(s, 6));
				}
			}
			catch { /* fall through to transparent */ }
			return Colors.Transparent;
		}

		private static byte Hex(string s, int i) =>
			byte.Parse(s.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

		public object ConvertBack(object value, Type targetType, object parameter, string language) =>
			throw new NotSupportedException();
	}
}
