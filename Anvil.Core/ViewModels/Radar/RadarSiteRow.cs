using Anvil.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// A presentation row for one radar site, shared by the Radar Atlas, the Atlas flyout and the dev site
	/// sweep. Wraps an immutable <see cref="RadarSite"/> with the observable state those lists need, so they
	/// render the same states as the on-map site keys.
	/// </summary>
	/// <remarks>
	/// ⚠️ TWO DIFFERENT QUESTIONS, kept in two different words: <see cref="ClassLabel"/> is the NETWORK
	/// (NEXRAD / TDWR / Research) and never changes; <see cref="StatusLabel"/> is whether data is flowing. The
	/// network used to read "Operational", which looked like a status sitting beside a red Offline dot.
	/// ⚠️ <see cref="Availability"/> has ONE writer — RadarViewModel's SITE AVAILABILITY block, through
	/// <see cref="SetAvailability"/> — which pushes the same state to the map markers in the same breath.
	/// </remarks>
	public sealed class RadarSiteRow : ObservableObject
	{
		public RadarSiteRow(RadarSite site) => Site = site;

		public RadarSite Site { get; }
		public string Id => Site.Id;
		public string Name => Site.Name;

		/// <summary>The site's NETWORK (NEXRAD / Research / TDWR) — not its status; see the remarks.</summary>
		public string ClassLabel => Site.Class switch
		{
			RadarSiteClass.Research => "Research",
			RadarSiteClass.Tdwr => "TDWR",
			_ => "NEXRAD",
		};

		/// <summary>Antenna coordinates for the Atlas detail, e.g. "35.333, -97.278".</summary>
		public string Coords => $"{Site.Latitude:0.000}, {Site.Longitude:0.000}";

		private SiteAvailability _availability;
		private bool _isReplayDay;

		/// <summary>Unknown until checked, then Online / Offline (RadarSiteStatus is the freshness rule).</summary>
		public SiteAvailability Availability => _availability;

		/// <summary>True only when KNOWN to be down. Unknown is not offline, so "Online only" keeps unchecked
		/// sites rather than hiding the whole network before the first pass.</summary>
		public bool IsOffline => _availability == SiteAvailability.Offline;

		/// <summary>True while <see cref="Availability"/> describes the PastCast REPLAY DAY (had data that day)
		/// rather than the live feed — the two must never share a label.</summary>
		public bool IsReplayDay => _isReplayDay;

		public string StatusLabel => _availability switch
		{
			SiteAvailability.Unknown => "Checking…",
			SiteAvailability.Online => _isReplayDay ? "Data on replay day" : "Online",
			_ => _isReplayDay ? "No data on replay day" : "Offline",
		};

		/// <summary>The one write path — see the remarks for who may call it.</summary>
		internal void SetAvailability(SiteAvailability availability, bool isReplayDay)
		{
			if (_availability == availability && _isReplayDay == isReplayDay)
			{
				return;
			}
			_availability = availability;
			_isReplayDay = isReplayDay;
			OnPropertyChanged(nameof(Availability));
			OnPropertyChanged(nameof(IsOffline));
			OnPropertyChanged(nameof(IsReplayDay));
			OnPropertyChanged(nameof(StatusLabel));
		}

		private bool _isFavorite;

		/// <summary>Starred by the user. Written ONLY by <see cref="RadarSiteFavoritesViewModel"/>, which also
		/// persists it.</summary>
		public bool IsFavorite
		{
			get => _isFavorite;
			set => SetProperty(ref _isFavorite, value);
		}

		private bool _isHome;

		/// <summary>The user's home site (at most one row). Written ONLY by <see cref="RadarSiteFavoritesViewModel"/>.</summary>
		public bool IsHome
		{
			get => _isHome;
			set => SetProperty(ref _isHome, value);
		}
	}
}
