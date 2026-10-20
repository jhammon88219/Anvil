using System.Threading.Tasks;

namespace Anvil.Services
{
	/// <summary>
	/// App settings persisted across launches as JSON (see <see cref="SettingsService"/>). Adopts the
	/// AppBase starter-template pattern: a single observable <see cref="AppSettings"/> POCO you extend one
	/// property at a time, auto-saved on change. The remaining members are Anvil DOMAIN helpers (not raw
	/// stored state): the effective basemap folder + a presence check.
	/// </summary>
	public interface ISettingsService
	{
		/// <summary>The live settings object. Mutate its properties to change AND auto-persist a setting.</summary>
		AppSettings Settings { get; }

		/// <summary>Resets every setting to its default and persists.</summary>
		Task ResetToDefaultsAsync();

		/// <summary>The offline map file Anvil draws, inside <see cref="MapDataFolder"/>: map setup's in-use file
		/// (<see cref="AppSettings.BasemapFilePath"/>) when set and present, else <c>usa_full.pmtiles</c>.</summary>
		string MapDataFileName { get; }

		/// <summary>
		/// The EFFECTIVE folder mapped to the <c>mapdata</c> WebView host: the in-use file's folder when map setup set one,
		/// else <see cref="LegacyMapDataFolder"/>. Read-only — write the settings on <see cref="Settings"/> to change it.
		/// </summary>
		string MapDataFolder { get; }

		/// <summary>Where <c>usa_full.pmtiles</c> is looked for (the older rule): the persisted
		/// <see cref="AppSettings.MapDataFolder"/> if set, else a runtime-resolved Desktop default (never a hardcoded path).</summary>
		string LegacyMapDataFolder { get; }

		/// <summary>Whether the basemap file is present in the given folder (or the effective one if null).</summary>
		bool MapDataFilePresent(string? folder = null);
	}
}
