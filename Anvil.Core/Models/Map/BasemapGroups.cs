using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>
	/// The basemap's layer GROUPS — what the Map key's flyout ticks on and off, arranged as a TREE of categories
	/// (<see cref="Tree"/>). Each group is a set of protomaps source-layers (ROADS: one source-layer split by each
	/// road's <c>kind</c>), so one list covers all five bundled styles.
	/// </summary>
	/// <remarks>
	/// ⚠️ The ids are the PAGE's too — <c>Assets/Map/js/basemap.js</c> (<c>groupOf()</c> + <c>ROAD_KINDS</c>).
	/// Change both. They are also persisted (<c>AppSettings.HiddenBasemapGroups</c>), so never rename one — the
	/// retired ids (<see cref="LegacyRoads"/>, <see cref="LegacyNames"/>) are expanded by <see cref="Normalize"/>.
	/// ⭐ Add a group = a const + a leaf in <see cref="Tree"/> + its rule in basemap.js. Only the TREE is
	/// written by hand; <see cref="All"/> is read off it.
	/// </remarks>
	public static class BasemapGroups
	{
		public const string Land = "land";
		public const string Water = "water";
		public const string Highways = "highways";
		public const string MajorRoads = "major_roads";
		public const string MinorRoads = "minor_roads";
		public const string Paths = "paths";
		public const string Rail = "rail";
		public const string Buildings = "buildings";
		public const string Borders = "borders";
		public const string Counties = "counties";
		public const string Cities = "cities";
		public const string Pois = "pois";
		public const string WaterNames = "water_names";

		/// <summary>The ONE "roads" group before it was split by kind (2026-10-01): every road group.</summary>
		public const string LegacyRoads = "roads";

		/// <summary>The ONE "names" group before it was split (2026-10-01): every label group.</summary>
		public const string LegacyNames = "names";

		/// <summary>The flyout's rows, bottom of the map first, labels last. A category with ONE leaf is a plain
		/// row; with several it is an EXPANDER over them. Category labels are display only — never persisted.</summary>
		public static IReadOnlyList<(string Label, IReadOnlyList<(string Id, string Label)> Leaves)> Tree { get; } =
			new (string, IReadOnlyList<(string, string)>)[]
			{
				("Land", new[] { (Land, "Land") }),
				("Water", new[] { (Water, "Water") }),
				("Roads", new[]
				{
					(Highways, "Highways"),
					(MajorRoads, "Major roads"),
					(MinorRoads, "Minor roads"),
					(Paths, "Paths and other"),
					(Rail, "Rail"),
				}),
				("Buildings", new[] { (Buildings, "Buildings") }),
				("Borders", new[] { (Borders, "Borders") }),
				("Counties", new[] { (Counties, "Counties") }),
				("Place names", new[]
				{
					(Cities, "Cities"),
					(Pois, "Points of interest"),
					(WaterNames, "Water names"),
				}),
			};

		/// <summary>Every group, flattened in <see cref="Tree"/> order.</summary>
		public static IReadOnlyList<(string Id, string Label)> All { get; } = Tree.SelectMany(c => c.Leaves).ToList();

		/// <summary>The road groups — what <see cref="LegacyRoads"/> expands to.</summary>
		public static IReadOnlyList<string> Roads { get; } = LeavesOf("Roads");

		/// <summary>The label groups — what <see cref="LegacyNames"/> expands to.</summary>
		public static IReadOnlyList<string> Names { get; } = LeavesOf("Place names");

		private static IReadOnlyList<string> LeavesOf(string category) =>
			Tree.Single(c => c.Label == category).Leaves.Select(l => l.Id).ToList();

		/// <summary>Known ids only, each once, in <see cref="All"/> order — what a settings file or a set of
		/// ticks becomes before it is stored or sent to the page. A retired id becomes every group it was split into.</summary>
		public static List<string> Normalize(IEnumerable<string>? ids)
		{
			var set = new HashSet<string>(ids ?? Enumerable.Empty<string>());
			if (set.Remove(LegacyRoads)) { set.UnionWith(Roads); }
			if (set.Remove(LegacyNames)) { set.UnionWith(Names); }
			return All.Select(g => g.Id).Where(set.Contains).ToList();
		}
	}
}
