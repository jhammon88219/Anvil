using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>
	/// The basemap's layer GROUPS — what the Map key's flyout ticks on and off. Each is a set of protomaps
	/// source-layers (the ROAD groups: one source-layer split by each road's <c>kind</c>), so one list covers all
	/// five bundled styles.
	/// </summary>
	/// <remarks>
	/// ⚠️ The ids are the PAGE's too — <c>Assets/Map/js/basemap.js</c> (<c>groupOf()</c> + <c>ROAD_KINDS</c>).
	/// Change both. They are also persisted (<c>AppSettings.HiddenBasemapGroups</c>), so never rename one — the
	/// one retired id, <see cref="LegacyRoads"/>, is migrated by <see cref="Normalize"/>.
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
		public const string Names = "names";

		/// <summary>The ONE "roads" group before it was split by kind (2026-10-01). A settings file still naming
		/// it means every road group was unticked; <see cref="Normalize"/> expands it.</summary>
		public const string LegacyRoads = "roads";

		/// <summary>The road groups, in flyout order — what <see cref="LegacyRoads"/> expands to.</summary>
		public static IReadOnlyList<string> Roads { get; } = new[] { Highways, MajorRoads, MinorRoads, Paths, Rail };

		/// <summary>Every group, in flyout order: bottom of the map first, labels last. <c>Heading</c> is a
		/// caption drawn ABOVE that row (the first road row's "Roads"); <c>IsSub</c> rows are indented under it.</summary>
		public static IReadOnlyList<(string Id, string Label, string? Heading, bool IsSub)> All { get; } = new (string, string, string?, bool)[]
		{
			(Land, "Land", null, false),
			(Water, "Water", null, false),
			(Highways, "Highways", "Roads", true),
			(MajorRoads, "Major roads", null, true),
			(MinorRoads, "Minor roads", null, true),
			(Paths, "Paths and other", null, true),
			(Rail, "Rail", null, true),
			(Buildings, "Buildings", null, false),
			(Borders, "Borders", null, false),
			(Counties, "Counties", null, false),
			(Names, "Place names", null, false),
		};

		/// <summary>Known ids only, each once, in <see cref="All"/> order — what a settings file or a set of
		/// ticks becomes before it is stored or sent to the page. The legacy "roads" id becomes every road group.</summary>
		public static List<string> Normalize(IEnumerable<string>? ids)
		{
			var set = new HashSet<string>(ids ?? Enumerable.Empty<string>());
			if (set.Remove(LegacyRoads)) { set.UnionWith(Roads); }
			return All.Select(g => g.Id).Where(set.Contains).ToList();
		}
	}
}
