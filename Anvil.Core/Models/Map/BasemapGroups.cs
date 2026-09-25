using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>
	/// The basemap's layer GROUPS — what the Map key's flyout ticks on and off. Each is a set of protomaps
	/// source-layers, so one list covers all five bundled styles.
	/// </summary>
	/// <remarks>
	/// ⚠️ The ids are the PAGE's too — <c>Assets/Map/js/basemap.js groupOf()</c>. Change both. They are also
	/// persisted (<c>AppSettings.HiddenBasemapGroups</c>), so never rename one.
	/// </remarks>
	public static class BasemapGroups
	{
		public const string Land = "land";
		public const string Water = "water";
		public const string Roads = "roads";
		public const string Buildings = "buildings";
		public const string Borders = "borders";
		public const string Counties = "counties";
		public const string Names = "names";

		/// <summary>Every group, in flyout order: bottom of the map first, labels last.</summary>
		public static IReadOnlyList<(string Id, string Label)> All { get; } = new[]
		{
			(Land, "Land"),
			(Water, "Water"),
			(Roads, "Roads"),
			(Buildings, "Buildings"),
			(Borders, "Borders"),
			(Counties, "Counties"),
			(Names, "Place names"),
		};

		/// <summary>Known ids only, each once, in <see cref="All"/> order — what a settings file or a set of
		/// ticks becomes before it is stored or sent to the page.</summary>
		public static List<string> Normalize(IEnumerable<string>? ids)
		{
			var set = new HashSet<string>(ids ?? Enumerable.Empty<string>());
			return All.Select(g => g.Id).Where(set.Contains).ToList();
		}
	}
}
