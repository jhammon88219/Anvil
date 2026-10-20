using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>A block of consecutive PMTiles tile ids, [<see cref="First"/>, <see cref="End"/>).</summary>
	public readonly record struct TileIdRange(ulong First, ulong End)
	{
		public ulong Count => End - First;
	}

	/// <summary>
	/// WHICH tiles an offline map holds: every tile at zooms 0…maxZoom that touches one of the picked areas' boxes, as
	/// sorted, non-overlapping tile-id ranges. Matches <c>pmtiles extract --region</c>'s "Region tiles" for a box (its
	/// cover at the top zoom plus every parent — the same as covering each zoom on its own).
	/// </summary>
	/// <remarks>
	/// Ranges, not a set of ids: Lower 48 at Full detail is ~20 million tiles, but a box's tiles are a few thousand id
	/// RANGES, because a tile's descendants are one contiguous id block (<see cref="PmtilesTileId.DescendantRange"/>) — so
	/// a quadtree walk emits one range per quadrant that lies wholly inside the box.
	/// </remarks>
	public static class BasemapTileCover
	{
		public static IReadOnlyList<TileIdRange> Build(IEnumerable<BasemapArea> areas, int maxZoom)
		{
			if (maxZoom < 0 || maxZoom > PmtilesTileId.MaxZoom) throw new ArgumentOutOfRangeException(nameof(maxZoom));
			var boxes = areas.SelectMany(a => a.Boxes).ToList();
			var ranges = new List<TileIdRange>();
			for (var z = 0; z <= maxZoom; z++)
			{
				foreach (var box in boxes)
				{
					var (x0, y0, x1, y1) = TileRect(box, z);
					Walk(0, 0, 0, z, x0, y0, x1, y1, ranges);
				}
			}
			return Merge(ranges);
		}

		/// <summary>The tiles at <paramref name="z"/> a box touches, inclusive (XYZ: y grows southward).</summary>
		public static (uint X0, uint Y0, uint X1, uint Y1) TileRect(BasemapBox box, int z)
		{
			var n = 1U << z;
			return (LonToX(box.West, n), LatToY(box.North, n), LonToX(box.East, n), LatToY(box.South, n));
		}

		private static uint LonToX(double lon, uint n) => Clamp(Math.Floor((lon + 180.0) / 360.0 * n), n);

		private static uint LatToY(double lat, uint n)
		{
			var r = lat * Math.PI / 180.0;
			return Clamp(Math.Floor((1.0 - Math.Asinh(Math.Tan(r)) / Math.PI) / 2.0 * n), n);
		}

		private static uint Clamp(double v, uint n) => (uint)Math.Clamp(v, 0, n - 1);

		// Quadtree walk at level k toward target zoom z: a quadrant wholly inside the rect is ONE id range; partly inside → its four children.
		private static void Walk(int k, uint x, uint y, int z, uint x0, uint y0, uint x1, uint y1, List<TileIdRange> into)
		{
			var shift = z - k;
			ulong lx = (ulong)x << shift, hx = (((ulong)x + 1) << shift) - 1;
			ulong ly = (ulong)y << shift, hy = (((ulong)y + 1) << shift) - 1;
			if (hx < x0 || lx > x1 || hy < y0 || ly > y1) return;
			if (lx >= x0 && hx <= x1 && ly >= y0 && hy <= y1)
			{
				var (first, count) = PmtilesTileId.DescendantRange(k, x, y, z);
				into.Add(new TileIdRange(first, first + count));
				return;
			}
			for (uint dy = 0; dy < 2; dy++)
				for (uint dx = 0; dx < 2; dx++)
					Walk(k + 1, x * 2 + dx, y * 2 + dy, z, x0, y0, x1, y1, into);
		}

		/// <summary>Sort and join touching or overlapping ranges (two areas can overlap — Canada and the Lower 48).</summary>
		public static IReadOnlyList<TileIdRange> Merge(List<TileIdRange> ranges)
		{
			ranges.Sort((a, b) => a.First.CompareTo(b.First));
			var merged = new List<TileIdRange>(ranges.Count);
			foreach (var r in ranges)
			{
				if (merged.Count > 0 && r.First <= merged[^1].End)
				{
					if (r.End > merged[^1].End) merged[^1] = merged[^1] with { End = r.End };
				}
				else merged.Add(r);
			}
			return merged;
		}

		/// <summary>The parts of [<paramref name="first"/>, <paramref name="end"/>) inside <paramref name="ranges"/>.</summary>
		public static IEnumerable<TileIdRange> Intersect(IReadOnlyList<TileIdRange> ranges, ulong first, ulong end)
		{
			// The first range ending after `first`.
			int lo = 0, hi = ranges.Count;
			while (lo < hi)
			{
				var mid = (lo + hi) / 2;
				if (ranges[mid].End <= first) lo = mid + 1; else hi = mid;
			}
			for (var i = lo; i < ranges.Count && ranges[i].First < end; i++)
			{
				yield return new TileIdRange(Math.Max(first, ranges[i].First), Math.Min(end, ranges[i].End));
			}
		}

		/// <summary>Whether any of [<paramref name="first"/>, <paramref name="end"/>) is inside <paramref name="ranges"/>.</summary>
		public static bool Touches(IReadOnlyList<TileIdRange> ranges, ulong first, ulong end) =>
			Intersect(ranges, first, end).Any();
	}
}
