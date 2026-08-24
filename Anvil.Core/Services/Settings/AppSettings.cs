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
	}
}
