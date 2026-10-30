using System;
using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>
	/// The HATCH scrubber's cell looks (Primitives/HatchScrubber), read off the SAME per-frame fill every scrubber reads
	/// (<c>RadarFrameSegment.Fill</c> = ½ download + ½ build, exactly 1 only when the frame is lit — RadarViewModel's
	/// RefreshSegmentFill). Density is progress, the edge hatch's ladder over time instead of space:
	/// nothing → an outline; downloading → a sparse single diagonal; building → dense single diagonals; ready → fully
	/// cross-hatched. (The frame on screen is drawn solid on top, by the scrubber.)
	/// </summary>
	public static class HatchScrubberLadder
	{
		public const int Empty = 0, Downloading = 1, Building = 2, Ready = 3;

		/// <summary>The download half of the fill is done at this value.</summary>
		public const double DownloadDone = 0.5;

		public static int Level(double fill, bool isReady)
		{
			if (isReady || fill >= 1) { return Ready; }
			if (!(fill > 0)) { return Empty; }
			return fill < DownloadDone ? Downloading : Building;
		}

		/// <summary>The gap between diagonals at a level, px (sparse while downloading, dense after).</summary>
		public static double Spacing(int level) => level == Downloading ? 6 : 3;

		/// <summary>The second (crossing) family of diagonals only once ready.</summary>
		public static bool Crosses(int level) => level == Ready;
	}

	/// <summary>
	/// The geometry of one hatch-scrubber cell (pure, tested): a PARALLELOGRAM leaning right — top edge shifted right by
	/// the slant — and its diagonals clipped to it. Coordinates are the cell's own: x ∈ [0, width], y ∈ [0, height], y down.
	/// </summary>
	public static class HatchScrubberGeometry
	{
		/// <summary>The lean: half the height, but never more than half the width (a narrow cell stays a cell).</summary>
		public static double Slant(double width, double height) => Math.Max(0, Math.Min(height / 2, width / 2));

		/// <summary>The cell's corners: bottom-left, top-left, top-right, bottom-right.</summary>
		public static (double X, double Y)[] Outline(double width, double height)
		{
			double s = Slant(width, height);
			return new[] { (0.0, height), (s, 0.0), (width, 0.0), (width - s, height) };
		}

		/// <summary>
		/// The cell's diagonals for a <see cref="HatchScrubberLadder"/> level: "/" lines at the level's spacing, plus "\"
		/// lines once ready, each clipped to the parallelogram. Empty for the empty level or a degenerate cell.
		/// </summary>
		public static IReadOnlyList<HatchSegment> Lines(double width, double height, int level)
		{
			var lines = new List<HatchSegment>();
			if (level == HatchScrubberLadder.Empty || !(width > 0) || !(height > 0)) { return lines; }
			var outline = Outline(width, height);
			double sp = HatchScrubberLadder.Spacing(level);
			for (double c = -height; c <= width + height; c += sp)
			{
				// "/": (c + t, height − t);   "\": (c + t, t);   t over a span that crosses the whole cell.
				Add(lines, outline, c - height, 2 * height, c + 2 * height, -height);
				if (HatchScrubberLadder.Crosses(level)) { Add(lines, outline, c - height, -height, c + 2 * height, 2 * height); }
			}
			return lines;
		}

		private static void Add(List<HatchSegment> lines, (double X, double Y)[] poly, double x0, double y0, double x1, double y1)
		{
			if (Clip(poly, x0, y0, x1, y1) is { } s) { lines.Add(s); }
		}

		/// <summary>Cyrus–Beck: the part of segment (x0,y0)→(x1,y1) inside the convex polygon, or null.</summary>
		internal static HatchSegment? Clip((double X, double Y)[] poly, double x0, double y0, double x1, double y1)
		{
			double cx = 0, cy = 0;
			foreach (var p in poly) { cx += p.X; cy += p.Y; }
			cx /= poly.Length; cy /= poly.Length;

			double tIn = 0, tOut = 1, dx = x1 - x0, dy = y1 - y0;
			for (int i = 0; i < poly.Length; i++)
			{
				var a = poly[i];
				var b = poly[(i + 1) % poly.Length];
				double nx = -(b.Y - a.Y), ny = b.X - a.X;
				if (nx * (cx - a.X) + ny * (cy - a.Y) < 0) { nx = -nx; ny = -ny; } // point the normal inward
				double num = nx * (x0 - a.X) + ny * (y0 - a.Y);
				double den = nx * dx + ny * dy;
				if (Math.Abs(den) < 1e-12)
				{
					if (num < 0) { return null; }
					continue;
				}
				double t = -num / den;
				if (den > 0) { tIn = Math.Max(tIn, t); } else { tOut = Math.Min(tOut, t); }
				if (tIn > tOut) { return null; }
			}
			if (tOut - tIn < 1e-9) { return null; }
			return new HatchSegment(x0 + dx * tIn, y0 + dy * tIn, x0 + dx * tOut, y0 + dy * tOut);
		}
	}
}
