using System.Collections.Generic;
using System.Linq;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="IStyleProvider"/>: the five BUNDLED styles, plus whatever the user has imported.
	/// </summary>
	/// <remarks>
	/// ⚠️ The bundled list is hardcoded and the imported list is read from disk on every call, which is
	/// deliberate: an import has to appear in the picker immediately, and the alternative — caching plus an
	/// invalidation the import path has to remember to call — is a bug waiting for the day someone adds a
	/// second way to import. The read is a directory enumerate of a folder holding a handful of files.
	/// ⚠️ Bundled styles come FIRST and imported ones after, so a user's import can never quietly displace
	/// the style a theme points at (themes resolve by id, but the list order is what a person scans).
	/// </remarks>
	public sealed class StyleProvider : IStyleProvider
	{
		private const string BundledHost = "mapassets";

		private readonly IMapStyleLibrary _library;

		public StyleProvider(IMapStyleLibrary library)
		{
			_library = library;
		}

		public IReadOnlyList<MapStyle> GetStyles() =>
			Bundled.Concat(_library.GetStyles()).ToList();

		// ⚠️ These ids are a CONTRACT — AppTheme.MapStyleId is matched against them exactly, and a miss
		// falls back to the first entry without a word. Note "dataVizlight" (lowercase L) and the file's
		// own "datVizGrayscale" typo; both are load-bearing spellings, not slips to tidy.
		private static IEnumerable<MapStyle> Bundled => new[]
		{
			Bundle("regular", "Regular", "style.json"),
			Bundle("dark", "Dark", "style-dark.json"),
			Bundle("dataVizlight", "Data Viz Light", "style-dataVizLight.json"),
			Bundle("dataVizBlack", "Data Viz Black", "style-dataVizBlack.json"),
			Bundle("dataVizGrayscale", "Data Viz Grayscale", "style-datVizGrayscale.json")
		};

		private static MapStyle Bundle(string id, string name, string file) =>
			new(id, name, file, $"https://{BundledHost}/{file}");
	}
}
