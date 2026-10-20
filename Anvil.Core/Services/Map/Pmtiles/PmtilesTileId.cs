using System;

namespace Anvil.Services
{
	/// <summary>
	/// PMTiles v3 tile ids: every tile of every zoom on ONE Hilbert curve — zoom z's ids start after all of zooms 0…z-1
	/// (<see cref="ZoomBase"/>), then run in Hilbert order. The property the extractor leans on: a tile's descendants at a
	/// deeper zoom are one CONTIGUOUS id block (<see cref="DescendantRange"/>). Port of go-pmtiles <c>ZxyToID</c>.
	/// </summary>
	public static class PmtilesTileId
	{
		/// <summary>The deepest zoom an id can address here (4^27 fits a ulong with room); Protomaps stops at 15.</summary>
		public const int MaxZoom = 26;

		/// <summary>The first id of zoom <paramref name="z"/> = 4^0 + … + 4^(z-1).</summary>
		public static ulong ZoomBase(int z)
		{
			if (z < 0 || z > MaxZoom) throw new ArgumentOutOfRangeException(nameof(z));
			return ((1UL << (2 * z)) - 1) / 3;
		}

		/// <summary>The id of tile (<paramref name="z"/>, <paramref name="x"/>, <paramref name="y"/>), XYZ (y down).</summary>
		public static ulong FromZxy(int z, uint x, uint y)
		{
			var acc = ZoomBase(z);
			if (z == 0) return acc;
			var size = 1U << z;
			if (x >= size || y >= size) throw new ArgumentOutOfRangeException(nameof(x), $"({x},{y}) is off zoom {z}");
			for (var s = 1U << (z - 1); s > 0; s >>= 1)
			{
				var rx = s & x;
				var ry = s & y;
				acc += (ulong)((3 * rx) ^ ry) * s;
				if (ry == 0)
				{
					if (rx != 0)
					{
						x = s - 1 - x; // wraps on purpose: only the bits below s matter from here on
						y = s - 1 - y;
					}
					(x, y) = (y, x);
				}
			}
			return acc;
		}

		/// <summary>
		/// The ids of every descendant of (<paramref name="z"/>, <paramref name="x"/>, <paramref name="y"/>) at zoom
		/// <paramref name="deeperZoom"/>: ONE contiguous block, [<c>First</c>, <c>First + Count</c>).
		/// </summary>
		public static (ulong First, ulong Count) DescendantRange(int z, uint x, uint y, int deeperZoom)
		{
			if (deeperZoom < z) throw new ArgumentOutOfRangeException(nameof(deeperZoom));
			var shift = 2 * (deeperZoom - z);
			var index = FromZxy(z, x, y) - ZoomBase(z); // the tile's place along zoom z's curve
			return (ZoomBase(deeperZoom) + (index << shift), 1UL << shift);
		}
	}
}
