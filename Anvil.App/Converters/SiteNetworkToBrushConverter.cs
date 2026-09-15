using System;
using Anvil.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Anvil.Converters
{
	/// <summary>
	/// Maps a site's <see cref="RadarSiteClass"/> to the fill of its NETWORK bar — the right-hand zone of a
	/// Radar Atlas row, mirroring the map key's class bar: TDWR = blue, research = violet, NEXRAD = transparent
	/// (the row lays a themed neutral underneath, because the majority network is chrome, not data).
	/// </summary>
	/// <remarks>
	/// ⚠️ FIXED COLOURS — the same literals as radar-sites.js (<c>.radar-site-class.tdwr / .research</c>), so a
	/// row and the marker it describes can't disagree. A converter can never resolve a theme brush, which is
	/// exactly why NEXRAD returns Transparent instead of a neutral.
	/// </remarks>
	public sealed class SiteNetworkToBrushConverter : IValueConverter
	{
		private static readonly SolidColorBrush Tdwr = new(ColorHelper.FromArgb(0xFF, 0x2F, 0x6F, 0xB0));
		private static readonly SolidColorBrush Research = new(ColorHelper.FromArgb(0xFF, 0x6B, 0x4B, 0xD6));
		private static readonly SolidColorBrush None = new(Colors.Transparent);

		/// <summary>For x:Bind functions in the row template.</summary>
		public static Brush For(RadarSiteClass siteClass) => siteClass switch
		{
			RadarSiteClass.Tdwr => Tdwr,
			RadarSiteClass.Research => Research,
			_ => None,
		};

		public object Convert(object value, Type targetType, object parameter, string language) =>
			value is RadarSiteClass siteClass ? For(siteClass) : None;

		public object ConvertBack(object value, Type targetType, object parameter, string language) =>
			throw new NotSupportedException();
	}
}
