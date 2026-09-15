using System;
using Anvil.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Anvil.Converters
{
	/// <summary>
	/// Maps <see cref="ViewModels.RadarSiteRow.Availability"/> to a status-dot brush: online = green, offline =
	/// red, not checked yet = grey. Used by the Radar Atlas's list + detail dots and the Atlas flyout.
	/// </summary>
	/// <remarks>
	/// ⚠️ FIXED COLOURS, NOT THEME BRUSHES — availability is DATA, and by the membership rule (docs/theming.md)
	/// a colour that tells you what something IS is never the theme's. These are the SAME three values the
	/// on-map site key's availability square uses (radar-sites.js, literals for the same reason), so a dot in a
	/// list and the marker on the map cannot disagree about what a colour means.
	///
	/// ⚠️ A converter has no element, so it can never resolve a theme brush correctly (the old version resolved
	/// SystemFillColorSuccessBrush through Application.Current.Resources and drew the OS theme's dots over the
	/// pinned app theme). If a converter ever seems to need a theme brush, the value belongs in a visual state.
	/// </remarks>
	public sealed class SiteAvailabilityToBrushConverter : IValueConverter
	{
		// Shared instances: one brush each, not one per row per refresh.
		private static readonly SolidColorBrush Online = new(ColorHelper.FromArgb(0xFF, 0x3F, 0xB9, 0x50));
		private static readonly SolidColorBrush Offline = new(ColorHelper.FromArgb(0xFF, 0xF8, 0x51, 0x49));
		private static readonly SolidColorBrush Unknown = new(ColorHelper.FromArgb(0xFF, 0x6E, 0x76, 0x81));

		/// <summary>For x:Bind functions (the Atlas's detail dot), which can't use a StaticResource converter.</summary>
		public static Brush For(SiteAvailability availability) => availability switch
		{
			SiteAvailability.Online => Online,
			SiteAvailability.Offline => Offline,
			_ => Unknown,
		};

		public object Convert(object value, Type targetType, object parameter, string language) =>
			value is SiteAvailability availability ? For(availability) : Unknown;

		public object ConvertBack(object value, Type targetType, object parameter, string language) =>
			throw new NotSupportedException();
	}
}
