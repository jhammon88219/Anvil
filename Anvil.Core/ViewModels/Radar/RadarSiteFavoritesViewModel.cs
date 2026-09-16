using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// The user's HOME radar site and FAVORITE sites. Owns the persisted ids (<see cref="AppSettings.HomeSiteId"/>,
	/// <see cref="AppSettings.FavoriteSiteIds"/>), mirrors them onto the shared <see cref="RadarSiteRow"/>s
	/// (<c>IsHome</c>/<c>IsFavorite</c>, so the Atlas rows light without a lookup), and is the one place a
	/// site gets LOADED from a list: the tools tier's site picker (<see cref="Pick"/>) and the Atlas's Load
	/// button both funnel through <see cref="LoadOnMap"/> into <see cref="RadarViewModel.SelectedRadarOption"/>
	/// — the same pipeline a marker click uses.
	/// </summary>
	/// <remarks>
	/// ⚠️ Home and favorite are INDEPENDENT flags. Home is never listed twice: <see cref="PinnedSites"/> puts
	/// it first and leaves it out of the favorites that follow, whether or not it is also starred.
	/// ⚠️ PastCast: site-picker picks are OFF (<see cref="CanPickSites"/>) — a pick there would re-target the
	/// replay window at another site, which is not what "go home" means.
	/// ⚠️ The map markers show NONE of this, by decision: the keys are too small to carry another badge.
	/// </remarks>
	public sealed class RadarSiteFavoritesViewModel : ObservableObject
	{
		private readonly RadarViewModel _radar;
		private readonly ISettingsService _settings;
		private readonly IMapService _mapService;
		private bool _launchHandled;

		public RadarSiteFavoritesViewModel(RadarViewModel radar, ISettingsService settings, IMapService mapService)
		{
			_radar = radar;
			_settings = settings;
			_mapService = mapService;

			var favoriteIds = new HashSet<string>(settings.Settings.FavoriteSiteIds, StringComparer.OrdinalIgnoreCase);
			foreach (var row in _radar.RadarSiteRows)
			{
				row.IsFavorite = favoriteIds.Contains(row.Id);
				row.IsHome = string.Equals(row.Id, settings.Settings.HomeSiteId, StringComparison.OrdinalIgnoreCase);
			}
			HomeSite = _radar.RadarSiteRows.FirstOrDefault(r => r.IsHome);

			PinnedSites = new ObservableCollection<RadarSiteRow>();
			HomeSites = new ObservableCollection<RadarSiteRow>();
			FavoriteSites = new ObservableCollection<RadarSiteRow>();
			RebuildPinned();

			_radar.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName is nameof(RadarViewModel.SelectedRadarOption))
				{
					OnPropertyChanged(nameof(LoadedSite));
				}
				else if (e.PropertyName is nameof(RadarViewModel.IsPastEventMode))
				{
					RaisePickState();
				}
				else if (e.PropertyName is nameof(RadarViewModel.ShowTdwrs) or nameof(RadarViewModel.ShowResearchRadars))
				{
					// A hidden network's sites leave the picker (RadarViewModel.IsNetworkShown). The Atlas rebuilds
					// off the same radar change itself, so no PinnedChanged here.
					RebuildPinned();
				}
			};
		}

		/// <summary>Raised after home or the favorite set changes, so lists sectioned over them (the Radar
		/// Atlas) can rebuild.</summary>
		public event EventHandler? PinnedChanged;

		/// <summary>Home first, then the favorites in the order they were starred (home excluded). The Radar
		/// Atlas sections off it.</summary>
		public ObservableCollection<RadarSiteRow> PinnedSites { get; }

		/// <summary>The home site alone (0 or 1 rows) — the site picker's PINNED section.</summary>
		public ObservableCollection<RadarSiteRow> HomeSites { get; }

		/// <summary>The favorites, home excluded, in starred order — the site picker's scrolling section.</summary>
		public ObservableCollection<RadarSiteRow> FavoriteSites { get; }

		/// <summary>True when there is neither a home site nor a favorite (the picker shows a hint instead).</summary>
		public bool IsEmpty => PinnedSites.Count == 0;

		public RadarSiteRow? HomeSite { get; private set; }

		public bool HasHome => HomeSite is not null;

		/// <summary>The row for the radar site on the map right now, whether or not it is home or a favorite —
		/// the site picker's FACE. Null with no site loaded.</summary>
		public RadarSiteRow? LoadedSite => _radar.SelectedRadarOption?.Site is { } site
			? _radar.RadarSiteRows.FirstOrDefault(r => r.Site == site)
			: null;

		/// <summary>False in PastCast — see the class remarks.</summary>
		public bool CanPickSites => !_radar.IsPastEventMode;

		/// <summary>Whether the home site's network is switched on in Settings → Radar.</summary>
		private bool IsHomeShown => HomeSite is { } home && _radar.IsNetworkShown(home.Site);

		/// <summary>Says WHY when the picker can't act — the host puts it on a wrapper so it shows while the
		/// picker is disabled.</summary>
		public string PickerToolTip =>
			!CanPickSites ? "Site picks are off in PastCast"
			: IsEmpty ? "Your radar sites — set a home site or star favorites in the Radar Atlas"
			: "Your radar sites — pick one to load it and fly there";

		/// <summary>One line for Settings → Radar.</summary>
		public string HomeSiteSummary => HomeSite is { } home
			? $"Home site: {home.Id} · {home.Name}"
			: "No home site yet — set one from the Radar Atlas.";

		/// <summary>Load the home site when the app opens. PERSISTED; default off.</summary>
		public bool LoadHomeOnLaunch
		{
			get => _settings.Settings.LoadHomeOnLaunch;
			set
			{
				if (_settings.Settings.LoadHomeOnLaunch == value) return;
				_settings.Settings.LoadHomeOnLaunch = value; // persists (auto-save)
				OnPropertyChanged();
			}
		}

		/// <summary>Star or un-star a site.</summary>
		public void ToggleFavorite(RadarSiteRow row)
		{
			row.IsFavorite = !row.IsFavorite;

			// A NEW list every time — see the ⚠️ on AppSettings.FavoriteSiteIds.
			var ids = _settings.Settings.FavoriteSiteIds
				.Where(id => !string.Equals(id, row.Id, StringComparison.OrdinalIgnoreCase))
				.ToList();
			if (row.IsFavorite)
			{
				ids.Add(row.Id);
			}
			_settings.Settings.FavoriteSiteIds = ids;

			RebuildPinned();
			PinnedChanged?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>Make a site home, or clear home when it already is. Only one home: the old one is released.</summary>
		public void ToggleHome(RadarSiteRow row)
		{
			var becomesHome = !row.IsHome;
			if (HomeSite is { } old)
			{
				old.IsHome = false;
			}
			row.IsHome = becomesHome;
			HomeSite = becomesHome ? row : null;
			_settings.Settings.HomeSiteId = HomeSite?.Id ?? string.Empty;

			OnPropertyChanged(nameof(HomeSite));
			OnPropertyChanged(nameof(HasHome));
			OnPropertyChanged(nameof(HomeSiteSummary));
			RebuildPinned();
			PinnedChanged?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>The site picker: load a home/favorite site and fly to it. ⚠️ Picking the site that is ALREADY
		/// loaded still flies there — that is how you get back to it after panning away.</summary>
		public void Pick(RadarSiteRow row)
		{
			if (CanPickSites && _radar.IsNetworkShown(row.Site))
			{
				LoadOnMap(row);
			}
		}

		/// <summary>Load a site's radar loop (same pipeline a marker click uses) and fly to it — a site picked
		/// from a list isn't on screen the way a clicked marker is. Callers own their own gating.</summary>
		public void LoadOnMap(RadarSiteRow row)
		{
			var option = _radar.RadarOptions.FirstOrDefault(o => o.Site == row.Site);
			if (option is null) return;

			_radar.SelectedRadarOption = option;
			_ = _mapService.FlyToAsync(row.Site.Longitude, row.Site.Latitude, 7);
		}

		/// <summary>Launch behaviour. Once per app run — called last in <c>MapViewModel.OnMapsReadyAsync</c>, so
		/// the markers exist and an isolation camera replay can't override the fly-to.</summary>
		public Task OnMapsReadyAsync()
		{
			if (_launchHandled) return Task.CompletedTask;
			_launchHandled = true;

			if (LoadHomeOnLaunch && IsHomeShown && CanPickSites)
			{
				LoadOnMap(HomeSite!);
			}
			return Task.CompletedTask;
		}

		private void RebuildPinned()
		{
			var byId = new Dictionary<string, RadarSiteRow>(StringComparer.OrdinalIgnoreCase);
			foreach (var row in _radar.RadarSiteRows)
			{
				byId.TryAdd(row.Id, row);
			}

			// A hidden network's sites stay home / starred (and persisted) but aren't listed until it's shown again.
			PinnedSites.Clear();
			HomeSites.Clear();
			FavoriteSites.Clear();
			if (HomeSite is { } home && _radar.IsNetworkShown(home.Site))
			{
				PinnedSites.Add(home);
				HomeSites.Add(home);
			}
			foreach (var id in _settings.Settings.FavoriteSiteIds)
			{
				// Ids for sites this build doesn't have stay persisted but aren't shown.
				if (byId.TryGetValue(id, out var row) && !row.IsHome && _radar.IsNetworkShown(row.Site)
					&& !PinnedSites.Contains(row))
				{
					PinnedSites.Add(row);
					FavoriteSites.Add(row);
				}
			}
			OnPropertyChanged(nameof(IsEmpty));
			OnPropertyChanged(nameof(PickerToolTip));
		}

		private void RaisePickState()
		{
			OnPropertyChanged(nameof(CanPickSites));
			OnPropertyChanged(nameof(PickerToolTip));
		}
	}
}
