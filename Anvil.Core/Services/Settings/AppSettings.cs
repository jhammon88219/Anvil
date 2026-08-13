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
