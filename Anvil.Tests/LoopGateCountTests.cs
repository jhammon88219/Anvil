using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;
using static Anvil.Tests.TemporalWindowPersistenceTests;

namespace Anvil.Tests
{
	/// <summary>
	/// The loop holding gate's "Frames built" count during a real replay load (fake archive, no map): a cell counts
	/// only once its frame is FILLED. The first-paint frame's cell lights on reflectivity alone (Rule 1), so counting
	/// lit cells read "10 of 10" for 8 s on a 2024 replay while the gate still waited on that frame's velocity.
	/// </summary>
	public class LoopGateCountTests
	{
		private static readonly RadarSite Ktlx = new("KTLX", "Norman", 35.333, -97.278);

		private static async Task<RadarViewModel> LoadedThreeFrames(Action<LoopHoldingGateViewModel>? beforeLoad = null)
		{
			var settings = new AppSettings { HoldPastCastLoads = true };
			var svc = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			var keys = new[] { "2024/05/07/KTLX/KTLX20240507_033006_V06", "2024/05/07/KTLX/KTLX20240507_033512_V06",
				"2024/05/07/KTLX/KTLX20240507_034018_V06" };
			var archive = Null<ILevel2RadarService>.Create(new()
			{
				["GetKeysForWindowAsync"] = _ => Task.FromResult<IReadOnlyList<string>>(keys),
				["EnsureCachedAsync"] = a => Task.FromResult<RadarVolume?>(new RadarVolume("https://radarlevel2/x.V06", Ktlx,
					Level2RadarService.ParseVolumeTime((string)a![1]!)!.Value)),
			});
			var radar = new RadarViewModel(Null<IMapService>.Create(), Null<IRadarSiteProvider>.Create(), archive,
				Null<IDowEventProvider>.Create(), svc, null);
			radar.IsPastEventMode = true;
			radar.SelectedRadarOption = new RadarOption("KTLX", Ktlx);
			radar.PastEventDate = new DateTimeOffset(2024, 5, 6, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2024, 5, 6)));
			radar.PastEventTime = new TimeSpan(22, 30, 0);
			radar.PastEventDurationIndex = 0; // 30 min
			beforeLoad?.Invoke(radar.LoopGate);
			Assert.True(await radar.LoadSelectedPastEventAsync());
			return radar;
		}

		[Fact]
		public async Task The_first_paint_frame_counts_as_built_only_once_its_velocity_lands()
		{
			var radar = await LoadedThreeFrames();
			Assert.True(radar.LoopGate.IsShown);
			Assert.Equal(3, radar.LoopGate.Total);

			for (var i = 0; i < 3; i++) radar.OnRadarFrameReady(i, hasData: true);
			// The page: frames 1 and 2 have the duo; frame 0 (first paint) has reflectivity only.
			radar.SetBuildProgress(2, 3, new[] { true, true, true }, new[] { false, true, true });
			Assert.True(radar.Segments[0].IsReady);       // its CELL is lit (Rule 1) …
			Assert.Equal(2, radar.LoopGate.Built);        // … but it doesn't count as built
			Assert.True(radar.LoopGate.IsShown);          // and the gate still holds

			radar.SetBuildProgress(3, 3, new[] { true, true, true }, new[] { true, true, true });
			Assert.Equal(3, radar.LoopGate.Built);
			Assert.False(radar.LoopGate.IsLoading);       // released
		}

		// The load-time log's window is the one the load ASKED for. The loaded-window readouts are set only after the load
		// returns, and a load whose frames settle as its backfill ends measured before that — logging the previous load's
		// window (Rainsville 3 h seeding load logged as 2 h, 2026-10-02).
		[Fact]
		public async Task A_load_measures_the_window_it_asked_for()
		{
			var measured = new List<LoopLoadTiming>();
			var radar = await LoadedThreeFrames(gate => gate.LoadMeasured += (_, t) => measured.Add(t));
			for (var i = 0; i < 3; i++) radar.OnRadarFrameReady(i, hasData: true);
			radar.SetBuildProgress(3, 3, new[] { true, true, true }, new[] { true, true, true });

			var timing = Assert.Single(measured);
			Assert.Equal(30, timing.WindowMinutes);
			Assert.Equal(radar.LoadedReplayStartUtc, timing.WindowStartUtc);
			Assert.NotNull(timing.WindowStartUtc);
		}

		[Fact]
		public void The_record_takes_the_measured_window_over_the_loaded_one()
		{
			var settings = new AppSettings();
			var svc = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			var radar = new RadarViewModel(Null<IMapService>.Create(), Null<IRadarSiteProvider>.Create(),
				Null<ILevel2RadarService>.Create(), Null<IDowEventProvider>.Create(), svc, null); // nothing loaded yet
			var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"anvil-loadlog-{Guid.NewGuid():N}");
			var recorder = new LoopLoadRecorder(radar, new LoopLoadLog(Microsoft.Extensions.Logging.Abstractions.NullLogger<LoopLoadLog>.Instance, dir));
			var start = new DateTimeOffset(2011, 4, 27, 22, 45, 0, TimeSpan.Zero);

			var record = recorder.Build(new LoopLoadTiming("KFFC", "rainsville-2011", LoopLoadOutcome.Finished, 39, 38, 35, 0,
				25_719, 700, null, null, false, true) { WindowStartUtc = start, WindowMinutes = 180 }, DateTimeOffset.UtcNow);

			Assert.Equal(start, record.WindowStartUtc);
			Assert.Equal(180, record.WindowMinutes);
		}
	}
}
