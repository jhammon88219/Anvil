using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// View model for the Radar Site Explorer — a searchable/filterable master–detail browser over the
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
	public sealed class RadarSiteExplorerViewModel : ObservableObject
	{
		private readonly RadarViewModel _radar;
		private readonly MarkersViewModel _markers;
		private readonly ILevel2RadarService _radarService;
		private readonly RadarSiteFavoritesViewModel _favorites;

		public RadarSiteExplorerViewModel(RadarViewModel radar, MarkersViewModel markers,
			ILevel2RadarService radarService, RadarSiteFavoritesViewModel favorites)
		{
			_radar = radar;
			_markers = markers;
			_radarService = radarService;
			_favorites = favorites;

			FilteredSites = new ObservableCollection<RadarSiteRow>();
			SiteGroups = new ObservableCollection<RadarSiteGroup>();
			RebuildFiltered();

			// Starring a site or moving home re-sections the list (Home / Favorites / All sites).
			_favorites.PinnedChanged += (_, _) => RebuildFiltered();

			// For the site the loop is showing, our scan read-out IS the loop's — so re-raise it whenever the
			// loop's frame time / mode / selection changes, and the two stay in lock-step (a new live frame
			// updates both at once) instead of the explorer freezing at whatever it fetched on selection.
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

				// Settings → Radar decides which networks exist in the list, same as on the map.
				if (e.PropertyName is nameof(RadarViewModel.ShowTdwrs) or nameof(RadarViewModel.ShowResearchRadars))
				{
					OnPropertyChanged(nameof(IsTdwrFilterEnabled));
					OnPropertyChanged(nameof(IsResearchFilterEnabled));
					if ((_selectedClassIndex == 2 && !_radar.ShowResearchRadars) || (_selectedClassIndex == 3 && !_radar.ShowTdwrs))
					{
						SelectedClassIndex = 0; // the filter pointed at a network that just vanished (rebuilds)
					}
					else
					{
						RebuildFiltered();
					}
				}
			};

			FollowRadarSelection();
		}

		/// <summary>
		/// Mirrors the map's radar pick into the list: whichever way a site gets selected (marker click, this
		/// explorer, a saved event) the explorer shows THAT site, so opening it lands on the loaded radar.
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

		private int _selectedClassIndex; // 0 = All, 1 = Operational, 2 = Research, 3 = TDWR

		/// <summary>Network filter (bound to a ComboBox SelectedIndex): All / Operational / Research / TDWR.</summary>
		public int SelectedClassIndex
		{
			get => _selectedClassIndex;
			set
			{
				if (SetProperty(ref _selectedClassIndex, value))
				{
					RebuildFiltered();
				}
			}
		}

		private bool _onlineOnly;

		/// <summary>When set, hides sites with no recent data in the feed (offline markers).</summary>
		public bool OnlineOnly
		{
			get => _onlineOnly;
			set
			{
				if (SetProperty(ref _onlineOnly, value))
				{
					RebuildFiltered();
				}
			}
		}

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

		/// <summary>Header count, e.g. "42 of 203 sites".</summary>
		public string ResultCountText =>
			$"{FilteredSites.Count} of {_radar.RadarSiteRows.Count(r => _radar.IsNetworkShown(r.Site))} sites";

		/// <summary>The network filter's TDWR / Research entries grey out while Settings → Radar hides that network.</summary>
		public bool IsTdwrFilterEnabled => _radar.ShowTdwrs;
		public bool IsResearchFilterEnabled => _radar.ShowResearchRadars;

		private void RebuildFiltered()
		{
			var search = _searchText.Trim();
			IEnumerable<RadarSiteRow> q = _radar.RadarSiteRows.Where(r => _radar.IsNetworkShown(r.Site));

			q = _selectedClassIndex switch
			{
				1 => q.Where(r => r.Site.Class == RadarSiteClass.Operational),
				2 => q.Where(r => r.Site.Class == RadarSiteClass.Research),
				3 => q.Where(r => r.Site.Class == RadarSiteClass.Tdwr),
				_ => q,
			};

			if (_onlineOnly)
			{
				q = q.Where(r => !r.IsOffline);
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
			var rest = _radar.RadarSiteRows.Where(r => matched.Contains(r) && !r.IsHome && !r.IsFavorite).ToList();

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
			OnPropertyChanged(nameof(ResultCountText));
			// The Clear() dropped the ListView's selection (its null echo was ignored — see SelectedSite);
			// re-raise so a selection that survived the filter is highlighted again.
			OnPropertyChanged(nameof(SelectedSite));
		}

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
		public string DetailStatusText => _selectedSite is null ? string.Empty : _selectedSite.StatusLabel;

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
		//     sweep time + mode, so the explorer and the Selected Site readout agree to the second and
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

		/// <summary>VCP / scan-mode line for the selected site (from the loop when it's the loaded site).</summary>
		public string VcpModeText
		{
			get
			{
				if (IsLoadedSite(_selectedSite))
				{
					var mode = _radar.RadarModeText;
					return string.IsNullOrEmpty(mode) ? "—" : mode;
				}
				return string.IsNullOrEmpty(_fetchedScan?.ModeText) ? string.Empty : _fetchedScan!.ModeText!;
			}
		}

		// Re-raise the scan read-out (both derived properties).
		private void RaiseScan()
		{
			OnPropertyChanged(nameof(LatestScanText));
			OnPropertyChanged(nameof(VcpModeText));
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
		/// flies to it, and closes the explorer. No-op with nothing selected.</summary>
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
			OnPropertyChanged(nameof(DetailStatusText));
			OnPropertyChanged(nameof(DetailDistanceText));
		}

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
