using System;
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
			new SolidColorBrush(ColorUtil.FromHex(value as string));

		/// <summary>Parses "#RRGGBB"/"#AARRGGBB" to a Color (kept for callers; delegates to <see cref="ColorUtil"/>).</summary>
		public static Color ParseHex(string? hex) => ColorUtil.FromHex(hex);

		public object ConvertBack(object value, Type targetType, object parameter, string language) =>
			throw new NotSupportedException();
	}
}
