using System;

namespace Anvil.Models
{
	/// <summary>
	/// One line of the LIVE-POLL TIMING LOG (<c>Usage\live-poll-timing.jsonl</c>, kept for good) — the permanent proof that
	/// regime-aware polling's estimates hold (docs/regime-aware-polling.md). Two kinds:
	/// <list type="bullet">
	/// <item><c>"frame"</c> — one new live frame: the MODEL's predicted end of its last cut, when that cut's chunk really
	/// LANDED in the bucket (S3 LastModified), when our poll FOUND it and when the page PAINTED it. Logged in BOTH modes,
	/// so fixed-time polling is the baseline regime-aware is judged against.</item>
	/// <item><c>"volume"</c> — a volume change seen: the length the planner was using (the previous volume's) vs the real one
	/// (AVSET varies it).</item>
	/// </list>
	/// ⚠️ APPEND-ONLY SHAPE: add fields, never rename or repurpose one — old lines must keep reading. Read it with
	/// <c>py -3 tools/live_poll_report.py</c>.
	/// </summary>
	/// <param name="ModelErrorSec">Landed − predicted end: the model + upload. Measured offline ~+1-3 s.</param>
	/// <param name="PollLagSec">Found − landed: what the poll's timing costs (regime-aware aims at ~3 s; fixed 30 s ≈ 15 avg).</param>
	/// <param name="ScreenLagSec">Painted − landed: what the user waits, end to end. Null when the frame wasn't drawn (scrubbed back).</param>
	/// <param name="Polls">Checks spent since the previous frame (including the one that found it).</param>
	/// <param name="Drawn">Frame lines from 2026-10-08 on: false = in the loop but never drawn (scrubbed back, or a
	/// minimized/hidden window that drew nothing within the paint timeout) — no paint time, no screen lag.</param>
	public sealed record LivePollTimingRecord(
		string Kind,
		DateTimeOffset At,
		string Site,
		int Vcp,
		double? Tilt,
		string Mode,
		DateTimeOffset? ScanUtc = null,
		int? LastCut = null,
		DateTimeOffset? PredictedEndUtc = null,
		DateTimeOffset? LandedUtc = null,
		DateTimeOffset? FoundUtc = null,
		DateTimeOffset? PaintedUtc = null,
		double? ModelErrorSec = null,
		double? PollLagSec = null,
		double? ScreenLagSec = null,
		int? Polls = null,
		DateTimeOffset? PreviousStartUtc = null,
		DateTimeOffset? StartUtc = null,
		double? PredictedLengthSec = null,
		double? ActualLengthSec = null,
		int Version = 1,
		bool? Drawn = null);
}
