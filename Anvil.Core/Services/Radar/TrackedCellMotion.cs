using System;
using System.Collections.Generic;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// The storm motion SRV removes by default: the VECTOR MEAN of every tracked storm cell's motion over a
	/// span (the radar's own cell tracking, as IEM serves it). Pure — no I/O, no clock.
	/// </summary>
	/// <remarks>
	/// Why this comes before the wind-profile chain: it describes how the storms on the screen actually MOVED,
	/// where Bunkers PREDICTS a supercell's motion from the wind profile. Measured 2026-10-14 against every
	/// built-in event (31 radar legs; truth = each rotating cell's own fitted track): mean of tracked cells
	/// 11.4 kt median error, Bunkers 20.2 kt, and Bunkers produced nothing at all on 5 legs where the local
	/// VAD profile was too shallow. The profile chain stays as the fallback for spans with too few cells.
	/// <para>⚠️ Every sighting counts once (a cell seen on 6 scans weighs 6×) — the same average the
	/// measurement scored. A cell with no motion yet (speed 0: first detection) is skipped.</para>
	/// </remarks>
	public static class TrackedCellMotion
	{
		/// <summary>Fewer moving sightings than this in the span = no answer (one young cell is not a
		/// storm motion); the caller falls back to the wind-profile chain.</summary>
		public const int MinSightings = 3;

		private const double MsPerKt = 0.514444;

		/// <summary>The mean motion over [<paramref name="fromUtc"/>, <paramref name="toUtc"/>], or null when
		/// there are fewer than <see cref="MinSightings"/> moving sightings. Heading is the bearing the
		/// storms move TOWARD (the cell table's direction is the one they come FROM).</summary>
		public static TrackedCellMotionResult? Mean(IReadOnlyList<StormCellScan> scans, DateTimeOffset fromUtc, DateTimeOffset toUtc)
		{
			double u = 0, v = 0;
			var n = 0;
			var ids = new HashSet<string>(StringComparer.Ordinal);
			foreach (var scan in scans)
			{
				if (scan.Valid < fromUtc || scan.Valid > toUtc) { continue; }
				foreach (var c in scan.Cells)
				{
					if (c.SpeedKt <= 0) { continue; }
					var toward = (c.DirFromDeg + 180) * Math.PI / 180.0;
					u += c.SpeedKt * Math.Sin(toward);
					v += c.SpeedKt * Math.Cos(toward);
					n++;
					ids.Add(c.Id);
				}
			}

			if (n < MinSightings) { return null; }
			u /= n;
			v /= n;
			var heading = (Math.Atan2(u, v) * 180.0 / Math.PI + 360.0) % 360.0;
			return new TrackedCellMotionResult(Math.Sqrt(u * u + v * v) * MsPerKt, heading, ids.Count, n);
		}
	}

	/// <summary>A tracked-cell mean motion: speed m/s, heading = bearing TOWARD, how many distinct cells and
	/// sightings it averages.</summary>
	public sealed record TrackedCellMotionResult(double SpeedMs, double HeadingDeg, int Cells, int Sightings);
}
