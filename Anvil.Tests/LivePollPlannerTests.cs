using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;
using static Anvil.Tests.TemporalWindowPersistenceTests;

namespace Anvil.Tests
{
	/// <summary>
	/// Regime-aware polling (<see cref="LivePollPlanner"/>, docs/regime-aware-polling.md): the next frame of the watched
	/// tilt is predicted from the live volume's own plan. The plan here is REAL — KTBW's VCP 212 volume of 2026-10-07
	/// 19:46:42Z as TiltCheck --regime read it — and the expected ends are what that volume's radials actually did.
	/// </summary>
	public class LivePollPlannerTests
	{
		// KTBW folder 175: (angle, waveform, azimuth rate °/s, SAILS?) per cut, in table order.
		private static readonly (double a, int w, double r, bool sails)[] Ktbw212 =
		{
			(0.48, 1, 21.15, false), (0.48, 2, 18.45, false), (0.88, 1, 21.15, false), (0.88, 2, 18.45, false),
			(1.27, 1, 23.03, false), (1.27, 2, 18.45, false), (1.80, 4, 26.39, false), (2.42, 4, 27.33, false),
			(3.08, 4, 28.22, false), (0.48, 1, 21.15, true), (0.48, 2, 18.45, true), (4.00, 4, 26.40, false),
			(5.10, 4, 26.40, false), (6.42, 4, 26.40, false), (8.00, 3, 28.42, false), (10.02, 3, 28.41, false),
			(12.48, 3, 28.74, false), (15.60, 3, 28.74, false), (19.51, 3, 28.74, false),
		};

		private static List<PlannedCut> Plan() =>
			Ktbw212.Select((c, i) => new PlannedCut(i + 1, c.a, c.w, c.r, c.sails, c.sails ? 1 : 0, false)).ToList();

		private static readonly DateTimeOffset Start = new(2026, 10, 7, 19, 46, 42, TimeSpan.Zero);

		private static LiveScanSchedule At(int highestCut, bool ended = false) =>
			new("KTBW", Start, 212, Plan(), highestCut, ended);

		[Fact]
		public void FrameEnds_MatchWhatTheRealVolumeDid()
		{
			// Measured (radial times): base pair ends +36 s, the SAILS pair +187 s; 0.9° pair +73 s.
			var base05 = LivePollPlanner.FrameEnds(Plan(), null);
			Assert.Equal(new[] { 2, 11 }, base05.Select(f => f.LastCut));
			Assert.InRange(base05[0].EndSeconds, 36 - 2, 36 + 2);
			Assert.InRange(base05[1].EndSeconds, 187 - 2.5, 187 + 2.5);
			var tilt09 = Assert.Single(LivePollPlanner.FrameEnds(Plan(), 0.88));
			Assert.InRange(tilt09.EndSeconds, 73 - 2, 73 + 2);
		}

		[Fact]
		public void ExpectedNext_IsTheFirstFrameNotYetDone_PlusTheUploadSlack()
		{
			var first = LivePollPlanner.FrameEnds(Plan(), null);
			// Scanning cut 1 → the base pair is next.
			Assert.Equal(Start.AddSeconds(first[0].EndSeconds + LivePollPlanner.UploadSlack), LivePollPlanner.ExpectedNext(At(1), null, null));
			// Scanning cut 5 (the base pair is done) → the SAILS rescan.
			Assert.Equal(Start.AddSeconds(first[1].EndSeconds + LivePollPlanner.UploadSlack), LivePollPlanner.ExpectedNext(At(5), null, null));
		}

		[Fact]
		public void ExpectedNext_SkipsTheScanAlreadyHeld()
		{
			// The chunks end exactly at the base pair (highest cut 2 = it may still be "scanning"), but we hold its scan.
			Assert.Equal(Start.AddSeconds(LivePollPlanner.FrameEnds(Plan(), null)[1].EndSeconds + LivePollPlanner.UploadSlack),
				LivePollPlanner.ExpectedNext(At(2), null, null, heldScan: Start.AddSeconds(0.8)));
		}

		[Fact]
		public void After_the_last_frame_the_next_volume_needs_a_learned_period()
		{
			var after = At(15);                                              // past the SAILS pair: nothing left at 0.5°
			Assert.Null(LivePollPlanner.ExpectedNext(after, null, null));    // no period yet → steady checks
			var period = TimeSpan.FromSeconds(304);                          // KTBW 19:41:38 → 19:46:42
			Assert.Equal(Start + period + TimeSpan.FromSeconds(LivePollPlanner.FrameEnds(Plan(), null)[0].EndSeconds + LivePollPlanner.UploadSlack),
				LivePollPlanner.ExpectedNext(after, null, period));
			Assert.Null(LivePollPlanner.Period(Start, Start.AddSeconds(30)));    // implausible volume lengths are refused
			Assert.Equal(period, LivePollPlanner.Period(Start - period, Start));
		}

