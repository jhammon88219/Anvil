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
	/// site gets LOADED from a list: the bar's Home key, the Atlas flyout and the Atlas's Load button all
	/// funnel through <see cref="LoadOnMap"/> into <see cref="RadarViewModel.SelectedRadarOption"/> — the same
	/// pipeline a marker click uses.
	/// </summary>
	/// <remarks>
	/// ⚠️ Home and favorite are INDEPENDENT flags. Home is never listed twice: <see cref="PinnedSites"/> puts
	/// it first and leaves it out of the favorites that follow, whether or not it is also starred.
	/// ⚠️ PastCast: Home and flyout picks are OFF (<see cref="CanPickSites"/>) — a pick there would re-target the
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
			RebuildPinned();

			_radar.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName is nameof(RadarViewModel.SelectedRadarOption) or nameof(RadarViewModel.IsPastEventMode))
				{
					RaiseHomeState();
				}
				else if (e.PropertyName is nameof(RadarViewModel.ShowTdwrs) or nameof(RadarViewModel.ShowResearchRadars))
				{
					// A hidden network's sites leave the flyout and disable Home (RadarViewModel.IsNetworkShown).
					// The Atlas rebuilds off the same radar change itself, so no PinnedChanged here.
					RebuildPinned();
					RaiseHomeState();
				}
			};
		}

		/// <summary>Raised after home or the favorite set changes, so lists sectioned over them (the Radar
		/// Atlas) can rebuild.</summary>
		public event EventHandler? PinnedChanged;

		/// <summary>Home first, then the favorites in the order they were starred (home excluded). The Atlas
		/// flyout\'s list.</summary>
		public ObservableCollection<RadarSiteRow> PinnedSites { get; }

		/// <summary>True when there is neither a home site nor a favorite (the flyout shows a hint instead).</summary>
		public bool IsEmpty => PinnedSites.Count == 0;

		public RadarSiteRow? HomeSite { get; private set; }

		public bool HasHome => HomeSite is not null;

		/// <summary>Whether the home site is the map's radar site right now (lights the Home key).</summary>
		public bool IsHomeOnMap => HomeSite?.Site is { } s && _radar.SelectedRadarOption?.Site == s;

		/// <summary>False in PastCast — see the class remarks.</summary>
		public bool CanPickSites => !_radar.IsPastEventMode;

		/// <summary>Whether the home site's network is switched on in Settings → Radar.</summary>
		private bool IsHomeShown => HomeSite is { } home && _radar.IsNetworkShown(home.Site);

		public bool IsHomeKeyEnabled => IsHomeShown && CanPickSites;

		/// <summary>Always says WHY when the key can't act — it's hosted on a wrapper so it shows while disabled.</summary>
		public string HomeKeyToolTip =>
			HomeSite is not { } home ? "Add a home radar site first — open the Atlas and choose Set as home"
			: !IsHomeShown ? $"{home.Id} is a {home.ClassLabel} site — turn on {NetworkToggleName(home)} in Settings → Radar to use Home"
			: !CanPickSites ? "Home isn't available in PastCast"
			: IsHomeOnMap ? $"Home — {home.Id} is on the map"
			: $"Home — load {home.Id} ({home.Name})";

		private static string NetworkToggleName(RadarSiteRow row) =>
			row.Site.Class == Models.RadarSiteClass.Tdwr ? "Show TDWRs" : "Show research radars";

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
			RaiseHomeState();
			RebuildPinned();
			PinnedChanged?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>The Home key: load the home site. No-op when it's already on the map, or picks are off.</summary>
		public void GoHome()
		{
			if (IsHomeKeyEnabled && !IsHomeOnMap)
			{
				LoadOnMap(HomeSite!);
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

			if (LoadHomeOnLaunch && IsHomeKeyEnabled)
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
			if (HomeSite is { } home && _radar.IsNetworkShown(home.Site))
			{
				PinnedSites.Add(home);
			}
			foreach (var id in _settings.Settings.FavoriteSiteIds)
			{
				// Ids for sites this build doesn't have stay persisted but aren't shown.
				if (byId.TryGetValue(id, out var row) && !row.IsHome && _radar.IsNetworkShown(row.Site)
					&& !PinnedSites.Contains(row))
				{
					PinnedSites.Add(row);
				}
			}
			OnPropertyChanged(nameof(IsEmpty));
		}

		private void RaiseHomeState()
		{
			OnPropertyChanged(nameof(IsHomeOnMap));
			OnPropertyChanged(nameof(CanPickSites));
			OnPropertyChanged(nameof(IsHomeKeyEnabled));
			OnPropertyChanged(nameof(HomeKeyToolTip));
		}
	}
}
