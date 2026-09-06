using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Anvil.Converters
{
	/// <summary>
	/// Maps <see cref="ViewModels.RadarSiteRow.IsOffline"/> (bool) to a status-dot brush: offline = red,
	/// online = green. Used by the Radar Site Explorer's list + detail status dots.
	/// </summary>
	/// <remarks>
	/// ⚠️ FIXED COLOURS, NOT THEME BRUSHES — availability is DATA, and by the membership rule (docs/theming.md)
	/// a colour that tells you what something IS is never the theme's. These are the SAME two values the
	/// on-map site key's availability square uses (radar-sites.js, where they are literals for the same
	/// reason), so the Explorer's dot and the marker on the map cannot disagree about what green means. They
	/// read on both the light and the dark panel surface.
	///
	/// ⚠️ It used to resolve SystemFillColorSuccessBrush / SystemFillColorCriticalBrush through
	/// <c>Application.Current.Resources.TryGetValue</c>, and that was doubly wrong: theme-varying for a
	/// signal that must not vary, AND resolved against <c>Application.RequestedTheme</c> — the OS theme,
	/// settable only before content loads — while the app pins its palette on the root element's
	/// <c>RequestedTheme</c> (MainWindow.ApplyAppTheme). So on a dark-mode machine the light theme drew the
	/// dark theme's dots. A converter has no element, so it can never resolve a theme brush correctly; if a
	/// converter ever seems to need one, that is the signal the value belongs in a visual state instead.
	/// </remarks>
	public sealed class OfflineToBrushConverter : IValueConverter
	{
		// Shared instances: one brush each, not one per row per refresh.
		private static readonly SolidColorBrush Online = new(ColorHelper.FromArgb(0xFF, 0x3F, 0xB9, 0x50));
		private static readonly SolidColorBrush Offline = new(ColorHelper.FromArgb(0xFF, 0xF8, 0x51, 0x49));

		public object Convert(object value, Type targetType, object parameter, string language) =>
			value is bool isOffline && isOffline ? Offline : Online;

		public object ConvertBack(object value, Type targetType, object parameter, string language) =>
			throw new NotSupportedException();
	}
}
