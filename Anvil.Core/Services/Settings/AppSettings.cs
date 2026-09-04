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
		/// that wrote it: a Debug session can quit on the dev tab (index 3) and a Release build then has no
		/// such tab. <see cref="ViewModels.MapViewModel.SettingsTabIndex"/> clamps on load — never trust this
		/// value against a tab count without clamping first.
		/// </summary>
		public int SettingsTabIndex
		{
			get => _settingsTabIndex;
			set => SetProperty(ref _settingsTabIndex, value);
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
	}
}
