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
	/// cut may still be scanning); <paramref name="VolumeEnded"/> = its end chunk ('E') has arrived.
	/// </summary>
	public sealed record LiveScanSchedule(string SiteId, DateTimeOffset VolumeStart, int Vcp, IReadOnlyList<PlannedCut> Plan,
		int HighestCutSeen, bool VolumeEnded);
}