		[Fact]
		public void NextWait_WaitsForTheLanding_RetriesWhileLate_AndFallsBackWhenOff()
		{
			var now = Start;
			Assert.Equal(40, LivePollPlanner.NextWait(now, now.AddSeconds(40)));
			Assert.Equal(LivePollPlanner.MaxWait, LivePollPlanner.NextWait(now, now.AddMinutes(9)));
			Assert.Equal(LivePollPlanner.MinWait, LivePollPlanner.NextWait(now, now.AddSeconds(0.5)));
			Assert.Equal(LivePollPlanner.RetrySeconds, LivePollPlanner.NextWait(now, now.AddSeconds(-10)));
			Assert.Equal(LivePollPlanner.TailSeconds, LivePollPlanner.NextWait(now, now.AddSeconds(-90)));
			Assert.Equal(LivePollPlanner.TailSeconds, LivePollPlanner.NextWait(now, null));
		}

		// ── The radar view model's side: the setting, the site check, learning the period ──

		private static (RadarViewModel radar, AppSettings settings, Func<LiveScanSchedule?> get, Action<LiveScanSchedule?> set) Radar()
		{
			LiveScanSchedule? current = null;
			var settings = new AppSettings();
			var svc = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			var archive = Null<ILevel2RadarService>.Create(new() { ["get_LiveSchedule"] = _ => current });
			var radar = new RadarViewModel(Null<IMapService>.Create(), Null<IRadarSiteProvider>.Create(), archive,
				Null<IDowEventProvider>.Create(), svc, null);
			return (radar, settings, () => current, s => current = s);
		}

		[Fact]
		public void PlanNextLiveCheck_FollowsTheSetting_AndTheSite()
		{
			var (radar, settings, _, set) = Radar();
			set(At(1));
			var wait = radar.PlanNextLiveCheck("KTBW", Start);
			Assert.InRange(wait!.Value, 36, 42);                             // ~ the base pair's landing
			Assert.NotNull(radar.ExpectedNextScanAt);

			Assert.Null(radar.PlanNextLiveCheck("KTLX", Start));             // another site's schedule is not ours

			settings.RegimeAwarePolling = false;
			Assert.Null(radar.PlanNextLiveCheck("KTBW", Start));             // fixed interval
			Assert.Null(radar.ExpectedNextScanAt);
		}

		[Fact]
		public void PlanNextLiveCheck_KnowsTheVolumeLength_FromTheFirstPoll_ViaThePreviousVolume()
		{
			// KTLX clear air, 2026-10-07: the first volume's only frame was in; with no length known it polled blind every
			// 15 s for ~6 min. The previous volume's start (in the bucket) gives the length on the first poll.
			var (radar, _, _, set) = Radar();
			set(At(15) with { PreviousVolumeStart = Start.AddSeconds(-304) });
			var wait = radar.PlanNextLiveCheck("KTBW", Start.AddSeconds(240));
			Assert.Equal(Start.AddSeconds(304 + LivePollPlanner.FrameEnds(Plan(), null)[0].EndSeconds + LivePollPlanner.UploadSlack),
				radar.ExpectedNextScanAt);
			Assert.NotEqual(LivePollPlanner.TailSeconds, wait);
		}

		[Fact]
		public void PlanNextLiveCheck_LearnsTheVolumePeriod_AtAVolumeChange()
		{
			var (radar, _, _, set) = Radar();
			set(At(15));
			Assert.Equal(LivePollPlanner.TailSeconds, radar.PlanNextLiveCheck("KTBW", Start)); // nothing left, no period
			var next = new LiveScanSchedule("KTBW", Start.AddSeconds(304), 212, Plan(), 15, false);
			set(next);
			radar.PlanNextLiveCheck("KTBW", next.VolumeStart);
			// The NEXT volume's base pair is now predicted from the learned 304 s.
			Assert.Equal(next.VolumeStart.AddSeconds(304 + LivePollPlanner.FrameEnds(Plan(), null)[0].EndSeconds + LivePollPlanner.UploadSlack),
				radar.ExpectedNextScanAt);
		}
	}
}
