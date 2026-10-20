using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>A lon/lat box, west → east, south → north. Never crosses the date line — an area that does is two boxes.</summary>
	public readonly record struct GeoBox(double West, double South, double East, double North)
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
	public sealed record BasemapArea(string Code, string Name, IReadOnlyList<GeoBox> Boxes,
		double StandardMegabytes, double FullMegabytes, bool RadarsComingSoon = false)
	{
		public bool Contains(double latitude, double longitude) => Boxes.Any(b => b.Contains(latitude, longitude));
	}

	/// <summary>
	/// THE fixed list of offline-map areas (the user's call, 2026-10-20: the user picks PLACES; radar sites don't shape the
	/// download — they only label each row with a count, worked out at runtime). Order = the list's order = the order the
	/// codes appear in a file name (<see cref="BasemapFileNames"/>), so append, never reorder, and never change a code
	/// (it's in users' file names).
	/// </summary>
	/// <remarks>
	/// Sizes: a <c>pmtiles extract --dry-run</c> of each box against the 2026-10-20 Protomaps build, Standard = detail
	/// level 13, Full = 15. They drift as OpenStreetMap grows — "about" numbers until the planner reads the real build.
	/// ⚠️ Canada's box overlaps the northern Lower 48, so Lower 48 + Canada sums a little high.
	/// </remarks>
	public static class BasemapAreas
	{
		public static readonly BasemapArea Lower48 = new("l48", "Lower 48 states",
			new[] { new GeoBox(-127.0, 23.5, -65.0, 50.0) }, 4400, 19000);

		// The far Aleutians sit past the date line — a second box, one checkbox.
		public static readonly BasemapArea Alaska = new("ak", "Alaska",
			new[] { new GeoBox(-180.0, 51.0, -129.0, 72.0), new GeoBox(172.0, 51.0, 180.0, 54.0) }, 444, 1600);

		public static readonly BasemapArea Hawaii = new("hi", "Hawaii",
			new[] { new GeoBox(-161.0, 18.5, -154.0, 22.5) }, 7.1, 32);

		public static readonly BasemapArea PuertoRico = new("pr", "Puerto Rico and U.S. Virgin Islands",
			new[] { new GeoBox(-68.0, 17.5, -64.3, 18.8) }, 11, 75);

		public static readonly BasemapArea Guam = new("gu", "Guam",
			new[] { new GeoBox(144.5, 13.1, 145.1, 13.8) }, 0.9, 3.3);

		public static readonly BasemapArea Canada = new("ca", "Canada",
			new[] { new GeoBox(-141.0, 41.6, -52.0, 70.0) }, 5200, 19000, RadarsComingSoon: true);

		/// <summary>Every area, in list (and file-name) order.</summary>
		public static IReadOnlyList<BasemapArea> All { get; } = new[] { Lower48, Alaska, Hawaii, PuertoRico, Guam, Canada };

		/// <summary>The area with <paramref name="code"/>, or null.</summary>
		public static BasemapArea? ByCode(string code) => All.FirstOrDefault(a => a.Code == code);
	}
}
