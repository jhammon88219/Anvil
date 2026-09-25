using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.Services
{
	/// <summary>
	/// Every user-configurable, persisted app setting, as ONE observable POCO. This is the entire surface
	/// you extend to add a setting: declare a typed property with its default inline (via
	/// <c>SetProperty</c>) and <see cref="SettingsService"/> serializes + loads it automatically — auto-save
	/// is wired to <see cref="ObservableObject.PropertyChanged"/>, so there are no keys, no per-setting
	/// plumbing, and no explicit save calls. Keep every value JSON-serializable (primitives, strings, enums,
	/// simple records/collections).
	/// </summary>
	public sealed class AppSettings : ObservableObject
	{
		private string _mapDataFolder = "";
		/// <summary>
		/// User-chosen folder holding the offline basemap PMTiles file. Empty = use the runtime-resolved
		/// default; read the EFFECTIVE folder off <see cref="ISettingsService.MapDataFolder"/> (which applies
		/// that fallback), and write here to change it.
		/// </summary>
		public string MapDataFolder
		{
			get => _mapDataFolder;
			set => SetProperty(ref _mapDataFolder, value);
		}

		/// <summary>
		/// The online basemap source offered when <see cref="UseOnlineTiles"/> is on, and the default the
		/// Map Controls window prefills. Protomaps' hosted API serves the SAME schema the bundled file does,
		/// so the app's styles render identically against it — but it needs an API key appended (free for
		/// non-commercial use, soft cap 1M tile requests/month). A self-hosted PMTiles archive
		/// (<c>pmtiles://https://…</c>) or any other Protomaps-schema TileJSON / <c>{z}/{x}/{y}</c> template
		/// works here too; the page tells the three forms apart (map.js <c>tileSourceFor</c>).
		/// </summary>
		public const string DefaultOnlineTilesUrl = "https://api.protomaps.com/tiles/v4.json?key=";

		private bool _useOnlineTiles;
		/// <summary>
		/// Stream the basemap's vector tiles from <see cref="OnlineTilesUrl"/> instead of the bundled
		/// offline PMTiles file. Default OFF — offline is the point of the app, and this only changes where
		/// the tiles come from, never how they are styled.
		/// </summary>
		public bool UseOnlineTiles
		{
			get => _useOnlineTiles;
			set => SetProperty(ref _useOnlineTiles, value);
		}

		private string _onlineTilesUrl = DefaultOnlineTilesUrl;
		/// <summary>
		/// Where online tiles come from. Kept as ONE free-form string (key included) rather than a vendor
		/// enum plus a key field, so pointing Anvil at a self-hosted bucket later is a settings change
		/// rather than a code change.
		/// </summary>
		public string OnlineTilesUrl
		{
			get => _onlineTilesUrl;
			set => SetProperty(ref _onlineTilesUrl, value);
		}

		private bool _showTdwrs;
		/// <summary>Show the FAA Terminal Doppler Weather Radar (<c>T***</c>) markers. Opt-in, default off.
		/// Surfaced as the "Show TDWRs" toggle (App Settings → Radar Settings); persisted here.</summary>
		public bool ShowTdwrs
		{
			get => _showTdwrs;
			set => SetProperty(ref _showTdwrs, value);
		}

		private bool _showResearchRadars;
		/// <summary>Show the research/test radar markers (e.g. the ROC test bed KCRI). Opt-in, default off.
		/// Surfaced as the "Show Research Radars" toggle (App Settings → Radar Settings); persisted here.</summary>
		public bool ShowResearchRadars
		{
			get => _showResearchRadars;
			set => SetProperty(ref _showResearchRadars, value);
		}

		private int _radarCacheMaxGb = 5;
		/// <summary>
		/// Ceiling (GB) for the on-disk NEXRAD volume cache, enforced by <see cref="SettingsService"/>'s
		/// consumer <c>Level2RadarService</c> at startup (oldest-first). User-adjustable in the App Settings
		/// card's Storage section — the settings service's first real consumer.
		/// </summary>
		public int RadarCacheMaxGb
		{
			get => _radarCacheMaxGb;
			set => SetProperty(ref _radarCacheMaxGb, value);
		}

		private int _settingsTabIndex;
		/// <summary>
		/// Which tab the Settings window reopens on. ⚠️ Persisted as a raw INDEX, so it can outlive the tab
		/// that wrote it: a Debug session can quit on the dev tab (index 4) and a Release build then has no
		/// such tab. <see cref="ViewModels.MapViewModel.SettingsTabIndex"/> clamps on load — never trust this
		/// value against a tab count without clamping first.
		/// </summary>
		public int SettingsTabIndex
		{
			get => _settingsTabIndex;
			set => SetProperty(ref _settingsTabIndex, value);
		}

		private int _atlasTabIndex;
		/// <summary>Which tab the Anvil Atlas reopens on: 0 = Radar sites, 1 = Past events. A raw index,
		/// clamped by <see cref="ViewModels.MapViewModel.AtlasTabIndex"/>.</summary>
		public int AtlasTabIndex
		{
			get => _atlasTabIndex;
			set => SetProperty(ref _atlasTabIndex, value);
		}

		private string _settingsTabPlacement = "Top";
		/// <summary>
		/// Where the Settings window draws its tab strip: <c>"Top"</c> (a rail across the top, the default) or
		/// <c>"Left"</c> (a side rail). Stored as a STRING rather than the enum so an unrecognized value from a
		/// hand-edited or future file parses back to the default instead of failing the whole settings load.
		/// </summary>
		public string SettingsTabPlacement
		{
			get => _settingsTabPlacement;
			set => SetProperty(ref _settingsTabPlacement, value);
		}

		private string _monitorMode = "Single";
		/// <summary>
		/// The monitor mode (<c>"Single"</c> or <c>"Multi"</c>), from the Settings window's Window Mode tab. A STRING
		/// for the same reason as <see cref="SettingsTabPlacement"/>. ⚠️ Multi is not built:
		/// <see cref="ViewModels.MapViewModel.MonitorMode"/> resolves any value to Single until it is.
		/// </summary>
		public string MonitorMode
		{
			get => _monitorMode;
			set => SetProperty(ref _monitorMode, value);
		}

		// ── PastCast timeframe (the replay window's pickers) ─────────────────────────────────────────
		// The last timeframe the user chose, so reopening PastCast offers the event they were last
		// watching instead of the built-in default. These three ARE the defaults now — RadarViewModel
		// restores from them on construction and writes them back on every picker change.
		// ⚠️ Stored as VALUES, not as the picker INDICES the view model works in: the year index is
		// 1991-based and the duration index points into a fixed option list, so a persisted index would
		// silently change meaning if either list ever moved. A calendar day + a local time-of-day + a
		// window length can't.

		private string _pastCastDate = "2011-05-24";
		/// <summary>
		/// The replay window's calendar day as <c>yyyy-MM-dd</c> (a LOCAL calendar date, not an instant —
		/// see <c>RadarViewModel.LocalMidnight</c>). Default 2011-05-24, a frequently-revisited event.
		/// ⚠️ A STRING for the same reason <see cref="SettingsTabPlacement"/> is one: an unparseable or
		/// hand-edited value falls back to the default instead of failing the whole settings load.
		/// </summary>
		public string PastCastDate
		{
			get => _pastCastDate;
			set => SetProperty(ref _pastCastDate, value);
		}

		private int _pastCastStartMinutes = 17 * 60;
		/// <summary>Start time-of-day of the replay window, as minutes past LOCAL midnight (default
		/// 5:00 PM). Minutes rather than a <c>TimeSpan</c>/<c>DateTime</c> so nothing about a time ZONE or
		/// a date is smuggled into the value — it is exactly what the TimePicker holds.</summary>
		public int PastCastStartMinutes
		{
			get => _pastCastStartMinutes;
			set => SetProperty(ref _pastCastStartMinutes, value);
		}

		private int _pastCastDurationMinutes = 120;
		/// <summary>How long a window the replay loads, in minutes (default 2 hours). ⚠️ The LENGTH, not
		/// the segmented picker's index: the view model maps it back through its own minutes table, so a
		/// value that table no longer offers falls back to the default rather than selecting a different
		/// duration.</summary>
		public int PastCastDurationMinutes
		{
			get => _pastCastDurationMinutes;
			set => SetProperty(ref _pastCastDurationMinutes, value);
		}

		private string _themeId = "";
		/// <summary>
		/// The app's chosen visual identity, as an <see cref="IThemeProvider"/> theme id — which owns BOTH
		/// the WinUI chrome palette and the basemap style under it. Empty = use the provider's default;
		/// resolve through <c>IThemeProvider.Resolve</c>, which also absorbs an id this build doesn't have.
		/// </summary>
		/// <remarks>
		/// ⚠️ A STRING, and empty-means-default, for the same reasons <see cref="SettingsTabPlacement"/> and
		/// <see cref="MapDataFolder"/> are: an unrecognized value falls back instead of failing the whole
		/// settings load, and the default itself is named in exactly one place (the provider) rather than
		/// copied here where the two could drift.
		/// ⚠️ NOTHING WRITES THIS YET — the app ships one theme, so there is no picker to write it. It is
		/// here because the basemap style is now the theme's, and the style has never persisted at all: the
		/// selection reset to Data Viz Black on every launch. The writer arrives with the picker.
		/// </remarks>
		public string ThemeId
		{
			get => _themeId;
			set => SetProperty(ref _themeId, value);
		}

		private string _mapIsolation = "";
		/// <summary>
		/// What the map's isolation picker was resting on: <c>"None"</c>, <c>"Conus"</c>, or a state NAME
		/// (matching <c>StateIsolationViewModel.States</c>). Empty or unrecognized = No Isolation, which is
		/// also the first-run default — the app opens on the whole map and masking is something you ask for.
		/// </summary>
		/// <remarks>
		/// ⚠️ ONE string holding the resolved PICK, not the view model's three flags
		/// (<c>IsConusIsolated</c>/<c>IsArmed</c>/<c>SelectedState</c>). Those can't disagree this way: a pair
		/// of settings could persist "a state is isolated" with no state named. The kinds and the state names
		/// share one value space safely because no state is called "None" or "Conus".
		/// ⚠️ "Select to Isolate" (armed, nothing picked) deliberately persists as <c>"None"</c> — arming is a
		/// pending GESTURE ("now click a state"), and relaunching into hover-armed mode with no mask on screen
		/// would read as the app having lost the pick rather than as a mode you left running.
		/// ⚠️ A state NAME rather than an index, for the usual reason (see the PastCast block): the picker's
		/// list is positional and an index would silently name a different place if it ever moved.
		/// </remarks>
		public string MapIsolation
		{
			get => _mapIsolation;
			set => SetProperty(ref _mapIsolation, value);
		}

		private bool _mapControlsStripVisible = true;
		/// <summary>Whether the MapControlsStrip — the notched strip of camera tools straddling the bottom
		/// bar's pull-tab — is showing. Toggled by the bar's "Map" key. Default ON, so the tools it exists to
		/// un-hide are visible on a first run.</summary>
		/// <remarks>
		/// ⚠️ It is NOT the same switch as the bar's own pull-tab. Hiding the bar carries the strip DOWN with
		/// it and leaves it visible; hiding both is deliberately two gestures, this one first.
		/// </remarks>
		public bool MapControlsStripVisible
		{
			get => _mapControlsStripVisible;
			set => SetProperty(ref _mapControlsStripVisible, value);
		}

		// ── Basemap layers (BasemapViewModel: the tools tier's Map key + its layer flyout) ─────────────────
		// ⚠️ The Map key's HIDE is deliberately NOT here: launching into a blank map would read as broken, so it
		// is session-only. The flyout's choices and the dimmer ARE persisted.

		private List<string> _hiddenBasemapGroups = new();
		/// <summary>The basemap groups the user unticked (<c>Models.BasemapGroups</c> ids); empty = all drawn.</summary>
		/// <remarks>⚠️ The UNTICKED set, so a group added later defaults to shown. REPLACE the list, never
		/// mutate it — see FavoriteSiteIds.</remarks>
		public List<string> HiddenBasemapGroups
		{
			get => _hiddenBasemapGroups;
			set => SetProperty(ref _hiddenBasemapGroups, value ?? new());
		}

		private double _basemapDim;
		/// <summary>How far the basemap is faded toward the blank ground, 0 (none) to 0.9.</summary>
		public double BasemapDim
		{
			get => _basemapDim;
			set => SetProperty(ref _basemapDim, value);
		}

		// ── Home + favorite radar sites (RadarSiteFavoritesViewModel) ─────────────────────────────────
		// ⚠️ All stored as ICAO ids ("KTLX"), never list positions — the site list is regenerated and an index
		// would silently name a different radar.

		private string _homeSiteId = "";
		/// <summary>The home radar site's ICAO; empty = none set. Pinned at the top of the tools tier's site picker.</summary>
		public string HomeSiteId
		{
			get => _homeSiteId;
			set => SetProperty(ref _homeSiteId, value ?? "");
		}

		private List<string> _favoriteSiteIds = new();
		/// <summary>Favorite radar site ICAOs, in the order they were starred (the flyout's order).</summary>
		/// <remarks>⚠️ REPLACE the list, never mutate it: auto-save listens to PropertyChanged, which only fires
		/// when a NEW list is assigned — an Add on the existing one would never reach disk.</remarks>
		public List<string> FavoriteSiteIds
		{
			get => _favoriteSiteIds;
			set => SetProperty(ref _favoriteSiteIds, value ?? new());
		}

		// ── Overlay order (the temporal windows' draggable layer sections) ─────────────────────────────
		// ⚠️ ONE LIST PER WINDOW, by the user's call: Past and Now never run together, so each keeps its own
		// stack. Top-first Models.LayerOrder ids; EMPTY = the window's own default (its XAML order).
		// ⚠️ REPLACE the list, never mutate it — see FavoriteSiteIds.

		private List<string> _nowCastLayerOrder = new();
		/// <summary>The NowCast window's layer-section order, top first (= top of the map).</summary>
		public List<string> NowCastLayerOrder
		{
			get => _nowCastLayerOrder;
			set => SetProperty(ref _nowCastLayerOrder, value ?? new());
		}

		private List<string> _pastCastLayerOrder = new();
		/// <summary>The PastCast window's layer-section order, top first (= top of the map).</summary>
		public List<string> PastCastLayerOrder
		{
			get => _pastCastLayerOrder;
			set => SetProperty(ref _pastCastLayerOrder, value ?? new());
		}

		// ── Temporal-window choices (ViewModels/Map/TemporalWindowPersistence restores + tracks these) ──────
		// ⚠️ NULLABLE ON PURPOSE: null = "never changed", and the view model keeps ITS OWN default. The
		// defaults therefore live in exactly one place (the VM field initialisers), and a settings file from
		// before these existed loads as all-null — no behaviour change until the user touches something.
		// ⚠️ Outlook products are SpcOutlookType NAMES ("Categorical"), "None" for the off option — never a
		// list index: the product lists cascade with the day, so a position names different products.

		private double? _radarOpacity;
		public double? RadarOpacity { get => _radarOpacity; set => SetProperty(ref _radarOpacity, value); }
		private bool? _showRadarLayer;
		public bool? ShowRadarLayer { get => _showRadarLayer; set => SetProperty(ref _showRadarLayer, value); }

		private bool? _warningsShowTornado;
		public bool? WarningsShowTornado { get => _warningsShowTornado; set => SetProperty(ref _warningsShowTornado, value); }
		private bool? _warningsShowSevere;
		public bool? WarningsShowSevere { get => _warningsShowSevere; set => SetProperty(ref _warningsShowSevere, value); }
		private bool? _warningsShowFlashFlood;
		public bool? WarningsShowFlashFlood { get => _warningsShowFlashFlood; set => SetProperty(ref _warningsShowFlashFlood, value); }
		private double? _warningsOpacity;
		public double? WarningsOpacity { get => _warningsOpacity; set => SetProperty(ref _warningsOpacity, value); }

		private bool? _watchesShowTornado;
		public bool? WatchesShowTornado { get => _watchesShowTornado; set => SetProperty(ref _watchesShowTornado, value); }
		private bool? _watchesShowSevere;
		public bool? WatchesShowSevere { get => _watchesShowSevere; set => SetProperty(ref _watchesShowSevere, value); }
		private double? _watchesOpacity;
		public double? WatchesOpacity { get => _watchesOpacity; set => SetProperty(ref _watchesOpacity, value); }

		// ONE set for both windows — NowCast and PastCast share StormReportsViewModel.
		private bool? _stormReportsShowTornado;
		public bool? StormReportsShowTornado { get => _stormReportsShowTornado; set => SetProperty(ref _stormReportsShowTornado, value); }
		private bool? _stormReportsShowWind;
		public bool? StormReportsShowWind { get => _stormReportsShowWind; set => SetProperty(ref _stormReportsShowWind, value); }
		private bool? _stormReportsShowHail;
		public bool? StormReportsShowHail { get => _stormReportsShowHail; set => SetProperty(ref _stormReportsShowHail, value); }
		private double? _stormReportsOpacity;
		public double? StormReportsOpacity { get => _stormReportsOpacity; set => SetProperty(ref _stormReportsOpacity, value); }

		private bool? _damageSurveysShowAreas;
		public bool? DamageSurveysShowAreas { get => _damageSurveysShowAreas; set => SetProperty(ref _damageSurveysShowAreas, value); }
		private bool? _damageSurveysShowTracks;
		public bool? DamageSurveysShowTracks { get => _damageSurveysShowTracks; set => SetProperty(ref _damageSurveysShowTracks, value); }
		private bool? _damageSurveysShowPoints;
		public bool? DamageSurveysShowPoints { get => _damageSurveysShowPoints; set => SetProperty(ref _damageSurveysShowPoints, value); }
		private double? _damageSurveysOpacity;
		public double? DamageSurveysOpacity { get => _damageSurveysOpacity; set => SetProperty(ref _damageSurveysOpacity, value); }

		// PastCast's historical outlook. Cycle: the issuance hour (UTC); null = Auto.
		private int? _pastOutlookDay;
		public int? PastOutlookDay { get => _pastOutlookDay; set => SetProperty(ref _pastOutlookDay, value); }
		private string? _pastOutlookProduct;
		public string? PastOutlookProduct { get => _pastOutlookProduct; set => SetProperty(ref _pastOutlookProduct, value); }
		private int? _pastOutlookCycle;
		public int? PastOutlookCycle { get => _pastOutlookCycle; set => SetProperty(ref _pastOutlookCycle, value); }
		private double? _pastOutlookOpacity;
		public double? PastOutlookOpacity { get => _pastOutlookOpacity; set => SetProperty(ref _pastOutlookOpacity, value); }

		// ForeCast's live outlook.
		private int? _foreCastOutlookDay;
		public int? ForeCastOutlookDay { get => _foreCastOutlookDay; set => SetProperty(ref _foreCastOutlookDay, value); }
		private string? _foreCastOutlookProduct;
		public string? ForeCastOutlookProduct { get => _foreCastOutlookProduct; set => SetProperty(ref _foreCastOutlookProduct, value); }
		private double? _foreCastOutlookOpacity;
		public double? ForeCastOutlookOpacity { get => _foreCastOutlookOpacity; set => SetProperty(ref _foreCastOutlookOpacity, value); }
		private bool? _foreCastShowHatching;
		public bool? ForeCastShowHatching { get => _foreCastShowHatching; set => SetProperty(ref _foreCastShowHatching, value); }

		// ── Temporal SESSION: which modes were on, and each mode's window (MapViewModel "Temporal session") ──
		// ⚠️ Restored at MAP-READY (a mode drives the map), before the home-site launch. Modes default OFF
		// (the app has always launched clean), so these are plain bools. A window flag is only honoured while
		// its mode is back on — a window can't outlive its mode.
		private bool _pastCastOn;
		public bool PastCastOn { get => _pastCastOn; set => SetProperty(ref _pastCastOn, value); }
		private bool _nowCastOn;
		public bool NowCastOn { get => _nowCastOn; set => SetProperty(ref _nowCastOn, value); }
		private bool _foreCastOn;
		public bool ForeCastOn { get => _foreCastOn; set => SetProperty(ref _foreCastOn, value); }

		private bool _pastWindowOpen;
		public bool PastWindowOpen { get => _pastWindowOpen; set => SetProperty(ref _pastWindowOpen, value); }
		private bool _nowWindowOpen;
		public bool NowWindowOpen { get => _nowWindowOpen; set => SetProperty(ref _nowWindowOpen, value); }
		private bool _foreWindowOpen;
		public bool ForeWindowOpen { get => _foreWindowOpen; set => SetProperty(ref _foreWindowOpen, value); }

		// Title-bar pin + lock per temporal window. Nullable: null = the VM's house default (both ON).
		private bool? _pastWindowOnTop;
		public bool? PastWindowOnTop { get => _pastWindowOnTop; set => SetProperty(ref _pastWindowOnTop, value); }
		private bool? _nowWindowOnTop;
		public bool? NowWindowOnTop { get => _nowWindowOnTop; set => SetProperty(ref _nowWindowOnTop, value); }
		private bool? _foreWindowOnTop;
		public bool? ForeWindowOnTop { get => _foreWindowOnTop; set => SetProperty(ref _foreWindowOnTop, value); }
		private bool? _pastWindowLocked;
		public bool? PastWindowLocked { get => _pastWindowLocked; set => SetProperty(ref _pastWindowLocked, value); }
		private bool? _nowWindowLocked;
		public bool? NowWindowLocked { get => _nowWindowLocked; set => SetProperty(ref _nowWindowLocked, value); }
		private bool? _foreWindowLocked;
		public bool? ForeWindowLocked { get => _foreWindowLocked; set => SetProperty(ref _foreWindowLocked, value); }

		private Dictionary<string, bool> _sectionExpanded = new();
		/// <summary>Which temporal-window sections the user opened or closed, keyed "window/section"
		/// (e.g. "now/warnings"). A key that is absent = the section's XAML default.</summary>
		/// <remarks>⚠️ REPLACE the dictionary, never mutate it — see FavoriteSiteIds.</remarks>
		public Dictionary<string, bool> SectionExpanded
		{
			get => _sectionExpanded;
			set => SetProperty(ref _sectionExpanded, value ?? new());
		}

		private bool _loadHomeOnLaunch;
		/// <summary>Start the home site's live loop when the app opens (Settings → Radar). Default OFF — the
		/// app has always launched with no radar, and that stays the first-run behaviour.</summary>
		public bool LoadHomeOnLaunch
		{
			get => _loadHomeOnLaunch;
			set => SetProperty(ref _loadHomeOnLaunch, value);
		}

		private string _distanceUnits = Models.DistanceUnits.Kilometers;
		/// <summary>
		/// The unit every GROUND-DISTANCE readout is shown in — the range ruler's chip and the Inspector's
		/// range — as a <see cref="Models.DistanceUnits"/> token (<c>"km"</c> / <c>"mi"</c> / <c>"nm"</c>).
		/// Set in Settings -> Radar -> Readouts.
		/// </summary>
		/// <remarks>
		/// ⚠️ A TOKEN STRING, not an enum, for the same reason as <see cref="SettingsTabPlacement"/>: an
		/// unrecognized value falls back to kilometres (<c>DistanceUnits.Normalize</c>) instead of failing the
		/// whole settings load. It is also pushed into the map page verbatim, so the two sides format alike.
		/// ⚠️ It does NOT reach heights or wind-profile layer bounds — see the remarks on
		/// <see cref="Models.DistanceUnits"/>.
		/// </remarks>
		public string DistanceUnits
		{
			get => _distanceUnits;
			set => SetProperty(ref _distanceUnits, Models.DistanceUnits.Normalize(value));
		}

		private string _scopeColor = Models.ScopeColors.ThemeDefault;
		/// <summary>
		/// The colour of the range ring AND the range ruler (one colour, so the ruler reads as part of the
		/// ring it ends on), as a <see cref="Models.ScopeColors"/> token: <c>#RRGGBB</c>, or empty for the
		/// theme's own. Set in Settings -> Radar -> Range ring &amp; ruler.
		/// </summary>
		/// <remarks>⚠️ Written into the page as a CSS custom-property override, so it only ever holds what
		/// <c>ScopeColors.Normalize</c> lets through. It overrides BOTH themes; the sweep pulse keeps its own.</remarks>
		public string ScopeColor
		{
			get => _scopeColor;
			set => SetProperty(ref _scopeColor, Models.ScopeColors.Normalize(value));
		}

		private string _rulerAnchor = Models.RulerAnchors.Site;
		/// <summary>Where the range ruler starts: <c>"Site"</c> (default) or <c>"Location"</c> (the user-location
		/// marker). A <see cref="Models.RulerAnchors"/> token. ⚠️ "Location" with no marker placed is kept as-is —
		/// the page falls back to the site and says so, rather than this forgetting the preference.</summary>
		public string RulerAnchor
		{
			get => _rulerAnchor;
			set => SetProperty(ref _rulerAnchor, Models.RulerAnchors.Normalize(value));
		}

		// ── Range rings (Settings → Radar Range Ring) ─────────────────────────────────────────────────
		// Three independent rings around the loaded site. The page sizes the first two from the DISPLAYED
		// frame: where its reflectivity ends (the outline, default on) and where its velocity ends (default
		// on). The third is fixed-spacing distance rings (default off). ⚠️ Hiding the reflectivity ring does
		// NOT move the range ruler: it still ends at the reflectivity reach.

		private bool _showReflectivityRing = true;
		/// <summary>The outline where the displayed frame's reflectivity data ends (the ring the app always had).</summary>
		public bool ShowReflectivityRing
		{
			get => _showReflectivityRing;
			set => SetProperty(ref _showReflectivityRing, value);
		}

		private bool _showVelocityRing = true;
		/// <summary>Where the displayed frame's velocity data ends — past it, no velocity or SRV.</summary>
		public bool ShowVelocityRing
		{
			get => _showVelocityRing;
			set => SetProperty(ref _showVelocityRing, value);
		}

		private bool _showDistanceRings;
		/// <summary>Faint fixed-spacing rings for judging distance, in <see cref="DistanceUnits"/>.</summary>
		public bool ShowDistanceRings
		{
			get => _showDistanceRings;
			set => SetProperty(ref _showDistanceRings, value);
		}

		private int _distanceRingSpacing = Models.RangeRingSpacings.Auto;
		/// <summary>The distance rings' step in the distance unit — a <see cref="Models.RangeRingSpacings"/> value
		/// (0 = Auto).</summary>
		public int DistanceRingSpacing
		{
			get => _distanceRingSpacing;
			set => SetProperty(ref _distanceRingSpacing, Models.RangeRingSpacings.Normalize(value));
		}

		// ── Range ring LOOK (Settings → Radar Range Ring) ─────────────────────────────────────────────
		// Each ring's stroke is ONE record (opacity/width/pattern), replaced whole — see Models.RingStyle. Colours
		// are separate ScopeColors tokens ("" = the theme's colour for that ring); the OUTLINE's colour is
		// ScopeColor above, because the ruler wears it too. ⚠️ Every colour here reaches a MapLibre paint
		// property, so only a ScopeColors.Normalize'd #RRGGBB is stored.

		private Models.RingStyle _outlineRingStyle = Models.RingStyle.OutlineDefault;
		/// <summary>The reflectivity outline's stroke.</summary>
		public Models.RingStyle OutlineRingStyle
		{
			get => _outlineRingStyle;
			set => SetProperty(ref _outlineRingStyle, (value ?? Models.RingStyle.OutlineDefault).Normalized());
		}

		private Models.RingStyle _velocityRingStyle = Models.RingStyle.VelocityDefault;
		/// <summary>The velocity reach ring's stroke.</summary>
		public Models.RingStyle VelocityRingStyle
		{
			get => _velocityRingStyle;
			set => SetProperty(ref _velocityRingStyle, (value ?? Models.RingStyle.VelocityDefault).Normalized());
		}

		private Models.RingStyle _distanceRingStyle = Models.RingStyle.DistanceDefault;
		/// <summary>The distance rings' stroke.</summary>
		public Models.RingStyle DistanceRingStyle
		{
			get => _distanceRingStyle;
			set => SetProperty(ref _distanceRingStyle, (value ?? Models.RingStyle.DistanceDefault).Normalized());
		}

		private string _velocityRingColor = Models.ScopeColors.ThemeDefault;
		/// <summary>The velocity ring's colour; empty = theme.css <c>--anvil-scope-vel</c>.</summary>
		public string VelocityRingColor
		{
			get => _velocityRingColor;
			set => SetProperty(ref _velocityRingColor, Models.ScopeColors.Normalize(value));
		}

		private string _distanceRingColor = Models.ScopeColors.ThemeDefault;
		/// <summary>The distance rings' colour; empty = theme.css <c>--anvil-scope-dist</c>.</summary>
		public string DistanceRingColor
		{
			get => _distanceRingColor;
			set => SetProperty(ref _distanceRingColor, Models.ScopeColors.Normalize(value));
		}

		private string _distanceLabelColor = Models.ScopeColors.ThemeDefault;
		/// <summary>The distance labels' colour; empty = whatever colour the distance rings are.</summary>
		public string DistanceLabelColor
		{
			get => _distanceLabelColor;
			set => SetProperty(ref _distanceLabelColor, Models.ScopeColors.Normalize(value));
		}

		private Models.RingLabelStyle _distanceLabelStyle = Models.RingLabelStyle.Default;
		/// <summary>The distance labels' opacity, text size and halo.</summary>
		public Models.RingLabelStyle DistanceLabelStyle
		{
			get => _distanceLabelStyle;
			set => SetProperty(ref _distanceLabelStyle, (value ?? Models.RingLabelStyle.Default).Normalized());
		}

		private double _distanceLabelBearing;
		/// <summary>Where the distance labels sit around the site, degrees clockwise from north (0 = north, the
		/// old fixed spot). Written by dragging the label HANDLE on the map, or by the Settings slider.</summary>
		public double DistanceLabelBearing
		{
			get => _distanceLabelBearing;
			set => SetProperty(ref _distanceLabelBearing, Models.RingLabelBearing.Normalize(value));
		}

		private string _distanceLabelHaloColor = Models.ScopeColors.ThemeDefault;
		/// <summary>The halo behind the distance labels; empty = the theme's dark casing (the ruler's).</summary>
		public string DistanceLabelHaloColor
		{
			get => _distanceLabelHaloColor;
			set => SetProperty(ref _distanceLabelHaloColor, Models.ScopeColors.Normalize(value));
		}

		private double _ringKnobSize = Models.RingKnobSize.Default;
		/// <summary>Size (px) of the knobs that ride the outer ring: the label handle AND the ruler's knob.</summary>
		public double RingKnobSize
		{
			get => _ringKnobSize;
			set => SetProperty(ref _ringKnobSize, Models.RingKnobSize.Normalize(value));
		}

		private bool _rangeRingsVisible = true;
		/// <summary>The MASTER show/hide for every range ring, label and the handle — the tools tier's key beside
		/// the Ruler. Which rings (above) is kept underneath it.</summary>
		public bool RangeRingsVisible
		{
			get => _rangeRingsVisible;
			set => SetProperty(ref _rangeRingsVisible, value);
		}

		private bool _showDistanceLabelHandle = true;
		/// <summary>Whether the map shows the drag handle that swings the distance labels (primary pane only).</summary>
		public bool ShowDistanceLabelHandle
		{
			get => _showDistanceLabelHandle;
			set => SetProperty(ref _showDistanceLabelHandle, value);
		}
	}
}
