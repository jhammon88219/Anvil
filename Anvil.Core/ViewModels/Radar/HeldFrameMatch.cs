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
