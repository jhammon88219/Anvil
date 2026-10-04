using System;
using System.Linq;
using Anvil.Models;
using Anvil.Services;

namespace Anvil.ViewModels
{
	/// <summary>
	/// Turns each measured PastCast load (<see cref="LoopHoldingGateViewModel.LoadMeasured"/>) into a
	/// <see cref="LoopLoadRecord"/> — the gate's timings plus what the radar was asked for (window, tilt, panes and their
	/// products, the machine's cores, the Dev speed cap) — and appends it to <see cref="LoopLoadLog"/>. Built by
	/// MapViewModel, like SiteUsageTracker.
	/// </summary>
	/// <remarks>⚠️ DEBUG-ONLY in practice: App registers <see cref="LoopLoadLog"/> only in Debug, and MapViewModel builds
	/// this only when it gets one. The log designs the "ready in" estimate; it isn't a shipped feature.</remarks>
	public sealed class LoopLoadRecorder
	{
		/// <summary>Bump when a field's MEANING changes (see LoopLoadRecord).</summary>
		internal const int RecordVersion = 3; // 2: + frame sources · 3: + bytes, Dev cap, run tag, frame detail, progress

		private readonly RadarViewModel _radar;
		private readonly LoopLoadLog _log;

		public LoopLoadRecorder(RadarViewModel radar, LoopLoadLog log)
		{
			_radar = radar;
			_log = log;
			radar.LoopGate.LoadMeasured += (_, timing) => _log.Append(Build(timing, DateTimeOffset.UtcNow));
		}

		/// <summary>A label stamped on every record while set — the Dev seeding run's ("seed-…|cold|120m"). Null = a
		/// load you made yourself.</summary>
		public string? RunTag { get; set; }

		internal LoopLoadRecord Build(LoopLoadTiming t, DateTimeOffset now)
		{
			// The window the load ASKED for (see LoopHoldingGateViewModel._windowStartUtc); the loaded window only as a fallback.
			var start = t.WindowStartUtc ?? _radar.LoadedReplayStartUtc;
			var minutes = t.WindowStartUtc is not null ? t.WindowMinutes
				: start is { } s && _radar.LoadedReplayEndUtc is { } e ? (int)Math.Round((e - s).TotalMinutes) : 0;
			var panes = Math.Max(1, _radar.VisiblePaneCount);
			var products = _radar.Panes.Take(panes).Select(p => p.ProductId).Distinct().ToArray();
			return new LoopLoadRecord(
				RecordVersion, now, t.SiteId, t.EventId, start, minutes,
				_radar.SelectedTiltLabel, panes, products, Environment.ProcessorCount,
				t.Outcome.ToString().ToLowerInvariant(),
				t.Frames, t.Downloaded, t.Built, t.Kept,
				t.TotalMs, t.FirstFrameMs, t.AllDownloadedMs, t.AllBuiltMs,
				t.Escaped, t.GateShown,
				t.CachedFrames, t.LocalRawFrames, t.NetworkFrames,
				t.FrameBytes, t.AllBytes, DevBandwidthLimit.Mbps, RunTag,
				t.FrameDetail, t.Progress);
		}
	}
}
