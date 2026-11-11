using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>A lon/lat box, west → east, south → north. Never crosses the date line — an area that does is two boxes.</summary>
	public readonly record struct BasemapBox(double West, double South, double East, double North)
	{
		public bool Contains(double latitude, double longitude) =>
			longitude >= West && longitude <= East && latitude >= South && latitude <= North;
	}

	/// <summary>
	/// One place an offline map can cover: its file-name <see cref="Code"/>, the name the user reads, the box(es) Anvil
	/// downloads, and what it costs at each detail level.
	/// </summary>
	/// <param name="StandardMegabytes">The download at Standard detail, MB (measured — see <see cref="BasemapAreas"/>).</param>
	/// <param name="FullMegabytes">The same at Full detail.</param>
	/// <param name="RadarsComingSoon">Anvil has no radars here YET (Canada) — the row says "Radars coming soon"
	/// instead of a count, so a user can include the map ahead of them.</param>
	public sealed record BasemapArea(string Code, string Name, IReadOnlyList<BasemapBox> Boxes,
		double StandardMegabytes, double FullMegabytes, bool RadarsComingSoon = false)
	{
		public bool Contains(double latitude, double longitude) => Boxes.Any(b => b.Contains(latitude, longitude));
	}

	/// <summary>Two areas whose boxes touch and the tiles they share, MB at each detail (<see cref="BasemapAreas.Overlaps"/>).
	/// <paramref name="Codes"/> = the two area codes, in list order.</summary>
	public sealed record BasemapAreaOverlap(IReadOnlyList<string> Codes, double StandardMegabytes, double FullMegabytes);

	/// <summary>
	/// THE fixed list of offline-map areas (the user's call, 2026-10-20: the user picks PLACES; radar sites don't shape the
	/// download — they only label each row with a count, worked out at runtime). Order = the list's order = the order the
	/// codes appear in a file name (<see cref="BasemapFileNames"/>), so append, never reorder, and never change a code
	/// (it's in users' file names).
	/// </summary>
	/// <remarks>
	/// Sizes: <c>TiltCheck -- --basemap-sizes</c> (the downloader's own planner, no tiles fetched) on the 2026-11-11
	/// Protomaps build, Standard = detail level 13, Full = 15. Each area is its cost ON TOP of the world overview (its own
	/// zoom 0-7 tiles are already in it). They drift as OpenStreetMap grows — re-run the check and paste.
	/// ⚠️ Boxes that touch SHARE tiles (Canada's box covers the northern Lower 48 and the Yukon side of Alaska) — those are
	/// <see cref="Overlaps"/>, taken off once by <see cref="EstimateMegabytes"/>. Without them all six at Full read "about
	/// 40 GB" for a 31.5 GB file.
	/// </remarks>
	public static class BasemapAreas
	{
		public static readonly BasemapArea Lower48 = new("l48", "Lower 48 states",
			new[] { new BasemapBox(-127.0, 23.5, -65.0, 50.0) }, 4400, 19399);

		// The far Aleutians sit past the date line — a second box, one checkbox.
		public static readonly BasemapArea Alaska = new("ak", "Alaska",
			new[] { new BasemapBox(-180.0, 51.0, -129.0, 72.0), new BasemapBox(172.0, 51.0, 180.0, 54.0) }, 423, 1547);

		public static readonly BasemapArea Hawaii = new("hi", "Hawaii",
			new[] { new BasemapBox(-161.0, 18.5, -154.0, 22.5) }, 6.8, 32);

		public static readonly BasemapArea PuertoRico = new("pr", "Puerto Rico and U.S. Virgin Islands",
			new[] { new BasemapBox(-68.0, 17.5, -64.3, 18.8) }, 11, 75);

		public static readonly BasemapArea Guam = new("gu", "Guam",
			new[] { new BasemapBox(144.5, 13.1, 145.1, 13.8) }, 0.7, 3.1);

		public static readonly BasemapArea Canada = new("ca", "Canada",
			new[] { new BasemapBox(-141.0, 41.6, -52.0, 70.0) }, 5146, 18685, RadarsComingSoon: true);

		/// <summary>
		/// What two areas SHARE (measured: cost(A) + cost(B) − cost(A+B), same check, same build). Only pairs whose boxes
		/// touch are listed; no three boxes meet, so pairs are the whole correction. A new area that touches another = a row.
		/// </summary>
		public static IReadOnlyList<BasemapAreaOverlap> Overlaps { get; } = new[]
		{
			new BasemapAreaOverlap(new[] { "l48", "ca" }, 1844, 7695),
			new BasemapAreaOverlap(new[] { "ak", "ca" }, 234, 842),
		};

		/// <summary>
		/// About how big an offline map of <paramref name="areas"/> is, MB: each area + the world overview every map
		/// carries, less each overlap whose BOTH areas are ticked. Tile data only — the file's index adds well under 1 %.
		/// </summary>
		public static double EstimateMegabytes(IEnumerable<BasemapArea> areas, bool fullDetail)
		{
			var picked = areas.ToList();
			var codes = picked.Select(a => a.Code).ToHashSet();
			return picked.Sum(a => fullDetail ? a.FullMegabytes : a.StandardMegabytes)
				+ WorldOverviewMegabytes
				- Overlaps.Where(o => o.Codes.All(codes.Contains)).Sum(o => fullDetail ? o.FullMegabytes : o.StandardMegabytes);
		}

		/// <summary>Every area, in list (and file-name) order.</summary>
		public static IReadOnlyList<BasemapArea> All { get; } = new[] { Lower48, Alaska, Hawaii, PuertoRico, Guam, Canada };

		/// <summary>The area with <paramref name="code"/>, or null.</summary>
		public static BasemapArea? ByCode(string code) => All.FirstOrDefault(a => a.Code == code);

		/// <summary>
		/// The WHOLE WORLD at low detail, in EVERY offline map (the user's call, 2026-10-20): zoomed out, an offline map then
		/// draws exactly what the online one does — every coastline, country and label — instead of whichever big tiles
		/// happened to touch the picked areas. Not a pickable area (not in <see cref="All"/>, never in a file name).
		/// </summary>
		public const int WorldOverviewZoom = 7;

		/// <summary>The overview's cost: zooms 0-7 measured 189 MB on the 2026-10-20 build, 190 on 2026-11-11 (0-6 was 45, 0-5 15).</summary>
		public const double WorldOverviewMegabytes = 190;

		public static readonly BasemapArea WorldOverview = new("world", "The whole world (overview)",
			new[] { new BasemapBox(-180.0, -85.0511, 180.0, 85.0511) }, WorldOverviewMegabytes, WorldOverviewMegabytes);
	}
}
