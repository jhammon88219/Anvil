using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// View model for the Radar Atlas — a searchable/filterable master–detail browser over the
	/// whole radar network the app can access (operational WSR-88D, research/test, TDWR). A dedicated
	/// subsystem VM constructed by the <see cref="MapViewModel"/> coordinator; it reuses existing state
	/// rather than duplicating it:
	/// <list type="bullet">
	/// <item>the list is a filtered view over <see cref="RadarViewModel.RadarSiteRows"/> (the SAME row
	/// instances whose <c>IsOffline</c> is kept current by the radar VM's status loop — one source of
	/// truth for online/offline);</item>
	/// <item>loading a site funnels into <see cref="RadarViewModel.SelectedRadarOption"/>, i.e. the exact
	/// same loop-load pipeline a map-marker click uses.</item>
	/// </list>
	/// The detail pane shows instant static facts + live status + distance, and fetches the selected
	/// site's latest-scan time and VCP/scan mode on demand via
	/// <see cref="ILevel2RadarService.GetLatestScanAsync"/>.
	/// </summary>
	public sealed class RadarAtlasViewModel : ObservableObject
	{
		private readonly RadarViewModel _radar;
		private readonly MarkersViewModel _markers;
		private readonly ILevel2RadarService _radarService;
		private readonly RadarSiteFavoritesViewModel _favorites;
		private readonly SiteUsageTracker _usage;
		private readonly RadarNwsStatusViewModel _nws;

		public RadarAtlasViewModel(RadarViewModel radar, MarkersViewModel markers,
			ILevel2RadarService radarService, RadarSiteFavoritesViewModel favorites, SiteUsageTracker usage,
			RadarNwsStatusViewModel nws)
		{
			_radar = radar;
			_markers = markers;
			_radarService = radarService;
			_favorites = favorites;
			_usage = usage;
			_nws = nws;

			// A check landing (or the minute rolling, which moves "27 hr ago") re-words the NWS section.
			_nws.Changed += (_, _) => OnPropertyChanged(nameof(NwsDetail));

			// Any recorded change can move the selected site's numbers — or its RANK, which depends on every site.
			_usage.Changed += (_, _) => RaiseUsage();

			FilteredSites = new ObservableCollection<RadarSiteRow>();
			SiteGroups = new ObservableCollection<RadarSiteGroup>();
			BuildPlaceOptions();
			RebuildFiltered();

			// Starring a site or moving home re-sections the list (Home / Favorites / All sites).
			_favorites.PinnedChanged += (_, _) => RebuildFiltered();

			// A status pass (or one site's evidence) can move a row across a status filter.
			_radar.SiteAvailabilityChanged += (_, _) =>
			{
				if (HasStatusFilter)
				{
					RebuildFiltered();
				}
			};

			// Dropping or placing the location marker decides whether "Nearest" can be sorted by at all —
			// and if it's the live sort, the order itself changes underneath us.
			_markers.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(MarkersViewModel.HasUserLocationMarker))
				{
					OnPropertyChanged(nameof(CanSortByDistance));
					if (_sortMode == AtlasSortMode.Nearest)
					{
						SortMode = CanSortByDistance ? AtlasSortMode.Nearest : AtlasSortMode.Icao;
						RebuildFiltered();
					}
				}
			};

			// For the site the loop is showing, our scan read-out IS the loop's — so re-raise it whenever the
			// loop's frame time / mode / selection changes, and the two stay in lock-step (a new live frame
			// updates both at once) instead of the Atlas freezing at whatever it fetched on selection.
			_radar.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName is nameof(RadarViewModel.NewestLoadedFrameTime)
					or nameof(RadarViewModel.RadarModeText)
					or nameof(RadarViewModel.SelectedRadarOption)
					or nameof(RadarViewModel.HasRadarLoop))
				{
					RaiseScan();
				}

				if (e.PropertyName is nameof(RadarViewModel.SelectedRadarOption))
				{
					FollowRadarSelection();
				}

				// The status words name a different question in PastCast — and a chip may be showing them.
				if (e.PropertyName is nameof(RadarViewModel.IsPastEventMode))
				{
					OnPropertyChanged(nameof(StatusOnlineLabel));
					OnPropertyChanged(nameof(StatusOfflineLabel));
					OnPropertyChanged(nameof(StatusUncheckedLabel));
					RebuildChips();
				}

				// Settings → Radar decides which networks exist in the list, same as on the map — and the era
				// decides whether a retired site (KLIX) does.
				if (e.PropertyName is nameof(RadarViewModel.ShowTdwrs) or nameof(RadarViewModel.ShowResearchRadars)
					or nameof(RadarViewModel.SiteEraKey))
				{
					OnPropertyChanged(nameof(IsTdwrFilterEnabled));
					OnPropertyChanged(nameof(IsResearchFilterEnabled));
					RebuildFiltered();
				}
			};

			FollowRadarSelection();
		}

		/// <summary>
		/// Mirrors the map's radar pick into the list: whichever way a site gets selected (marker click, this
		/// Atlas, a saved event) the Atlas shows THAT site, so opening it lands on the loaded radar.
		/// "None" leaves the list selection alone — clearing the radar isn't a reason to blank the detail.
		/// </summary>
		private void FollowRadarSelection()
		{
			if (_radar.SelectedRadarOption?.Site is { } site)
			{
				SelectedSite = _radar.RadarSiteRows.FirstOrDefault(r => r.Site == site) ?? _selectedSite;
			}
			OnPropertyChanged(nameof(CanLoadOnMap));
			OnPropertyChanged(nameof(LoadButtonText));
		}

		// ── Search + filters ─────────────────────────────────────────────────────────────────────
		private string _searchText = string.Empty;

		/// <summary>ICAO / name search text (case-insensitive substring). Refilters on change.</summary>
		public string SearchText
		{
			get => _searchText;
			set
			{
				if (SetProperty(ref _searchText, value ?? string.Empty))
				{
					RebuildFiltered();
				}
			}
		}

		// ── Network ──────────────────────────────────────────────────────────────────────────────
		// Independently tickable, unlike the single-pick combo this replaced: "NEXRAD + TDWR but not
		// research" is a question you can now ask. ⚠️ ALL THREE DEFAULT TRUE — "no filter" here means
		// everything ticked, not nothing, so an unticked box always reads as a narrowing.
		private bool _filterNexrad = true;
		private bool _filterTdwr = true;
		private bool _filterResearch = true;

		public bool FilterNexrad
		{
			get => _filterNexrad;
			set { if (SetProperty(ref _filterNexrad, value)) RebuildFiltered(); }
		}

		public bool FilterTdwr
		{
			get => _filterTdwr;
			set { if (SetProperty(ref _filterTdwr, value)) RebuildFiltered(); }
		}

		public bool FilterResearch
		{
			get => _filterResearch;
			set { if (SetProperty(ref _filterResearch, value)) RebuildFiltered(); }
		}

		/// <summary>
		/// The networks Settings → Radar currently permits, each with its tick state and label.
		/// </summary>
		/// <remarks>⚠️ THE reference for "is the network filter narrowed": narrowing is measured against what
		/// is SELECTABLE right now, not against all three. Otherwise turning TDWRs off in Settings would leave
		/// a permanent "NEXRAD +1" chip the user could never clear, because a hidden network can't be re-ticked.</remarks>
		private List<(RadarSiteClass Class, bool Ticked, string Label)> EnabledNetworks()
		{
			var list = new List<(RadarSiteClass, bool, string)>
			{
				(RadarSiteClass.Operational, _filterNexrad, "NEXRAD"),
			};
			if (_radar.ShowResearchRadars) list.Add((RadarSiteClass.Research, _filterResearch, "Research"));
			if (_radar.ShowTdwrs) list.Add((RadarSiteClass.Tdwr, _filterTdwr, "TDWR"));
			return list;
		}

		// ── Status ───────────────────────────────────────────────────────────────────────────────
		// Three independent ticks, not the old "Online only" checkbox: during an outage the useful question
		// is "what's DOWN", which a single boolean couldn't ask. NONE ticked means no status filter at all.
		private bool _statusOnline;
		private bool _statusOffline;
		private bool _statusUnchecked;

		public bool StatusOnline
		{
			get => _statusOnline;
			set { if (SetProperty(ref _statusOnline, value)) RebuildFiltered(); }
		}

		public bool StatusOffline
		{
			get => _statusOffline;
			set { if (SetProperty(ref _statusOffline, value)) RebuildFiltered(); }
		}

		public bool StatusUnchecked
		{
			get => _statusUnchecked;
			set { if (SetProperty(ref _statusUnchecked, value)) RebuildFiltered(); }
		}

		private bool HasStatusFilter => _statusOnline || _statusOffline || _statusUnchecked;

		// ⚠️ The status words name what the state MEANS right now — the live feed, or the PastCast replay
		// day. Same rule the row's StatusLabel follows; the two must never disagree.
		public string StatusOnlineLabel => _radar.IsPastEventMode ? "Data on replay day" : "Online";
		public string StatusOfflineLabel => _radar.IsPastEventMode ? "No data on replay day" : "Offline";
		public string StatusUncheckedLabel => "Not yet checked";

		// ── Place ────────────────────────────────────────────────────────────────────────────────
		// Region and state both come from RadarSite.State, which tools/make_site_states.py stamps in. The
		// option lists are built ONCE from every site the app knows: a list that shrank as you filtered
		// would keep moving the entry you were reaching for.
		private const string AllRegions = "All regions";
		private const string AllStates = "All states";

		/// <summary>"All regions" + every region that actually has a site.</summary>
		public ObservableCollection<string> RegionOptions { get; } = new() { AllRegions };

		/// <summary>"All states" + every state code that actually has a site, alphabetical.</summary>
		public ObservableCollection<string> StateOptions { get; } = new() { AllStates };

		private string _selectedRegion = AllRegions;

		public string SelectedRegion
		{
			get => _selectedRegion;
			set
			{
				if (SetProperty(ref _selectedRegion, string.IsNullOrEmpty(value) ? AllRegions : value))
				{
					RebuildFiltered();
				}
			}
		}

		private string _selectedState = AllStates;

		public string SelectedState
		{
			get => _selectedState;
			set
			{
				if (SetProperty(ref _selectedState, string.IsNullOrEmpty(value) ? AllStates : value))
				{
					RebuildFiltered();
				}
			}
		}

		// Region and state are independent, so a contradictory pair (Alaska + FL) is expressible and yields
		// nothing. That's deliberate: the empty list plus two clearable chips explains itself, where silently
		// re-pointing one of them would leave the user staring at a filter they didn't set.
		private void BuildPlaceOptions()
		{
			foreach (var region in _radar.RadarSiteRows
				.Select(r => r.Region)
				.Where(r => r is not null)
				.Select(r => r!.Value)
				.Distinct()
				.OrderBy(r => (int)r))
			{
				RegionOptions.Add(RadarSiteRegions.DisplayName(region));
			}

			foreach (var state in _radar.RadarSiteRows
				.Select(r => r.State)
				.Where(s => !string.IsNullOrEmpty(s))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
			{
				StateOptions.Add(state!);
			}
		}

		// ── Sort ─────────────────────────────────────────────────────────────────────────────────
		private AtlasSortMode _sortMode = AtlasSortMode.Icao;

		/// <summary>Order of the "All sites" section. Home and Favorites are never re-ordered.</summary>
		public AtlasSortMode SortMode
		{
			get => _sortMode;
			private set
			{
				if (SetProperty(ref _sortMode, value))
				{
					OnPropertyChanged(nameof(SortIndex));
				}
			}
		}

		/// <summary>The sort as a RadioButtons index (0 ICAO / 1 Name / 2 Nearest).</summary>
		public int SortIndex
		{
			get => (int)_sortMode;
			set
			{
				// RadioButtons reports -1 while it rebuilds its items; that isn't a user choice.
				if (value < 0 || value > (int)AtlasSortMode.Nearest) return;
				var mode = (AtlasSortMode)value;
				if (mode == AtlasSortMode.Nearest && !CanSortByDistance) return;
				if (mode != _sortMode)
				{
					SortMode = mode;
					RebuildFiltered();
				}
			}
		}

		/// <summary>Whether "Nearest" can be picked — it needs somewhere to measure from.</summary>
		public bool CanSortByDistance => _markers.UserLocationMarker is not null;

		private bool _favoritesOnly;

		/// <summary>When set, shows only the home site and favorites.</summary>
		public bool FavoritesOnly
		{
			get => _favoritesOnly;
			set
			{
				if (SetProperty(ref _favoritesOnly, value))
				{
					RebuildFiltered();
				}
			}
		}

		/// <summary>The filtered site rows, FLAT and in display order (shared instances from the radar VM). The
		/// list binds <see cref="SiteGroups"/>; this is the membership the selection guard checks.</summary>
		public ObservableCollection<RadarSiteRow> FilteredSites { get; }

		/// <summary>The same rows as the list's SECTIONS (Home / Favorites / All sites) for a grouped
		/// CollectionViewSource. Empty sections are omitted; a row is in exactly one.</summary>
		public ObservableCollection<RadarSiteGroup> SiteGroups { get; }

		/// <summary>Every site Settings → Radar currently permits — the denominator of the count.</summary>
		private int VisibleSiteCount => _radar.RadarSiteRows.Count(r => _radar.IsNetworkShown(r.Site));

		/// <summary>
		/// The feedback row's count. It changes PHRASING, not just numbers: "204 sites" while nothing narrows
		/// the list, "118 of 204" once something does — so the line always says something true, and the row
		/// is never empty (it's permanent, and an empty permanent row reads as a bug).
		/// </summary>
		public string ResultCountText => IsNarrowed
			? $"{FilteredSites.Count} of {VisibleSiteCount}"
			: $"{VisibleSiteCount} sites";

		/// <summary>The network filter's TDWR / Research entries grey out while Settings → Radar hides that network.</summary>
		public bool IsTdwrFilterEnabled => _radar.ShowTdwrs;
		public bool IsResearchFilterEnabled => _radar.ShowResearchRadars;

		// ── The feedback row: chips + clear ──────────────────────────────────────────────────────
		/// <summary>The active filters, one chip per GROUP, rebuilt with the list. See <see cref="AtlasFilterChip"/>
		/// for which filters earn a chip and which don't.</summary>
		public ObservableCollection<AtlasFilterChip> Chips { get; } = new();

		/// <summary>Whether anything is FILTERING the list. ⚠️ Sort is not a filter and is excluded — it's
		/// what "Clear all" acts on and what the button's enablement reads.</summary>
		public bool HasActiveFilters =>
			EnabledNetworks().Any(n => !n.Ticked) || HasStatusFilter || _favoritesOnly ||
			_selectedRegion != AllRegions || _selectedState != AllStates;

		/// <summary>Whether ANYTHING is cutting the list down, search included — the count's phrasing rule.</summary>
		private bool IsNarrowed => HasActiveFilters || _searchText.Trim().Length > 0;

		/// <summary>A chip's ✕ — clears the whole group it stands for.</summary>
		public void ClearChip(AtlasFilterChip chip)
		{
			switch (chip.Kind)
			{
				case AtlasFilterKind.Network:
					// Back to every network Settings allows; a hidden one's flag is left as it was, since it
					// isn't part of the narrowing (see EnabledNetworks).
					_filterNexrad = true;
					if (_radar.ShowResearchRadars) _filterResearch = true;
					if (_radar.ShowTdwrs) _filterTdwr = true;
					RaiseNetworkFlags();
					break;
				case AtlasFilterKind.Status:
					_statusOnline = _statusOffline = _statusUnchecked = false;
					RaiseStatusFlags();
					break;
				case AtlasFilterKind.Region:
					SetProperty(ref _selectedRegion, AllRegions, nameof(SelectedRegion));
					break;
				case AtlasFilterKind.State:
					SetProperty(ref _selectedState, AllStates, nameof(SelectedState));
					break;
				case AtlasFilterKind.Sort:
					SortMode = AtlasSortMode.Icao;
					break;
			}
			RebuildFiltered();
		}

		/// <summary>
		/// The "Clear all" key beside the Filters flyout. ⚠️ Clears the FILTERS and leaves the sort alone —
		/// a button named for filters yanking the chosen order would be a surprise, and the sort chip staying
		/// put is honest, because that order is still in effect.
		/// </summary>
		public void ClearAllFilters()
		{
			_filterNexrad = true;
			if (_radar.ShowResearchRadars) _filterResearch = true;
			if (_radar.ShowTdwrs) _filterTdwr = true;
			_statusOnline = _statusOffline = _statusUnchecked = false;
			_favoritesOnly = false;
			SetProperty(ref _selectedRegion, AllRegions, nameof(SelectedRegion));
			SetProperty(ref _selectedState, AllStates, nameof(SelectedState));
			RaiseNetworkFlags();
			RaiseStatusFlags();
			OnPropertyChanged(nameof(FavoritesOnly));
			RebuildFiltered();
		}

		private void RaiseNetworkFlags()
		{
			OnPropertyChanged(nameof(FilterNexrad));
			OnPropertyChanged(nameof(FilterTdwr));
			OnPropertyChanged(nameof(FilterResearch));
		}

		private void RaiseStatusFlags()
		{
			OnPropertyChanged(nameof(StatusOnline));
			OnPropertyChanged(nameof(StatusOffline));
			OnPropertyChanged(nameof(StatusUnchecked));
		}

		// One chip per group, labelled by its picks: "NEXRAD" alone, "NEXRAD +1" for two. The compaction is
		// what caps the row — five groups, so five chips, however many boxes are ticked inside them.
		private void RebuildChips()
		{
			Chips.Clear();

			var networks = EnabledNetworks();
			if (networks.Any(n => !n.Ticked))
			{
				var ticked = networks.Where(n => n.Ticked).Select(n => n.Label).ToList();
				Chips.Add(new AtlasFilterChip(AtlasFilterKind.Network,
					ticked.Count == 0 ? "No network" : Summarize(ticked)));
			}

			if (HasStatusFilter)
			{
				var picked = new List<string>();
				if (_statusOnline) picked.Add(StatusOnlineLabel);
				if (_statusOffline) picked.Add(StatusOfflineLabel);
				if (_statusUnchecked) picked.Add(StatusUncheckedLabel);
				Chips.Add(new AtlasFilterChip(AtlasFilterKind.Status, Summarize(picked)));
			}

			if (_selectedRegion != AllRegions)
			{
				Chips.Add(new AtlasFilterChip(AtlasFilterKind.Region, _selectedRegion));
			}

			if (_selectedState != AllStates)
			{
				Chips.Add(new AtlasFilterChip(AtlasFilterKind.State, _selectedState));
			}

			// Sort shows only when it ISN'T the default — a chip for "the order it's always in" is noise.
			if (_sortMode != AtlasSortMode.Icao)
			{
				Chips.Add(new AtlasFilterChip(AtlasFilterKind.Sort,
					_sortMode == AtlasSortMode.Nearest ? "Nearest first" : "By name"));
			}

			static string Summarize(List<string> picks) =>
				picks.Count > 1 ? $"{picks[0]} +{picks.Count - 1}" : picks[0];
		}

		private void RebuildFiltered()
		{
			var search = _searchText.Trim();
			IEnumerable<RadarSiteRow> q = _radar.RadarSiteRows.Where(r => _radar.IsNetworkShown(r.Site));

			// Network. Measured against what Settings permits, so a hidden network's stale flag can't filter
			// anything (its rows are gone already) — see EnabledNetworks.
			var networks = EnabledNetworks();
			if (networks.Any(n => !n.Ticked))
			{
				var allowed = networks.Where(n => n.Ticked).Select(n => n.Class).ToHashSet();
				q = q.Where(r => allowed.Contains(r.Site.Class));
			}

			// Status. Nothing ticked = no status filter; otherwise a row must match one of the ticks.
			if (HasStatusFilter)
			{
				q = q.Where(r =>
					(_statusOnline && r.Availability == SiteAvailability.Online) ||
					(_statusOffline && r.Availability == SiteAvailability.Offline) ||
					(_statusUnchecked && r.Availability == SiteAvailability.Unknown));
			}

			if (_selectedRegion != AllRegions)
			{
				q = q.Where(r => r.Region is { } region && RadarSiteRegions.DisplayName(region) == _selectedRegion);
			}

			if (_selectedState != AllStates)
			{
				q = q.Where(r => string.Equals(r.State, _selectedState, StringComparison.OrdinalIgnoreCase));
			}

			if (_favoritesOnly)
			{
				q = q.Where(r => r.IsHome || r.IsFavorite);
			}

			if (search.Length > 0)
			{
				q = q.Where(r =>
					r.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
					r.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
			}

			// Sections. Favorites keep the order they were starred in (the flyout's order); a row lands in ONE
			// section — a ListView can't hold the same item twice.
			var matched = q.ToHashSet();
			var home = _favorites.PinnedSites.Where(r => r.IsHome && matched.Contains(r)).ToList();
			var favorites = _favorites.PinnedSites.Where(r => !r.IsHome && matched.Contains(r)).ToList();
			var rest = SortRows(_radar.RadarSiteRows.Where(r => matched.Contains(r) && !r.IsHome && !r.IsFavorite));

			// ⚠️ FilteredSites clears FIRST: clearing the groups drops the ListView's selection, and its null echo
			// is only ignored while the selected row is absent from FilteredSites (see SelectedSite).
			FilteredSites.Clear();
			SiteGroups.Clear();
			AddSection("Home", home);
			AddSection($"Favorites · {favorites.Count}", favorites);
			AddSection($"All sites · {rest.Count}", rest);

			void AddSection(string header, List<RadarSiteRow> rows)
			{
				if (rows.Count == 0) return;
				foreach (var row in rows)
				{
					FilteredSites.Add(row);
				}
				SiteGroups.Add(new RadarSiteGroup(header, rows));
			}
			RebuildChips();
			OnPropertyChanged(nameof(ResultCountText));
			OnPropertyChanged(nameof(HasActiveFilters));
			// The Clear() dropped the ListView's selection (its null echo was ignored — see SelectedSite);
			// re-raise so a selection that survived the filter is highlighted again.
			OnPropertyChanged(nameof(SelectedSite));
		}

		/// <summary>
		/// Orders the "All sites" section. ⚠️ ONLY that section: Home and Favorites are in the order the user
		/// pinned them, and re-sorting them would throw that away.
		/// </summary>
		private List<RadarSiteRow> SortRows(IEnumerable<RadarSiteRow> rows) => _sortMode switch
		{
			AtlasSortMode.Name => rows
				.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
				.ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
				.ToList(),
			// A site with no distance (no marker, which shouldn't reach here) sinks rather than leading.
			AtlasSortMode.Nearest => rows
				.OrderBy(r => MilesTo(r) ?? double.MaxValue)
				.ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
				.ToList(),
			_ => rows.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToList(),
		};

		/// <summary>Great-circle miles from the user-location marker to a row's site, or null without one.</summary>
		private double? MilesTo(RadarSiteRow row) =>
			_markers.UserLocationMarker is { } u
				? HaversineMiles(u.Latitude, u.Longitude, row.Site.Latitude, row.Site.Longitude)
				: null;

		// ── Selection + detail ───────────────────────────────────────────────────────────────────
		private RadarSiteRow? _selectedSite;
		private int _detailToken; // bumped per selection so a stale async scan-info fetch is ignored

		/// <summary>The selected row driving the detail pane; setting it refreshes the detail + kicks
		/// the on-demand scan-info fetch.</summary>
		/// <remarks>⚠️ A null write while the current row ISN'T in <see cref="FilteredSites"/> is ignored: it's
		/// the ListView echoing "I can't show that" (a filter hides the loaded site, or a rebuild's Clear()),
		/// not the user deselecting — honouring it would lose the map-synced site behind a search.</remarks>
		public RadarSiteRow? SelectedSite
		{
			get => _selectedSite;
			set
			{
				if (value is null && _selectedSite is not null && !FilteredSites.Contains(_selectedSite))
				{
					return;
				}

				if (SetProperty(ref _selectedSite, value))
				{
					IsNwsEarlierExpanded = false; // a new site opens with its older messages folded
					RaiseDetail();
					_ = LoadDetailAsync(value, ++_detailToken);
				}
			}
		}

		/// <summary>Whether a site is selected (detail pane visibility / Load button enablement).</summary>
		public bool HasSelection => _selectedSite is not null;

		/// <summary>Load button enablement: a site is selected AND it isn't already the map's radar site.</summary>
		public bool CanLoadOnMap => _selectedSite?.Site is { } s && _radar.SelectedRadarOption?.Site != s;

		/// <summary>The load button's label — "On map" (disabled) says WHY it can't be pressed.</summary>
		public string LoadButtonText => CanLoadOnMap ? "Load on map" : "On map";

		public string DetailId => _selectedSite?.Id ?? string.Empty;
		public string DetailName => _selectedSite?.Name ?? string.Empty;
		public string DetailClassLabel => _selectedSite?.ClassLabel ?? string.Empty;
		public string DetailCoords => _selectedSite?.Coords ?? string.Empty;

		/// <summary>Great-circle distance from the user-location marker (if any) to the selected site.</summary>
		public string DetailDistanceText
		{
			get
			{
				if (_selectedSite is null || _markers.UserLocationMarker is not { } u) return string.Empty;
				var miles = HaversineMiles(u.Latitude, u.Longitude, _selectedSite.Site.Latitude, _selectedSite.Site.Longitude);
				return $"{miles:0} mi from your location";
			}
		}

		// Scan info (latest scan time + VCP/scan mode). TWO sources, deliberately:
		//   • The site the LOOP is showing — read straight off the radar VM. It already holds the exact
		//     sweep time + mode, so the Atlas and the Selected Site readout agree to the second and
		//     advance together. (Re-fetching it would mean rebuilding the live frame — a ~8-12 s,
		//     tens-of-MB chunks download per click, which would also clobber the shared live-frame cache.)
		//   • Any OTHER site — fetched on demand (GetLatestScanAsync). Nothing is displaying a time for it,
		//     so there's nothing to be out of sync with; the chunks/archive estimate is enough.
		private bool _isLoadingScanInfo;
		private RadarScanInfo? _fetchedScan;   // the on-demand result (non-loaded sites)
		private string _scanStatus = string.Empty; // "No recent data" / "Unavailable" when there's no time

		/// <summary>True while the latest-scan/VCP fetch is in flight (drives the detail spinner). Never
		/// set for the loaded site — its numbers come from the loop for free.</summary>
		public bool IsLoadingScanInfo
		{
			get => _isLoadingScanInfo;
			private set => SetProperty(ref _isLoadingScanInfo, value);
		}

		/// <summary>Whether the selected site is the one the loop is currently showing.</summary>
		private bool IsLoadedSite(RadarSiteRow? row) =>
			row?.Site is { } s && _radar.HasRadarLoop && _radar.SelectedRadarOption?.Site == s;

		/// <summary>Latest scan time + age for the selected site. For the loaded site this IS the loop's
		/// own frame time, so the two readouts can't drift apart.</summary>
		public string LatestScanText
		{
			get
			{
				var t = IsLoadedSite(_selectedSite) ? _radar.NewestLoadedFrameTime : _fetchedScan?.ScanTime;
				if (t is not { } scan) return _scanStatus;
				var local = scan.ToLocalTime();
				return $"{local:g} ({FormatAge(DateTimeOffset.Now - local)})";
			}
		}

		/// <summary>
		/// VCP / scan-mode line for the selected site (from the loop when it's the loaded site). Cut with the
		/// bar's own rule (<see cref="RadarViewModel.ScanStrategyText"/>), so the two read identically — no
		/// trailing "0.5°×N" to contradict the SAILS count.
		/// </summary>
		public string VcpModeText
		{
			get
			{
				if (IsLoadedSite(_selectedSite))
				{
					var mode = RadarViewModel.ScanStrategyText(_radar.RadarModeText);
					return string.IsNullOrEmpty(mode) ? "—" : mode;
				}
				return RadarViewModel.ScanStrategyText(_fetchedScan?.ModeText);
			}
		}

		// ── At-a-glance tiles + the "?" hints ────────────────────────────────────────────────────
		// The tiles are the SAME facts the fields below them show, said shorter: the field sheet is the
		// precise form (a clock time, the full mode line) and the tile is the glanceable one (an age, the
		// VCP alone). Both read the same properties, so they can't disagree.
		//
		// ⚠️ Every hint's words come from RadarGlossary — nothing explains a radar concept in this file.

		/// <summary>The selected site's newest scan time — the ONE source both the field and the tile read.</summary>
		private DateTimeOffset? DetailScanTime =>
			IsLoadedSite(_selectedSite) ? _radar.NewestLoadedFrameTime : _fetchedScan?.ScanTime;

		private TimeSpan? DetailAge =>
			DetailScanTime is { } scan ? DateTimeOffset.Now - scan.ToLocalTime() : null;

		/// <summary>Age of the newest scan in minutes (drives the tile's colour). Null with no scan time.</summary>
		public double? DetailAgeMinutes => DetailAge?.TotalMinutes;

		/// <summary>Compact age for the tile — "4 min", "2 hr", "&lt;1 min". The field below shows the clock time.</summary>
		public string DetailAgeValue
		{
			get
			{
				if (DetailAge is not { } age) return "—";
				if (age.TotalMinutes < 1) return "<1 min";
				if (age.TotalMinutes < 60) return $"{age.TotalMinutes:0} min";
				if (age.TotalHours < 24) return $"{age.TotalHours:0} hr";
				return $"{age.TotalDays:0} d";
			}
		}

		// "VCP 35 · clear-air" splits into the tile's value and its label. Parsing our own formatted line is
		// contained on purpose: the mode is only ever handed around as that one string (Level2Format builds it),
		// so a second, structured path would mean a second source of truth for the same fact.
		private string[] ModeParts => VcpModeText.Split(" · ", StringSplitOptions.RemoveEmptyEntries);

		/// <summary>Tile value — "VCP 35" (or "—" before anything is known).</summary>
		public string DetailScanValue => ModeParts.Length > 0 ? ModeParts[0] : "—";

		/// <summary>Tile label — the regime word ("clear-air" / "precip"), empty when the line has none.</summary>
		public string DetailScanLabel => ModeParts.Length > 1 ? ModeParts[1] : "Scan pattern";

		/// <summary>Miles to the selected site, or null with no location marker. Same measurement the
		/// "Nearest" sort uses — one distance, so the tile and the order can't disagree.</summary>
		private double? DetailMiles => _selectedSite is null ? null : MilesTo(_selectedSite);

		/// <summary>Tile value — "142 mi", empty without a location marker (the tile collapses).</summary>
		public string DetailDistanceValue => DetailMiles is { } mi ? $"{mi:0} mi" : string.Empty;

		public RadarGlossaryCard AgeHint => RadarGlossary.DataAge(DetailAge);
		public RadarGlossaryCard ScanHint => RadarGlossary.ScanPattern(VcpModeText);
		public RadarGlossaryCard DistanceHint => RadarGlossary.Distance(DetailMiles);
		public RadarGlossaryCard CoordsHint => RadarGlossary.Coordinates();
		public RadarGlossaryCard StatusHint =>
			RadarGlossary.Status(_selectedSite?.Availability ?? SiteAvailability.Unknown, _selectedSite?.IsReplayDay ?? false);
		public RadarGlossaryCard NetworkHint =>
			RadarGlossary.Network(_selectedSite?.Site.Class ?? RadarSiteClass.Operational);

		// Re-raise the scan read-out (the fields, the tiles they feed, and the hints that read them back).
		private void RaiseScan()
		{
			OnPropertyChanged(nameof(LatestScanText));
			OnPropertyChanged(nameof(VcpModeText));
			OnPropertyChanged(nameof(DetailAgeValue));
			OnPropertyChanged(nameof(DetailAgeMinutes));
			OnPropertyChanged(nameof(DetailScanValue));
			OnPropertyChanged(nameof(DetailScanLabel));
			OnPropertyChanged(nameof(AgeHint));
			OnPropertyChanged(nameof(ScanHint));
			OnPropertyChanged(nameof(StatusHint));
			// A frame landing / the selection moving also moves the running site-hours clock and "Now".
			RaiseUsage();
		}

		private async Task LoadDetailAsync(RadarSiteRow? row, int token)
		{
			_fetchedScan = null;
			_scanStatus = string.Empty;
			RaiseScan();
			if (row is null)
			{
				IsLoadingScanInfo = false;
				return;
			}

			// The loop already knows this site's exact scan time + mode — read them off the VM (see the
			// scan-info fields) rather than paying for a second, staler lookup. No fetch, no spinner.
			if (IsLoadedSite(row))
			{
				IsLoadingScanInfo = false;
				RaiseScan();
				return;
			}

			IsLoadingScanInfo = true;
			try
			{
				var scan = await _radarService.GetLatestScanAsync(row.Site);
				if (token != _detailToken) return; // selection changed while we were fetching

				if (scan is null)
				{
					_scanStatus = "No recent data";
				}
				else
				{
					_fetchedScan = scan;
					// The newest scan (archive, or chunks when fresher) is authoritative evidence for this site's
					// status — graded by the same rule as the pass, so the pill can't contradict this readout.
					_radar.ReportSiteScan(row.Site, scan.ScanTime, canMarkOffline: true);
				}
				RaiseScan();
			}
			catch
			{
				if (token == _detailToken)
				{
					_scanStatus = "Unavailable";
					RaiseScan();
				}
			}
			finally
			{
				if (token == _detailToken)
				{
					IsLoadingScanInfo = false;
				}
			}
		}

		// ── Load on map ──────────────────────────────────────────────────────────────────────────
		/// <summary>Loads the selected site's radar loop on the map (same pipeline a marker click uses),
		/// flies to it, and closes the Atlas. No-op with nothing selected.</summary>
		public void LoadOnMap()
		{
			if (!CanLoadOnMap || _selectedSite is null) return;

			_favorites.LoadOnMap(_selectedSite);
		}

		// Re-raises all detail-pane bindings after a selection change.
		private void RaiseDetail()
		{
			OnPropertyChanged(nameof(HasSelection));
			OnPropertyChanged(nameof(CanLoadOnMap));
			OnPropertyChanged(nameof(LoadButtonText));
			OnPropertyChanged(nameof(DetailId));
			OnPropertyChanged(nameof(DetailName));
			OnPropertyChanged(nameof(DetailClassLabel));
			OnPropertyChanged(nameof(DetailCoords));
			OnPropertyChanged(nameof(DetailDistanceText));
			OnPropertyChanged(nameof(DetailDistanceValue));
			OnPropertyChanged(nameof(DistanceHint));
			OnPropertyChanged(nameof(CoordsHint));
			OnPropertyChanged(nameof(StatusHint));
			OnPropertyChanged(nameof(NetworkHint));
			OnPropertyChanged(nameof(NwsDetail));
			RaiseUsage();
		}

		// ── NWS status (RadarNwsStatusViewModel read-back) ───────────────────────────────────────
		// ⚠️ Information, not availability: nothing here touches the Online/Offline dot. The words are built
		// in RadarNwsStatusViewModel.DetailFor; the Re-check button binds NwsStatus directly.

		/// <summary>The NWS check itself — the section header's Re-check button and "checked 2 min ago".</summary>
		public RadarNwsStatusViewModel NwsStatus => _nws;

		/// <summary>The selected site's NWS STATUS section, every word chosen.</summary>
		public RadarNwsSiteDetail NwsDetail => _nws.DetailFor(_selectedSite);

		private bool _isNwsEarlierExpanded;

		/// <summary>Whether the older FTMs under the latest one are unfolded. Resets per selected site.</summary>
		public bool IsNwsEarlierExpanded
		{
			get => _isNwsEarlierExpanded;
			private set => SetProperty(ref _isNwsEarlierExpanded, value);
		}

		public void ToggleNwsEarlier() => IsNwsEarlierExpanded = !_isNwsEarlierExpanded;

		// ── Your use (SiteUsageTracker read-back) ────────────────────────────────────────────────
		// The words for the tiles and the Clear confirmation live HERE, not in XAML — same rule as the chips.
		// ⚠️ The "?" on Site load time is RadarGlossary.SiteLoadTime; the clock rule it states is SiteUsageTracker's.
		// ⚠️ Both split tiles use the app's MODE names (NowCast / PastCast) the same way — keep them alike.

		private SiteUsage? SelectedUsage => _selectedSite is null ? null : _usage.Get(_selectedSite.Id);
		private bool IsSelectedCurrent => _selectedSite is not null && _usage.IsCurrent(_selectedSite.Id);
		private double SelectedSeconds => _selectedSite is null ? 0 : _usage.SecondsLoaded(_selectedSite.Id);

		/// <summary>Whether the selected site has ANY usage (the tiles show) — else the one-line empty state.
		/// The current site counts even straight after a clear, since its clock is already running again.</summary>
		public bool HasUsage => SelectedUsage is not null || IsSelectedCurrent;

		/// <summary>Whether the header's Clear shows — only when there's recorded history to clear.</summary>
		public bool CanClearUsage => SelectedUsage is not null;

		/// <summary>The one-line empty state for a never-used site.</summary>
		public string UsageEmptyText => _selectedSite is null ? string.Empty : $"You haven't loaded {_selectedSite.Id} yet.";

		/// <summary>"since Mar 4, 2026" beside the header, empty with no history.</summary>
		public string UsageSinceText => SelectedUsage?.FirstUsedUtc is { } first
			? $"since {first.ToLocalTime():MMM d, yyyy}"
			: string.Empty;

		public string UsageLoadsValue => (SelectedUsage?.TotalLoads ?? 0).ToString(CultureInfo.CurrentCulture);

		/// <summary>"LOADS · 22 NOWCAST, 5 PASTCAST" — collapses to "ALL NOWCAST"/"ALL PASTCAST" when one side is
		/// empty.</summary>
		public string UsageLoadsLabel
		{
			get
			{
				var u = SelectedUsage;
				return u is null || u.TotalLoads == 0
					? "LOADS"
					: SplitLabel("LOADS", u.LiveLoads > 0, u.ReplayLoads > 0,
						u.LiveLoads.ToString(CultureInfo.CurrentCulture), u.ReplayLoads.ToString(CultureInfo.CurrentCulture));
			}
		}

		private double SelectedSecondsIn(bool replay) => _selectedSite is null ? 0 : _usage.SecondsLoaded(_selectedSite.Id, replay);

		/// <summary>The Site load time tile — "6.4 hr" once there's an hour, minutes before that ("18 min",
		/// "&lt;1 min"), so a new site doesn't read "0.0 hr".</summary>
		public string LoadTimeValue => ShortTime(SelectedSeconds);

		/// <summary>"SITE LOAD TIME · 5.2 HR NOWCAST, 1.2 HR PASTCAST" — the loads label's rule, in time. A side
		/// under a minute counts as empty, so a stray few seconds don't split the label.</summary>
		public string LoadTimeLabel
		{
			get
			{
				double now = SelectedSecondsIn(replay: false), past = SelectedSecondsIn(replay: true);
				return now + past < 60
					? "SITE LOAD TIME"
					: SplitLabel("SITE LOAD TIME", now >= 60, past >= 60,
						ShortTime(now).ToUpperInvariant(), ShortTime(past).ToUpperInvariant());
			}
		}

		// "<NAME> · ALL NOWCAST" / "· ALL PASTCAST" / "· <a> NOWCAST, <b> PASTCAST".
		private static string SplitLabel(string name, bool hasNow, bool hasPast, string nowText, string pastText) =>
			!hasPast ? $"{name} · ALL NOWCAST"
			: !hasNow ? $"{name} · ALL PASTCAST"
			: $"{name} · {nowText} NOWCAST, {pastText} PASTCAST";

		// "6.4 hr" / "18 min" / "<1 min".
		private static string ShortTime(double seconds)
		{
			if (seconds < 60) return "<1 min";
			if (seconds < 3600) return $"{seconds / 60:0} min";
			return $"{(seconds / 3600).ToString("0.0", CultureInfo.CurrentCulture)} hr";
		}

		/// <summary>"Now" while the site is loaded, else a compact age ("3 hr ago", "2 days ago", "Mar 4").</summary>
		public string LastUsedValue
		{
			get
			{
				if (IsSelectedCurrent) return "Now";
				if (SelectedUsage?.LastUsedUtc is not { } last) return "—";
				var age = DateTimeOffset.UtcNow - last;
				if (age.TotalMinutes < 1) return "Just now";
				if (age.TotalMinutes < 60) return $"{age.TotalMinutes:0} min ago";
				if (age.TotalHours < 24) return $"{age.TotalHours:0} hr ago";
				if (age.TotalDays < 2) return "Yesterday";
				if (age.TotalDays < 60) return $"{age.TotalDays:0} days ago";
				return last.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture);
			}
		}

		private int? SelectedRank => _selectedSite is null ? null : _usage.Rank(_selectedSite.Id);

		public string RankValue => SelectedRank is { } r ? $"#{r}" : "—";

		public string RankLabel => _usage.UsedSiteCount switch
		{
			0 => "OF SITES YOU USE",
			1 => "YOUR ONLY SITE SO FAR",
			var n => $"OF {n} SITES YOU USE",
		};

		public RadarGlossaryCard LoadTimeHint =>
			RadarGlossary.SiteLoadTime(_selectedSite?.Id ?? "This site",
				SelectedSecondsIn(replay: false), SelectedSecondsIn(replay: true), SelectedUsage?.TotalLoads ?? 0);

		// ── Clear (the confirmation's words + the act) ──

		public string ClearUsageTitle => $"Clear your use of {DetailId}?";

		public string ClearUsageBody
		{
			get
			{
				var u = SelectedUsage;
				var loads = u?.TotalLoads ?? 0;
				var since = u?.FirstUsedUtc is { } f ? $", recorded since {f.ToLocalTime():MMM d, yyyy}" : string.Empty;
				return $"This removes {HistorySpan(u?.FirstUsedUtc)} for this site: {loads} load{(loads == 1 ? "" : "s")} " +
					$"and {HoursWords(SelectedSeconds)}{since}. It can't be undone.";
			}
		}

		public string ClearAllUsageLabel
		{
			get
			{
				var (sites, seconds, since) = _usage.Totals();
				return $"Clear all {sites} site{(sites == 1 ? "" : "s")} instead ({HistorySpan(since, bare: true)}, {HoursWords(seconds)})";
			}
		}

		/// <summary>Forget the selected site's usage, or every site's.</summary>
		public void ClearUsage(bool allSites)
		{
			if (allSites) _usage.ClearAll();
			else if (_selectedSite is not null) _usage.Clear(_selectedSite.Id);
		}

		private void RaiseUsage()
		{
			OnPropertyChanged(nameof(HasUsage));
			OnPropertyChanged(nameof(CanClearUsage));
			OnPropertyChanged(nameof(UsageEmptyText));
			OnPropertyChanged(nameof(UsageSinceText));
			OnPropertyChanged(nameof(UsageLoadsValue));
			OnPropertyChanged(nameof(UsageLoadsLabel));
			OnPropertyChanged(nameof(LoadTimeValue));
			OnPropertyChanged(nameof(LoadTimeLabel));
			OnPropertyChanged(nameof(LastUsedValue));
			OnPropertyChanged(nameof(RankValue));
			OnPropertyChanged(nameof(RankLabel));
			OnPropertyChanged(nameof(LoadTimeHint));
		}

		// "6 months of history" / "today's history"; bare = "6 months" (the checkbox's parenthesis).
		private static string HistorySpan(DateTimeOffset? since, bool bare = false)
		{
			var days = since is { } s ? (DateTimeOffset.UtcNow - s).TotalDays : 0;
			if (days < 1) return bare ? "today" : "today's history";
			string span;
			if (days < 14) span = $"{days:0} day{(Math.Round(days) == 1 ? "" : "s")}";
			else if (days < 60) span = $"{days / 7:0} weeks";
			else if (days < 730) span = $"{days / 30.44:0} months";
			else span = $"{days / 365.25:0} years";
			return bare ? span : $"{span} of history";
		}

		// "6.4 hours of load time" / "18 minutes of load time" — the same one-hour switch the tile makes.
		private static string HoursWords(double seconds) => seconds < 3600
			? $"{seconds / 60:0} minute{(Math.Round(seconds / 60) == 1 ? "" : "s")} of load time"
			: $"{seconds / 3600:0.0} hours of load time";

		// Two largest non-zero units of an age span (yr/mo/day/hr/min) with an "ago" suffix,
		// e.g. "2 hr 5 min ago"; "just now" under a minute.
		private static string FormatAge(TimeSpan span)
		{
			if (span.TotalMinutes < 1) return "just now";
			double totalDays = span.TotalDays;
			int years = (int)(totalDays / 365.25);
			double remDays = totalDays - years * 365.25;
			int months = (int)(remDays / 30.44);
			int days = (int)(remDays - months * 30.44);
			var parts = new (int Value, string Unit)[]
			{
				(years, "yr"), (months, "mo"), (days, "day"), (span.Hours, "hr"), (span.Minutes, "min"),
			};
			var shown = parts.SkipWhile(p => p.Value == 0).Take(2).Where(p => p.Value > 0).ToList();
			return shown.Count == 0 ? "just now" : string.Join(" ", shown.Select(p => $"{p.Value} {p.Unit}")) + " ago";
		}

		private static double HaversineMiles(double lat1, double lon1, double lat2, double lon2)
		{
			const double R = 3958.7613; // Earth radius in miles
			double dLat = DegToRad(lat2 - lat1);
			double dLon = DegToRad(lon2 - lon1);
			double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
				Math.Cos(DegToRad(lat1)) * Math.Cos(DegToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
			return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
		}

		private static double DegToRad(double deg) => deg * Math.PI / 180.0;
	}
}
