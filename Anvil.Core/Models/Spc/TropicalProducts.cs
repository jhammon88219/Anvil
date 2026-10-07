using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>One tropical-cyclone product: a VTEC phenomenon + significance with the NWS's own name and colour.</summary>
	/// <param name="Id">"HU.W" — VTEC phenom.sig; the persisted (hidden-kinds) and page id.</param>
	/// <param name="Name">The NWS's name ("Hurricane Warning").</param>
	/// <param name="RowLabel">The section row's short name, under its WARNINGS / WATCHES heading ("Hurricane").</param>
	/// <param name="Fill">The NWS hazard-map colour (DATA — never themed).</param>
	/// <param name="Priority">The NWS hazard-map priority: LOWER = more important = drawn ON TOP.</param>
	/// <param name="Definition">What it means, in a line.</param>
	public sealed record TropicalProduct(string Id, string Phenom, string Sig, string Name, string RowLabel,
		string Fill, int Priority, string Definition)
	{
		public bool IsWarning => Sig == "W";
	}

	/// <summary>
	/// The tropical-cyclone watches and warnings, as the NWS issues them — the ONE list the service, the section's
	/// rows and the page's colours come from.
	/// </summary>
	/// <remarks>
	/// ⚠️ COLOURS + PRIORITIES ARE THE NWS's, read from https://www.weather.gov/help-map (2026-10-07), not picked:
	/// change them only to follow that table. They are MIRRORED in <c>Assets/Map/js/tropical.js</c> only through
	/// the file — the service writes <c>fill</c> + <c>prio</c> onto every feature — so there is one copy.
	/// ⚠️ DEFINITIONS are NHC's glossary (https://www.nhc.noaa.gov/aboutgloss.shtml), condensed; the Extreme Wind
	/// Warning is the NWS's (it is a WFO product, not in NHC's glossary).
	/// Order = priority (most important first) — the order the section lists them in.
	/// </remarks>
	public static class TropicalProducts
	{
		public static readonly IReadOnlyList<TropicalProduct> All = new[]
		{
			new TropicalProduct("EW.W", "EW", "W", "Extreme Wind Warning", "Extreme wind", "#FF8C00", 3,
				"Sustained winds of 115 mph or more from a major hurricane, usually within the hour — take shelter now."),
			new TropicalProduct("SS.W", "SS", "W", "Storm Surge Warning", "Storm surge", "#B524F7", 17,
				"Life-threatening inundation from rising water moving inland from the shoreline, generally within 36 hours."),
			new TropicalProduct("HU.W", "HU", "W", "Hurricane Warning", "Hurricane", "#DC143C", 19,
				"Sustained winds of 74 mph or more expected — issued 36 hours before tropical-storm-force winds arrive."),
			new TropicalProduct("TR.W", "TR", "W", "Tropical Storm Warning", "Tropical storm", "#B22222", 31,
				"Sustained winds of 39 to 73 mph expected within 36 hours."),
			new TropicalProduct("SS.A", "SS", "A", "Storm Surge Watch", "Storm surge", "#DB7FF7", 53,
				"Life-threatening inundation from rising water moving inland is possible, generally within 48 hours."),
			new TropicalProduct("HU.A", "HU", "A", "Hurricane Watch", "Hurricane", "#FF00FF", 54,
				"Sustained winds of 74 mph or more possible — issued 48 hours before tropical-storm-force winds arrive."),
			new TropicalProduct("TR.A", "TR", "A", "Tropical Storm Watch", "Tropical storm", "#F08080", 57,
				"Sustained winds of 39 to 73 mph possible within 48 hours."),
		};

		public static TropicalProduct? Find(string? phenom, string? sig) =>
			All.FirstOrDefault(p => p.Phenom == phenom && p.Sig == sig);

		public static TropicalProduct? ById(string? id) => All.FirstOrDefault(p => p.Id == id);

		/// <summary>The VTEC phenomena, for the queries.</summary>
		public static readonly IReadOnlyList<string> Phenomena = new[] { "HU", "TR", "SS", "EW" };
	}
}
