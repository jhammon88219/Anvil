using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Models;
using Anvil.Services;

namespace Anvil.ViewModels
{
	/// <summary>How a replay window's frame list was chosen (the PastCast readout says which).</summary>
	internal enum ReplayFrameChoice
	{
		/// <summary>Every 0.5° pass of every volume — the whole window, full density.</summary>
		EveryPass,
		/// <summary>Every 0.5° pass, but the span was shortened around the event (more passes than the frame cap).</summary>
		TrimmedAroundEvent,
		/// <summary>One frame per volume — an overview window too long for every pass.</summary>
		VolumeStarts,
		/// <summary>One frame per volume, evenly thinned — an overview with more volumes than the cap.</summary>
		SampledVolumes,
	}

	/// <summary>
	/// Which frames a PastCast window loads. A SAILS / MESO-SAILS / MRLE volume scans 0.5° 2-4 times, and a warning
	/// forecaster loops EVERY low-level scan — never thinned (AWIPS / GR2Analyst / RadarScope cap a loop's frame COUNT and
	/// let its span shrink). So: every pass while the window fits the cap; an EVENT window (≤ <see cref="EventWindowMaxMinutes"/>)
	/// that doesn't keeps every pass and trims the span, centred on the event's key moment (or the window's middle); a
	/// longer OVERVIEW window shows one frame per volume as it always did, thinned evenly past the cap.
	/// </summary>
	/// <remarks>⚠️ The cap is MEMORY, not taste: a decoded frame is ~18 MB per product (radar-diag 2026-10-08, Enid 4-pane:
	/// 1,286 MB of geometry for 18 frames × 4 products, under the renderer's ~4,192 MB heap). Pure, so it is tested alone.</remarks>
	internal static class ReplayFramePlan
	{
		/// <summary>The longest window treated as one EVENT (every pass, span trimmed to fit); longer is an overview.</summary>
		internal const int EventWindowMaxMinutes = 120;

		/// <param name="eventWindowMaxMinutes">The longest event window; the Dev frame-cap experiment (DevFrameCap) passes
		/// int.MaxValue so every window keeps every pass.</param>
		internal static (List<string> Frames, ReplayFrameChoice Choice) Choose(IReadOnlyList<string> volumeKeys,
			IReadOnlyDictionary<string, int> passes, int windowMinutes, DateTimeOffset? focusUtc, int cap,
			int eventWindowMaxMinutes = EventWindowMaxMinutes)
		{
			var frames = RadarFrameKey.Expand(volumeKeys, passes);
			if (frames.Count <= cap)
			{
				return (frames, ReplayFrameChoice.EveryPass);
			}

			if (windowMinutes <= eventWindowMaxMinutes)
			{
				var times = EstimatedTimes(volumeKeys, passes);
				var focus = focusUtc ?? Midpoint(times);
				var nearest = 0;
				for (var i = 1; i < times.Count; i++)
				{
					if (Math.Abs((times[i] - focus).Ticks) < Math.Abs((times[nearest] - focus).Ticks)) nearest = i;
				}
				var start = Math.Clamp(nearest - cap / 2, 0, frames.Count - cap);
				return (frames.GetRange(start, cap), ReplayFrameChoice.TrimmedAroundEvent);
			}

			if (volumeKeys.Count <= cap)
			{
				return (volumeKeys.ToList(), ReplayFrameChoice.VolumeStarts);
			}
			// More volumes than the cap → evenly subsample across the whole window (first + last kept).
			var pick = new List<string>(cap);
			for (var i = 0; i < cap; i++)
			{
				pick.Add(volumeKeys[(int)Math.Round((double)i * (volumeKeys.Count - 1) / (cap - 1))]);
			}
			return (pick.Distinct().ToList(), ReplayFrameChoice.SampledVolumes);
		}

		// Each frame's approximate scan time, for centring only: a volume's passes spread evenly across the gap to the
		// next volume (the last volume borrows the previous gap). The real times arrive with the frames.
		private static List<DateTimeOffset> EstimatedTimes(IReadOnlyList<string> volumeKeys, IReadOnlyDictionary<string, int> passes)
		{
			var starts = volumeKeys.Select(k => Level2RadarService.ParseVolumeTime(k) ?? DateTimeOffset.MinValue).ToList();
			var times = new List<DateTimeOffset>();
			for (var v = 0; v < starts.Count; v++)
			{
				var gap = v + 1 < starts.Count ? starts[v + 1] - starts[v]
					: v > 0 ? starts[v] - starts[v - 1] : TimeSpan.FromMinutes(5);
				var n = passes.TryGetValue(volumeKeys[v], out var c) ? Math.Max(1, c) : 1;
				for (var p = 0; p < n; p++) times.Add(starts[v] + gap * p / n);
			}
			return times;
		}

		private static DateTimeOffset Midpoint(List<DateTimeOffset> times) =>
			times.Count == 0 ? DateTimeOffset.MinValue : times[0] + (times[^1] - times[0]) / 2;
	}
}
