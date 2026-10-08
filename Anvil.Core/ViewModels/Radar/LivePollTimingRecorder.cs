using System;
using System.Linq;
using Anvil.Models;
using Anvil.Services;

namespace Anvil.ViewModels
{
	/// <summary>
	/// Feeds the LIVE-POLL TIMING LOG (<see cref="LivePollTimingLog"/>) from the radar's live-poll stages
	/// (<see cref="RadarViewModel.LiveFrameActivity"/>): a <c>"frame"</c> record per new live frame — the model's predicted
	/// end, the bucket's real landing, our find, the page's paint — and a <c>"volume"</c> record per volume change (the
	/// length the planner used vs the real one). Built by MapViewModel, like LoopLoadRecorder. Pure bookkeeping: it reads,
	/// never steers the poll.
	/// </summary>
	public sealed class LivePollTimingRecorder
	{
		private readonly RadarViewModel _radar;
		private readonly LivePollTimingLog _log;
		private readonly Func<DateTimeOffset> _now;

		private int _polls;                    // polls ended since the previous frame was found
		private LiveScanSchedule? _lastSchedule; // the schedule at the previous poll's end (volume changes)
		private Pending? _pending;              // a found frame waiting for its paint

		private sealed record Pending(LivePollTimingRecord Record);

		public LivePollTimingRecorder(RadarViewModel radar, LivePollTimingLog log, Func<DateTimeOffset>? now = null)
		{
			_radar = radar;
			_log = log;
			_now = now ?? (() => DateTimeOffset.UtcNow);
			radar.LiveFrameActivity += (_, a) => OnStage(a);
		}

		private void OnStage(LiveFrameActivity a)
		{
			switch (a.Stage)
			{
				case LiveFrameStage.Unchanged or LiveFrameStage.Failed:
					_polls++;
					NoteVolume();
					break;
				case LiveFrameStage.Found:
					_polls++;
					NoteVolume();
					_pending = new Pending(FrameRecord(a));
					_polls = 0;
					break;
				case LiveFrameStage.Shown when _pending is { } p:
					var now = _now();
					var r = p.Record with { Drawn = a.Drawn };
					if (a.Drawn)
					{
						r = r with { PaintedUtc = now, ScreenLagSec = r.LandedUtc is { } l ? Round((now - l).TotalSeconds) : null };
					}
					_log.Append(r);
					_pending = null;
					break;
				case LiveFrameStage.Dropped:
					_pending = null;
					_lastSchedule = null;
					_polls = 0;
					break;
			}
		}

		// The found frame, matched in the schedule's plan by its scan time; landing from the bucket's LastModified.
		private LivePollTimingRecord FrameRecord(LiveFrameActivity a)
		{
			var now = _now();
			var s = _radar.LatestLiveSchedule is { } sched && sched.SiteId == a.SiteId ? sched : null;
			var tilt = _radar.WatchedTiltAngle is { } t ? (double?)t : null;
			int? lastCut = null;
			DateTimeOffset? predicted = null, landed = null;
			if (s is not null && a.VolumeTime is { } scan)
			{
				foreach (var (cut, start, end) in LivePollPlanner.FrameEnds(s.Plan, tilt))
				{
					if (Math.Abs((s.VolumeStart.AddSeconds(start) - scan).TotalSeconds) > 20) continue;
					lastCut = cut;
					predicted = s.VolumeStart.AddSeconds(end);
					if (s.CutLanded is { } cl && cl.TryGetValue(cut, out var l)) landed = l;
					break;
				}
			}
			return new LivePollTimingRecord("frame", now, a.SiteId, s?.Vcp ?? 0, tilt, Mode(),
				ScanUtc: a.VolumeTime, LastCut: lastCut, PredictedEndUtc: predicted, LandedUtc: landed, FoundUtc: now,
				ModelErrorSec: landed is { } l1 && predicted is { } p1 ? Round((l1 - p1).TotalSeconds) : null,
				PollLagSec: landed is { } l2 ? Round((now - l2).TotalSeconds) : null,
				Polls: _polls);
		}

		// A volume change since the previous poll: the length the planner used for the old volume (ITS previous volume's)
		// vs the real one (old start → new start).
		private void NoteVolume()
		{
			var s = _radar.LatestLiveSchedule;
			if (s is null) return;
			if (_lastSchedule is { } prev && prev.SiteId == s.SiteId && s.VolumeStart > prev.VolumeStart
				&& LivePollPlanner.Period(prev.VolumeStart, s.VolumeStart) is { } actual)
			{
				double? used = prev.PreviousVolumeStart is { } pp && LivePollPlanner.Period(pp, prev.VolumeStart) is { } u
					? u.TotalSeconds : null;
				_log.Append(new LivePollTimingRecord("volume", _now(), s.SiteId, prev.Vcp, null, Mode(),
					PreviousStartUtc: prev.VolumeStart, StartUtc: s.VolumeStart,
					PredictedLengthSec: used, ActualLengthSec: Round(actual.TotalSeconds)));
			}
			_lastSchedule = s;
		}

		private string Mode() => _radar.IsRegimeAwarePolling ? "regime" : "fixed";

		private static double Round(double v) => Math.Round(v, 1);
	}
}
