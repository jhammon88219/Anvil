using System;
using System.Collections.Generic;

namespace Anvil.ViewModels
{
	/// <summary>
	/// NowCast's archive fold-in, the HELD-FRAME rule: which of the scans the chunks bucket already gave us (held live
	/// frames, and a live slot the fold-in retires) ARE which newly-arrived archive frames — so those frames reuse the
	/// picture already built instead of downloading and decoding the same scan again (the SAILS "cells rebuild between
	/// polls" bug, 2026-10-09). Matched by REAL scan time: both sides time a scan by its first radial, so the same scan
	/// agrees to the second, while 0.5° passes are ≥ ~76 s apart. A frame with no time, or no candidate in reach,
	/// matches nothing and decodes as before.
	/// </summary>
	internal static class HeldFrameMatch
	{
		/// <summary>Whether a chunks scan (the live slot, a held frame) is BEYOND what the archive now holds — carried after
		/// it — rather than covered by it. Against the archive newest's real time when known (<paramref name="isNewer"/> =
		/// the engine's LiveIsNewer). ⚠️ When it is UNKNOWN (a rescan whose fetch failed, or a planned pass the volume lacks)
		/// it could be any time up to the next volume's start, and its volume-start stamp is NOT its time — using that
		/// carried scans the archive also had: the same scan twice, time running backwards (KTLH 2026-10-10 00:50). Then
		/// only a scan from a LATER volume (<paramref name="liveVolumeStarts"/>, the live schedule's) counts as beyond.</summary>
		public static Func<DateTimeOffset, bool> BeyondArchive(DateTimeOffset? newestKnown, DateTimeOffset? newestVolumeStart,
			IEnumerable<DateTimeOffset?> liveVolumeStarts, TimeSpan slack, Func<DateTimeOffset, DateTimeOffset, bool> isNewer)
		{
			if (newestKnown is { } known) return t => isNewer(t, known);
			DateTimeOffset? later = null;
			if (newestVolumeStart is { } start)
			{
				foreach (var s in liveVolumeStarts)
				{
					if (s is { } v && v > start + slack && (later is null || v < later)) later = v;
				}
			}
			return t => later is { } l && t >= l - slack;
		}

		/// <summary>Pairs each new archive frame (index, real scan time if known) with at most one candidate (old index,
		/// scan time) within <paramref name="slack"/>, nearest first; each candidate is used once. Returns (old, new).</summary>
		public static List<(int Old, int New)> Match(
			IReadOnlyList<(int Index, DateTimeOffset Time)> candidates,
			IReadOnlyList<(int Index, DateTimeOffset? Time)> arrivals,
			TimeSpan slack)
		{
			var pairs = new List<(int Old, int New)>();
			var used = new bool[candidates.Count];
			foreach (var (newIndex, time) in arrivals)
			{
				if (time is not { } t) continue;
				var best = -1;
				var bestGap = TimeSpan.MaxValue;
				for (var c = 0; c < candidates.Count; c++)
				{
					var gap = (candidates[c].Time - t).Duration();
					if (!used[c] && gap <= slack && gap < bestGap)
					{
						best = c;
						bestGap = gap;
					}
				}
				if (best < 0) continue;
				used[best] = true;
				pairs.Add((candidates[best].Index, newIndex));
			}
			return pairs;
		}
	}
}
