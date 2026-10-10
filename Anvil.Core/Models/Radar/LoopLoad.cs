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
	/// <param name="Escaped">The user took "Use the map" while it loaded. Always false since that escape was removed
	/// (2026-10-10); older log lines can be true. Kept: append-only.</param>
	/// <param name="GateShown">The map was gated. Always true since the opt-out was removed (2026-10-10); older log
	/// lines can be false. Kept: the log's fields are append-only.</param>
	/// <param name="CachedFrames">Frames whose tilt file was already cached (no download, no extract).</param>
	/// <param name="LocalRawFrames">Frames extracted from a whole volume already on disk (no download).</param>
	/// <param name="NetworkFrames">Frames downloaded during this load. The three sum to the frames that LANDED.</param>
	public sealed record LoopLoadTiming(
		string SiteId, string? EventId, LoopLoadOutcome Outcome,
		int Frames, int Downloaded, int Built, int Kept,
		long TotalMs, long? FirstFrameMs, long? AllDownloadedMs, long? AllBuiltMs,
		bool Escaped, bool GateShown,
		int CachedFrames = 0, int LocalRawFrames = 0, int NetworkFrames = 0)
	{
		/// <summary>Bytes downloaded for the landed frames.</summary>
		public long FrameBytes { get; init; }
		/// <summary>Every Level II byte the process downloaded during the load (frames + the background raw prefetch).</summary>
		public long AllBytes { get; init; }
		/// <summary>The window this load asked for (UTC start, length) — null/0 when the engine didn't say.</summary>
		public DateTimeOffset? WindowStartUtc { get; init; }
		public int WindowMinutes { get; init; }
		/// <summary>Per landed frame: [index, source (0 network · 1 cached tilt · 2 local raw), bytes, fetch ms, landed ms].</summary>
		public long[][] FrameDetail { get; init; } = Array.Empty<long[]>();
		/// <summary>The progress curve: [ms, downloaded, built, bytes] at every count change.</summary>
		public long[][] Progress { get; init; } = Array.Empty<long[]>();
	}

	/// <summary>
	/// One line of the load-time log (<c>%LocalAppData%\Anvil\Usage\loop-load-times.jsonl</c>, <see
	/// cref="Anvil.Services.LoopLoadLog"/>): the gate's timings plus what was asked for, so loads can be averaged and a
	/// "ready in about X" estimate fitted to them (the user's call, 2026-10-02).
	/// </summary>
	/// <remarks>
	/// ⚠️ APPEND-ONLY and read by future code: ADD fields, never rename or re-type one. <see cref="Version"/> bumps when a
	/// field's MEANING changes. Version 2 (2026-10-02) added the frame SOURCES — <see cref="CachedFrames"/>,
	/// <see cref="LocalRawFrames"/>, <see cref="NetworkFrames"/> (from <c>RadarVolume.Source</c>); a v1 line reads them as 0,
	/// which means UNKNOWN there, not "none cached". Version 3 (same day) added the NETWORK vs COMPUTER split: bytes
	/// (<see cref="FrameBytes"/>, <see cref="AllBytes"/>), the Dev speed cap in effect (<see cref="DevMbps"/>, 0 = none),
	/// the seeding run's tag (<see cref="Run"/>), per-frame <see cref="FrameDetail"/> and the <see cref="Progress"/> curve.
	/// ⚠️ DEBUG-ONLY: the log is registered only in Debug builds — it designs the estimate, it doesn't ship.
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
		bool GateShown,
		int CachedFrames = 0,
		int LocalRawFrames = 0,
		int NetworkFrames = 0,
		long FrameBytes = 0,
		long AllBytes = 0,
		double DevMbps = 0,
		string? Run = null,
		long[][]? FrameDetail = null,
		long[][]? Progress = null);
}
