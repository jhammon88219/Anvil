using System;
using System.Linq;
using Anvil.Models;
using Anvil.Services;

namespace Anvil.ViewModels
{
	/// <summary>
	/// Turns each measured PastCast load (<see cref="LoopHoldingGateViewModel.LoadMeasured"/>) into a
	/// <see cref="LoopLoadRecord"/> — the gate's timings plus what the radar was asked for (window, tilt, panes and their
	/// products, the machine's cores) — and appends it to <see cref="LoopLoadLog"/>. Built by MapViewModel, like
	/// SiteUsageTracker.
	/// </summary>
	public sealed class LoopLoadRecorder
	{
		/// <summary>Bump when a field's MEANING changes (see LoopLoadRecord).</summary>
		internal const int RecordVersion = 2; // 2: + the frame sources (cached / local raw / network)

		private readonly RadarViewModel _radar;
		private readonly LoopLoadLog _log;

		public LoopLoadRecorder(RadarViewModel radar, LoopLoadLog log)
		{
			_radar = radar;
			_log = log;
			radar.LoopGate.LoadMeasured += (_, timing) => _log.Append(Build(timing, DateTimeOffset.UtcNow));
		}

		internal LoopLoadRecord Build(LoopLoadTiming t, DateTimeOffset now)
		{
			var start = _radar.LoadedReplayStartUtc;
			var minutes = start is { } s && _radar.LoadedReplayEndUtc is { } e ? (int)Math.Round((e - s).TotalMinutes) : 0;
			var panes = Math.Max(1, _radar.VisiblePaneCount);
			var products = _radar.Panes.Take(panes).Select(p => p.ProductId).Distinct().ToArray();
			return new LoopLoadRecord(
				RecordVersion, now, t.SiteId, t.EventId, start, minutes,
				_radar.SelectedTiltLabel, panes, products, Environment.ProcessorCount,
				t.Outcome.ToString().ToLowerInvariant(),
				t.Frames, t.Downloaded, t.Built, t.Kept,
				t.TotalMs, t.FirstFrameMs, t.AllDownloadedMs, t.AllBuiltMs,
				t.Escaped, t.GateShown,
				t.CachedFrames, t.LocalRawFrames, t.NetworkFrames);
		}
	}
}
