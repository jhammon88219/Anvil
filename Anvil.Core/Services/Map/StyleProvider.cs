using System.Collections.Generic;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="IStyleProvider"/>: the five BUNDLED styles, served over the "mapassets" host.
	/// </summary>
	/// <remarks>
	/// ⚠️ THE LIST IS HARDCODED, AND USER-SUPPLIED STYLES ARE NOT A THING. An import feature existed
	/// briefly (a writable %LocalAppData% library on its own `mapstyles` host, merged in here) and was
	/// removed with the style editor — see the note in CLAUDE.md. Adding a basemap is a file in
	/// Assets/Map, a Content line in the csproj and a row below.
	/// </remarks>
	public sealed class StyleProvider : IStyleProvider
	{
		private const string BundledHost = "mapassets";

		// ⚠️ These ids are a CONTRACT — AppTheme.MapStyleId is matched against them exactly, and a miss
		// falls back to the first entry without a word. Note "dataVizlight" (lowercase L) and the file's
		// own "datVizGrayscale" typo; both are load-bearing spellings, not slips to tidy.
		public IReadOnlyList<MapStyle> GetStyles() => new[]
		{
			Bundle("regular", "Regular", "style.json"),
			Bundle("dark", "Dark", "style-dark.json"),
			Bundle("dataVizlight", "Data Viz Light", "style-dataVizLight.json"),
			Bundle("dataVizBlack", "Data Viz Black", "style-dataVizBlack.json", hasCountyLines: true),
			Bundle("dataVizGrayscale", "Data Viz Grayscale", "style-datVizGrayscale.json")
		};

		private static MapStyle Bundle(string id, string name, string file, bool hasCountyLines = false) =>
			new(id, name, file, $"https://{BundledHost}/{file}", hasCountyLines);
	}
}
