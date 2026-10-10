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
	/// ⚠️ The map markers carry no favorite BADGE, by decision: the keys are too small. They can be FILTERED to home +
	/// favorites, though (<see cref="ShowOnlyFavoritesOnMap"/>).
	/// </remarks>
	public sealed class RadarSiteFavoritesViewModel : ObservableObject
	{
		private readonly RadarViewModel _radar;
		private readonly ISettingsService _settings;
		private readonly IMapService _mapService;
		private bool _launchHandled;
		private bool _isMapReady;

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
			SelectedSites = new ObservableCollection<RadarSiteRow>();
			RebuildPinned();
			RebuildSelected();

			_radar.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName is nameof(RadarViewModel.SelectedRadarOption))
				{
					RebuildSelected();
					OnPropertyChanged(nameof(LoadedSite));
				}
				else if (e.PropertyName is nameof(RadarViewModel.IsPastEventMode))
				{
					RaisePickState();
				}
				else if (e.PropertyName is nameof(RadarViewModel.SiteEraKey))
				{
					// A retired site outside the era being viewed leaves the picker (RadarViewModel.IsInEra). The
					// network marker toggles do NOT — they hide map keys only. The Atlas rebuilds
					// off the same radar change itself, so no PinnedChanged here.
					RebuildPinned();
				}
			};
		}

		/// <summary>Raised after home or the favorite set changes, so lists sectioned over them (the Radar
		/// Atlas) can rebuild.</summary>
		public event EventHandler? PinnedChanged;

		/// <summary>Home first, then the favorites in the order they were starred (home not repeated). The Radar
		/// Atlas sections off it, and it is the site picker's scrolling section.</summary>
		public ObservableCollection<RadarSiteRow> PinnedSites { get; }

		/// <summary>The site on the map now (0 or 1 rows, = <see cref="LoadedSite"/>) — the site picker's PINNED
		/// top section, so the picker doubles as "which radar is selected". ⚠️ A selected home/favorite stays in
		/// its place in <see cref="PinnedSites"/> too: removing it would reshuffle the list on every switch.</summary>
		public ObservableCollection<RadarSiteRow> SelectedSites { get; }

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

		/// <summary>Whether the home site existed in the era being viewed (a retired id drops out). The map's
		/// network marker toggles don't affect the picker.</summary>
		private bool IsHomeShown => HomeSite is { } home && _radar.IsInEra(home.Site);

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

		/// <summary>
		/// Show only home + the favorites on the map (radar-sites.js's <c>favorites</c> rule; the loaded site always
		/// shows). PERSISTED via <see cref="AppSettings.ShowOnlyFavoriteSites"/>, default off.
		/// </summary>
		/// <remarks>⚠️ TWO SWITCHES, ONE VALUE: Settings → Radar → Radar site keys AND the site picker's footer both
		/// bind TwoWay to THIS property, so they can never disagree — don't give either its own copy.</remarks>
		public bool ShowOnlyFavoritesOnMap
		{
			get => _settings.Settings.ShowOnlyFavoriteSites;
			set
			{
				if (_settings.Settings.ShowOnlyFavoriteSites == value) return;
				_settings.Settings.ShowOnlyFavoriteSites = value; // persists (auto-save)
				OnPropertyChanged();
				OnPropertyChanged(nameof(FavoritesOnlyNote));
				_ = PushFavoritesOnlyAsync();
			}
		}

		/// <summary>The line under the Settings switch — says so when there is nothing to show.</summary>
		public string FavoritesOnlyNote => IsEmpty
			? "You have no home site or favorites yet, so only a loaded site would show. Star sites in the Atlas."
			: "Star sites in the Atlas. The loaded site always shows, and the site picker has the same switch.";

		// The ids the page keeps: home + every favorite, era or not (the page's own era rule hides a retired one).
		private Task PushFavoritesOnlyAsync()
		{
			if (!_isMapReady) return Task.CompletedTask;
			var ids = _settings.Settings.FavoriteSiteIds.ToList();
			if (_settings.Settings.HomeSiteId is { Length: > 0 } home) ids.Add(home);
			return _mapService.SetFavoriteSitesOnlyAsync(ShowOnlyFavoritesOnMap, System.Text.Json.JsonSerializer.Serialize(ids));
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
			_ = PushFavoritesOnlyAsync();
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
			_ = PushFavoritesOnlyAsync();
		}

		/// <summary>The site picker: load a home/favorite site and fly to it. ⚠️ Picking the site that is ALREADY
		/// loaded still flies there — that is how you get back to it after panning away.</summary>
		public void Pick(RadarSiteRow row)
		{
			if (CanPickSites && _radar.IsInEra(row.Site))
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
		public async Task OnMapsReadyAsync()
		{
			// Every map-ready, not once: a held command is replayed whenever the page is (re)built.
			_isMapReady = true;
			await PushFavoritesOnlyAsync();

			if (_launchHandled) return;
			_launchHandled = true;

			// Not when PastCast came back from the last session: a live loop means nothing in replay, and
			// starting one would only be torn down (MapViewModel restores the modes just before this).
			if (LoadHomeOnLaunch && IsHomeShown && CanPickSites && !_radar.IsPastEventMode)
			{
				LoadOnMap(HomeSite!);
			}
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
			if (HomeSite is { } home && _radar.IsInEra(home.Site))
			{
				PinnedSites.Add(home);
			}
			foreach (var id in _settings.Settings.FavoriteSiteIds)
			{
				// Ids for sites this build doesn't have stay persisted but aren't shown.
				if (byId.TryGetValue(id, out var row) && !row.IsHome && _radar.IsInEra(row.Site)
					&& !PinnedSites.Contains(row))
				{
					PinnedSites.Add(row);
				}
			}
			OnPropertyChanged(nameof(IsEmpty));
			OnPropertyChanged(nameof(PickerToolTip));
			OnPropertyChanged(nameof(FavoritesOnlyNote));
		}

		private void RebuildSelected()
		{
			SelectedSites.Clear();
			if (LoadedSite is { } loaded)
			{
				SelectedSites.Add(loaded);
			}
		}

		private void RaisePickState()
		{
			OnPropertyChanged(nameof(CanPickSites));
			OnPropertyChanged(nameof(PickerToolTip));
		}
	}
}
