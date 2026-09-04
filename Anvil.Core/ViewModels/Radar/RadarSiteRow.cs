using Anvil.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// A presentation row for the dock "Radar Sites" list. Wraps an immutable
	/// <see cref="RadarSite"/> with the observable, view-facing state the list needs so its rows
	/// can render the same states as the on-map site buttons: <see cref="IsOffline"/> drives the
	/// "down" (no recent data) look and <see cref="StatusLabel"/> spells it out. (Selection itself is
	/// the ListView's own SelectedItem state — the row only carries what selection can't express.)
	/// </summary>
	public sealed class RadarSiteRow : ObservableObject
	{
		public RadarSiteRow(RadarSite site) => Site = site;

		public RadarSite Site { get; }
		public string Id => Site.Id;
		public string Name => Site.Name;

		/// <summary>Human label for the site's network, for the explorer's chip/detail (Operational / Research / TDWR).</summary>
		public string ClassLabel => Site.Class switch
		{
			RadarSiteClass.Research => "Research",
			RadarSiteClass.Tdwr => "TDWR",
			_ => "Operational",
		};

		/// <summary>Antenna coordinates for the explorer detail, e.g. "35.333, -97.278".</summary>
		public string Coords => $"{Site.Latitude:0.000}, {Site.Longitude:0.000}";

		/// <summary>Status label mirroring the marker state ("Online" / "Offline").</summary>
		public string StatusLabel => _isOffline ? "Offline" : "Online";

		private bool _isOffline;

		/// <summary>True when the site has no recent data in the feed; renders as a down row.</summary>
		public bool IsOffline
		{
			get => _isOffline;
			set
			{
				if (SetProperty(ref _isOffline, value))
				{
					OnPropertyChanged(nameof(StatusLabel));
				}
			}
		}
	}
}
