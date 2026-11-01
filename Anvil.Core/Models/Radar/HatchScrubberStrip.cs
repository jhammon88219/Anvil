using System;
using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>
	/// The HATCH scrubber's lines (Primitives/HatchScrubber; pure, so it's tested): the EDGE HATCH itself
	/// (<see cref="EdgeHatchGeometry.Build"/>, thinning on), laid along the scrubber as ONE unclipped pattern — the user's
	/// call 2026-11-01, "match the current edge and just place it horizontally". The strip is shallower than the edge band,
	/// so the edge's spacing and weight are scaled by depth / <see cref="EdgeHatchSizes.DefaultWidth"/> (the edge's own
	/// proportions at any height).
	/// <para>PROGRESS: each line belongs to the frame its BASE point falls in (equal slots, the host's pointer rule) and
	/// reaches its edge share × that frame's fill (<c>RadarFrameSegment.Fill</c>, 1 only when lit) — a loading frame's
	/// lines grow out of the edge. The frame ON SCREEN's lines come back apart, so the scrubber draws them bolder.</para>
	/// </summary>
	public static class HatchScrubberStrip
	{
		/// <summary>The strip's height, px (the user's pick from the mock-up, 2026-11-01). The console's hit strip is 20;
		/// the lines bleed 4 px above it.</summary>
		public const double Depth = 24;

		/// <summary>The current frame's lines are this much heavier (and drawn at full opacity).</summary>
		public const double CurrentWeight = 1.75;

		/// <summary>The edge's spacing or weight, scaled to a strip <paramref name="depth"/> deep.</summary>
		public static double Scaled(double edgeValue, double depth) => edgeValue * depth / EdgeHatchSizes.DefaultWidth;

		/// <summary>
		/// The strip's lines in band coordinates (edge along y = 0, as <see cref="EdgeHatchGeometry.Build"/>):
		/// <paramref name="fills"/> = each frame's fill, 0–1; <paramref name="current"/> = the frame on screen (-1 = none).
		/// <paramref name="edgeSpacing"/> is the EDGE's (Settings → Edges) — scaled here. With every fill 1 the two lists
		/// together are exactly the edge's lines.
		/// </summary>
		public static (List<HatchSegment> Lines, List<HatchSegment> Current) Build(
			double length, double depth, double edgeSpacing, IReadOnlyList<double> fills, int current)
		{
			var lines = new List<HatchSegment>();
			var onScreen = new List<HatchSegment>();
			int count = fills.Count;
			if (count == 0 || !(length > 0) || !(depth > 0) || !double.IsFinite(length) || !double.IsFinite(depth)) { return (lines, onScreen); }
			double spacing = Math.Max(1, Scaled(double.IsFinite(edgeSpacing) ? edgeSpacing : EdgeHatchSizes.DefaultSpacing, depth));
			double slot = length / count;

			// The edge's loop (EdgeHatchGeometry.Build) — same base points, same reach ladder, same clip to [0, length].
			int first = -(int)Math.Ceiling(depth / spacing) - 1;
			int last = (int)Math.Ceiling(length / spacing) + 1;
			for (int k = first; k <= last; k++)
			{
				double x0 = k * spacing;
				int frame = FrameOf(x0, slot, count);
				double fill = Math.Clamp(double.IsFinite(fills[frame]) ? fills[frame] : 0, 0, 1);
				double reach = EdgeHatchGeometry.Reach(Math.Abs(k) + 1) * depth * fill;
				if (!(reach > 0)) { continue; }
				var into = frame == current ? onScreen : lines;
				foreach (var dir in new[] { 1, -1 })
				{
					double lo = dir > 0 ? -x0 : x0 - length;
					double hi = dir > 0 ? length - x0 : x0;
					double t0 = Math.Max(0, lo), t1 = Math.Min(reach, hi);
					if (t1 <= t0) { continue; }
					into.Add(new HatchSegment(x0 + dir * t0, t0, x0 + dir * t1, t1));
				}
			}
			return (lines, onScreen);
		}

		/// <summary>The frame a base point at <paramref name="x"/> belongs to: its equal slot, clamped (base points past
		/// either end belong to the end frames).</summary>
		public static int FrameOf(double x, double slot, int count) =>
			Math.Clamp((int)Math.Floor(x / slot), 0, count - 1);
	}
}
