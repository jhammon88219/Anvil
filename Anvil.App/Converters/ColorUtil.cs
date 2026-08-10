using System;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Anvil.Converters
{
	/// <summary>
	/// Shared color/brush primitives for the value converters, so hex/RGB parsing and
	/// <see cref="SolidColorBrush"/> construction live in one place instead of being re-spelled per
	/// converter (<see cref="HexToBrushConverter"/>, <see cref="RampToBrushConverter"/>,
	/// <see cref="PipelineCellStateToBrushConverter"/>).
	/// </summary>
	internal static class ColorUtil
	{
		/// <summary>Parses "#RRGGBB" or "#AARRGGBB" (leading '#' optional) to a <see cref="Color"/>;
		/// malformed/empty input degrades to <see cref="Colors.Transparent"/> (never throws).</summary>
		public static Color FromHex(string? hex)
		{
			if (string.IsNullOrWhiteSpace(hex)) { return Colors.Transparent; }
			var s = hex.TrimStart('#');
			try
			{
				if (s.Length == 6) { return Color.FromArgb(255, Byte(s, 0), Byte(s, 2), Byte(s, 4)); }
				if (s.Length == 8) { return Color.FromArgb(Byte(s, 0), Byte(s, 2), Byte(s, 4), Byte(s, 6)); }
			}
			catch { /* fall through to transparent */ }
			return Colors.Transparent;
		}

		/// <summary>Builds an OPAQUE colour from an <c>[r,g,b]</c> (or longer) int array; a null/short array
		/// yields fully transparent.</summary>
		public static Color FromRgb(int[]? rgb) =>
			rgb is { Length: >= 3 }
				? Color.FromArgb(255, (byte)rgb[0], (byte)rgb[1], (byte)rgb[2])
				: Color.FromArgb(0, 0, 0, 0);

		/// <summary>A <see cref="SolidColorBrush"/> from ARGB bytes.</summary>
		public static SolidColorBrush Solid(byte a, byte r, byte g, byte b) =>
			new(Color.FromArgb(a, r, g, b));

		private static byte Byte(string s, int i) =>
			byte.Parse(s.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
	}
}
