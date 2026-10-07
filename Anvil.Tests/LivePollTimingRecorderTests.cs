using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Anvil.Tests.TemporalWindowPersistenceTests;

namespace Anvil.Tests
{
	/// <summary>
	/// The live-poll timing log (<see cref="LivePollTimingRecorder"/> → <see cref="LivePollTimingLog"/>): a new frame is
	/// logged with the model's predicted end, the bucket's landing, our find and the page's paint; a volume change with
	/// the length the planner used vs the real one. Real KTBW plan (LivePollPlannerTests), real radar announcement path.
	/// </summary>
	public class LivePollTimingRecorderTests
	{
		private static readonly DateTimeOffset Start = LivePollPlannerTests.Start;
		private static readonly RadarSite Ktbw = new("KTBW", "Tampa", 27.7, -82.4);

		private sealed class Rig : IDisposable
		{
			public readonly string Dir = Path.Combine(Path.GetTempPath(), "AnvilPollLog", Guid.NewGuid().ToString("N"));
			public RadarViewModel Radar = null!;
			public LiveScanSchedule? Schedule;
			public DateTimeOffset Now = Start;
			public string File => Path.Combine(Dir, "live-poll-timing.jsonl");
			public List<JsonElement> Lines() => System.IO.File.Exists(File)
				? System.IO.File.ReadAllLines(File).Select(l => JsonDocument.Parse(l).RootElement).ToList()
				: new List<JsonElement>();
			public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
		}

		private static async Task<Rig> NewRig()
		{
			var rig = new Rig();
			var settings = new AppSettings();
			var svc = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			var archive = Null<ILevel2RadarService>.Create(new() { ["get_LiveSchedule"] = _ => rig.Schedule });
			rig.Radar = new RadarViewModel(Null<IMapService>.Create(), Null<IRadarSiteProvider>.Create(), archive,
				Null<IDowEventProvider>.Create(), svc, null);
			await rig.Radar.OnMapsReadyAsync();
			_ = new LivePollTimingRecorder(rig.Radar, new LivePollTimingLog(NullLogger<LivePollTimingLog>.Instance, rig.Dir), () => rig.Now);
			return rig;
		}

		private static LiveScanSchedule Schedule(DateTimeOffset start, int highest, DateTimeOffset? previous, Dictionary<int, DateTimeOffset>? landed = null) =>
			new("KTBW", start, 212, LivePollPlannerTests.Plan(), highest, false, previous, landed);

		[Fact]
		public async Task A_new_frame_is_logged_with_its_predicted_end_landing_find_and_paint()
		{
			using var rig = await NewRig();
			var end = LivePollPlanner.FrameEnds(LivePollPlannerTests.Plan(), null)[0].EndSeconds;  // the base pair
			rig.Schedule = Schedule(Start, 3, Start.AddSeconds(-304), new() { [2] = Start.AddSeconds(38) });

			rig.Radar.BeginLiveCheck("KTBW");                      // one poll with nothing new…
			rig.Radar.EndLiveCheck(failed: false);
			rig.Radar.BeginLiveCheck("KTBW");                      // …then the one that finds it
			var live = new RadarVolume("https://radarlevel2/live.V06", Ktbw, Start.AddSeconds(0.8), "VCP 212", new[] { 0.5f });
			rig.Now = Start.AddSeconds(40);
			rig.Radar.AnnounceLiveFound(live);
			rig.Radar.AnnounceLiveBuilt(live, onScreen: true);      // decoded; waiting for the draw
			Assert.Empty(rig.Lines());                             // nothing logged before the paint
			rig.Now = Start.AddSeconds(43);
			rig.Radar.OnRadarPainted(0);

			var r = Assert.Single(rig.Lines());
			Assert.Equal("frame", r.GetProperty("kind").GetString());
			Assert.Equal("regime", r.GetProperty("mode").GetString());
			Assert.Equal(212, r.GetProperty("vcp").GetInt32());
			Assert.Equal(2, r.GetProperty("lastCut").GetInt32());
			Assert.Equal(Math.Round(38 - end, 1), r.GetProperty("modelErrorSec").GetDouble());   // landed − predicted end
			Assert.Equal(2, r.GetProperty("pollLagSec").GetDouble());                            // found − landed
			Assert.Equal(5, r.GetProperty("screenLagSec").GetDouble());                          // painted − landed
			Assert.Equal(2, r.GetProperty("polls").GetInt32());
		}

		[Fact]
		public async Task A_volume_change_is_logged_with_the_length_used_and_the_real_one()
		{
			using var rig = await NewRig();
			rig.Schedule = Schedule(Start, 15, Start.AddSeconds(-304));
			rig.Radar.BeginLiveCheck("KTBW");
			rig.Radar.EndLiveCheck(false);
			rig.Schedule = Schedule(Start.AddSeconds(290), 1, Start);  // AVSET: this one ran 290 s, not 304
			rig.Radar.BeginLiveCheck("KTBW");
			rig.Radar.EndLiveCheck(false);

			var r = Assert.Single(rig.Lines());
			Assert.Equal("volume", r.GetProperty("kind").GetString());
			Assert.Equal(304, r.GetProperty("predictedLengthSec").GetDouble());
			Assert.Equal(290, r.GetProperty("actualLengthSec").GetDouble());
		}
	}
}
