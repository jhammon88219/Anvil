using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// REGIME-AWARE POLLING (docs/regime-aware-polling.md): when the next live frame of the watched tilt should be in the
	/// chunks bucket, and so how long the NowCast poll waits. Pure — the live volume's own plan (<see cref="LiveScanSchedule"/>)
	/// in, a time out.
	/// </summary>
	/// <remarks>
	/// ⚠️ The constants are MEASURED (TiltCheck -- --regime, 2026-10-07, VCP 212/215/35 at KTBW/KTLH/KBYX/KAMX/KTLX):
	/// a cut ends at volume start + Σ planned sweeps + ~0.3 s per cut (±2 s); its chunk lands a median ~1.7 s later, the
	/// worst seen ~9 s; the next volume starts ~9 s after the last cut. MESO-SAILS and MRLE were NOT live then — the
	/// retry window is what absorbs a plan that's off. Re-measure before retuning.
	/// </remarks>
	public static class LivePollPlanner
	{
		/// <summary>Measured: a cut costs this beyond its planned sweep (antenna move + settle).</summary>
		public const double OverheadPerCut = 0.3;
		/// <summary>Look this long after a frame's last cut should end: the median upload (~1.7 s) + the model's spread.</summary>
		public const double UploadSlack = 3;
		/// <summary>Measured: the next volume's first radial, after the previous volume's last cut.</summary>
		public const double VolumeGap = 9;
		/// <summary>A frame that hasn't landed by its time: check again this often…</summary>
		public const double RetrySeconds = 5;
		/// <summary>…for this long past its time (worst upload seen ~9 s); beyond it the plan is off — steady checks.</summary>
		public const double LateCap = 45;
		/// <summary>Steady checks when there's no prediction (between volumes before a period is learned, or a plan that's off).</summary>
		public const double TailSeconds = 15;
		/// <summary>Never wait longer than this — a missed regime change costs at most one of these.</summary>
		public const double MaxWait = 120;
		public const double MinWait = 2;

		/// <summary>The FRAMES of <paramref name="tilt"/> (null = the lowest) in a plan: each run of consecutive cuts at that
		/// angle (a surveillance cut + its Doppler partner) is one frame, done when its LAST cut ends. Returns that cut's
		/// number, the run's planned START and its planned END, seconds after the volume starts.</summary>
		public static List<(int LastCut, double StartSeconds, double EndSeconds)> FrameEnds(IReadOnlyList<PlannedCut> plan, double? tilt)
		{
			var frames = new List<(int, double, double)>();
			if (plan.Count == 0) return frames;
			var target = tilt ?? plan.Min(c => c.Angle);
			bool At(PlannedCut c) => Math.Abs(c.Angle - target) < Level2Format.TiltAngleTol;
			double t = 0, runStart = 0;
			for (var i = 0; i < plan.Count; i++)
			{
				if (i > 0) t += OverheadPerCut;
				if (At(plan[i]) && (i == 0 || !At(plan[i - 1]))) runStart = t;
				t += plan[i].SweepSeconds;
				if (At(plan[i]) && (i == plan.Count - 1 || !At(plan[i + 1]))) frames.Add((plan[i].Number, runStart, t));
			}
			return frames;
		}

		// A frame whose scan began within this of the newest scan already held IS that scan (the held time is its
		// first radial; the plan's start is ±2 s).
		private const double HeldSlack = 20;

		/// <summary>When the next frame of <paramref name="tilt"/> should be in the bucket (UTC), or null when it can't be
		/// said (no plan; or the volume has no frames left and no volume <paramref name="period"/> has been learned).</summary>
		/// <param name="period">This site's last volume start → next volume start, if seen (AVSET makes it shorter than the plan).</param>
		/// <param name="heldScan">The newest live scan already built (its first radial) — that frame is never "next".</param>
		public static DateTimeOffset? ExpectedNext(LiveScanSchedule s, double? tilt, TimeSpan? period, DateTimeOffset? heldScan = null)
		{
			var frames = FrameEnds(s.Plan, tilt);
			if (frames.Count == 0) return null;
			// The highest cut seen may still be scanning; every cut below it is done (so its frame has been built).
			var done = s.VolumeEnded ? int.MaxValue : s.HighestCutSeen - 1;
			foreach (var (lastCut, start, end) in frames)
			{
				if (lastCut <= done) continue;
				if (heldScan is { } held && s.VolumeStart.AddSeconds(start) <= held.AddSeconds(HeldSlack)) continue;
				return s.VolumeStart.AddSeconds(end + UploadSlack);
			}
			// This volume has nothing left for the tilt — the next volume's first frame.
			return period is { } p ? s.VolumeStart + p + TimeSpan.FromSeconds(frames[0].EndSeconds + UploadSlack) : null;
		}

		/// <summary>Seconds to wait before the next check: up to the expected landing, a quick retry while it's
		/// late, steady checks when there's no prediction or it's badly off.</summary>
		public static double NextWait(DateTimeOffset now, DateTimeOffset? expected)
		{
			if (expected is not { } at) return TailSeconds;
			var until = (at - now).TotalSeconds;
			if (until > 0) return Math.Clamp(until, MinWait, MaxWait);
			return -until < LateCap ? RetrySeconds : TailSeconds;
		}

		/// <summary>A volume start → the next one, accepted only when plausible (a NEXRAD volume runs ~3.5-11 min).</summary>
		public static TimeSpan? Period(DateTimeOffset previousStart, DateTimeOffset nextStart)
		{
			var p = nextStart - previousStart;
			return p > TimeSpan.FromMinutes(2) && p < TimeSpan.FromMinutes(12) ? p : null;
		}
	}
}
