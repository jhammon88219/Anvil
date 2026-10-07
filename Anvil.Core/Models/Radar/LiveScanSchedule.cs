using System;
using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>One cut of a VCP's PLANNED scan (Message 5, read by <c>ScanPlan</c>), in table order.</summary>
	/// <param name="Number">1-based — the elevation number the cut's radials carry.</param>
	/// <param name="AzimuthRate">Antenna speed, |°/s|.</param>
	public sealed record PlannedCut(int Number, double Angle, int Waveform, double AzimuthRate, bool IsSails, int SailsSequence, bool IsMrle)
	{
		/// <summary>One full rotation at the planned speed, seconds (0 when the rate is unreadable).</summary>
		public double SweepSeconds => AzimuthRate > 0 ? 360.0 / AzimuthRate : 0;
	}

	/// <summary>
	/// What the NEWEST live volume has done so far, from the last poll — the input to regime-aware polling
	/// (<c>LivePollPlanner</c>). <paramref name="HighestCutSeen"/> = the highest elevation number among its chunks (that
	/// cut may still be scanning); <paramref name="VolumeEnded"/> = its end chunk ('E') has arrived;
	/// <paramref name="PreviousVolumeStart"/> = the volume before it (its folder's newest start), so the volume LENGTH is
	/// known from the first poll — without it, a site's first volume had no next-volume prediction (KTLX clear air,
	/// 2026-10-07: ~6 min of blind 15 s checks).
	/// </summary>
	/// <param name="CutLanded">Per elevation number, when that cut's LAST chunk landed in the bucket (S3 LastModified) —
	/// the live-poll timing log's ground truth (LivePollTimingLog).</param>
	public sealed record LiveScanSchedule(string SiteId, DateTimeOffset VolumeStart, int Vcp, IReadOnlyList<PlannedCut> Plan,
		int HighestCutSeen, bool VolumeEnded, DateTimeOffset? PreviousVolumeStart = null,
		IReadOnlyDictionary<int, DateTimeOffset>? CutLanded = null);
}
