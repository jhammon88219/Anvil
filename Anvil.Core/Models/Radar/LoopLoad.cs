using System;
using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>How a PastCast load ended (the load-time log's <c>outcome</c>).</summary>
	public enum LoopLoadOutcome
	{
		/// <summary>Every frame built.</summary>
		Finished,
		/// <summary>The user pressed Cancel load (<see cref="LoopLoadTiming.Kept"/> frames stayed).</summary>
		Cancelled,
		/// <summary>Failed, superseded by another load, or left mid-load (site change, Clear, PastCast off).</summary>
		Abandoned,
	}

	/// <summary>
	/// What the loop holding gate measured for one load (<c>LoopHoldingGateViewModel.LoadMeasured</c>). Times are ms since
	/// the load began (Begin: before the archive listing); null = that line was never crossed.
	/// </summary>
	/// <param name="Escaped">The user took "Use the map" while it loaded (the map then competes for the CPU).</param>
	/// <param name="GateShown">Holding was on (AppSettings.HoldPastCastLoads), so the map was gated.</param>
	public sealed record LoopLoadTiming(
		string SiteId, string? EventId, LoopLoadOutcome Outcome,
		int Frames, int Downloaded, int Built, int Kept,
		long TotalMs, long? FirstFrameMs, long? AllDownloadedMs, long? AllBuiltMs,
		bool Escaped, bool GateShown);

	/// <summary>
	/// One line of the load-time log (<c>%LocalAppData%\Anvil\Usage\loop-load-times.jsonl</c>, <see
	/// cref="Anvil.Services.LoopLoadLog"/>): the gate's timings plus what was asked for, so loads can be averaged and a
	/// "ready in about X" estimate fitted to them (the user's call, 2026-10-02).
	/// </summary>
	/// <remarks>
	/// ⚠️ APPEND-ONLY and read by future code: ADD fields, never rename or re-type one. <see cref="Version"/> bumps when a
	/// field's MEANING changes. Not yet recorded (a separate step): how many frames were already on disk — the biggest
	/// predictor; the fetch path knows it but doesn't report it.
	/// </remarks>
	public sealed record LoopLoadRecord(
		int Version,
		DateTimeOffset AtUtc,
		string Site,
		string? EventId,
		DateTimeOffset? WindowStartUtc,
		int WindowMinutes,
		string Tilt,
		int Panes,
		IReadOnlyList<string> Products,
		int LogicalCores,
		string Outcome,
		int Frames,
		int Downloaded,
		int Built,
		int Kept,
		long TotalMs,
		long? FirstFrameMs,
		long? AllDownloadedMs,
		long? AllBuiltMs,
		bool Escaped,
		bool GateShown);
}
