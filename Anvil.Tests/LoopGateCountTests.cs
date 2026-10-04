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

		private static readonly string[] Keys = { "2024/05/07/KTLX/KTLX20240507_033006_V06",
			"2024/05/07/KTLX/KTLX20240507_033512_V06", "2024/05/07/KTLX/KTLX20240507_034018_V06" };

		private static RadarVolume Volume(string key) =>
			new("https://radarlevel2/x.V06", Ktlx, Level2RadarService.ParseVolumeTime(key)!.Value);

		// A replay over the three keys, armed but not loaded. fetch = the archive's EnsureCachedAsync (instant by default);
		// progress = its TryGetDownloadProgress (key → bytes, expected), for frames still downloading.
		private static RadarViewModel NewReplay(Func<string, Task<RadarVolume?>>? fetch = null,
			Func<string, (long Bytes, long Expected)?>? progress = null)
		{
			var settings = new AppSettings { HoldPastCastLoads = true };
			var svc = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			var archive = Null<ILevel2RadarService>.Create(new()
			{
				["GetKeysForWindowAsync"] = _ => Task.FromResult<IReadOnlyList<string>>(Keys),
				["EnsureCachedAsync"] = a => fetch is null ? Task.FromResult<RadarVolume?>(Volume((string)a![1]!)) : fetch((string)a![1]!),
				["TryGetDownloadProgress"] = a =>
				{
					if (progress?.Invoke((string)a![0]!) is not { } p) return false;
					a[1] = p.Bytes;
					a[2] = p.Expected;
					return true;
				},
			});
			var radar = new RadarViewModel(Null<IMapService>.Create(), Null<IRadarSiteProvider>.Create(), archive,
				Null<IDowEventProvider>.Create(), svc, null);
			radar.IsPastEventMode = true;
			radar.SelectedRadarOption = new RadarOption("KTLX", Ktlx);
			radar.PastEventDate = new DateTimeOffset(2024, 5, 6, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2024, 5, 6)));
			radar.PastEventTime = new TimeSpan(22, 30, 0);
			radar.PastEventDurationIndex = 0; // 30 min
			return radar;
		}

		private static async Task<RadarViewModel> LoadedThreeFrames(Action<LoopHoldingGateViewModel>? beforeLoad = null)
		{
			var radar = NewReplay();
			beforeLoad?.Invoke(radar.LoopGate);
			Assert.True(await radar.LoadSelectedPastEventAsync());
			return radar;
		}

		// ── The loading screen's CELLS (LoopLoadProgressBar): each frame's real state, never a guess ──────────────

		[Fact]
		public async Task A_download_cell_fills_with_its_bytes_while_the_volume_streams_in()
		{
			var last = new TaskCompletionSource<RadarVolume?>();
			var radar = NewReplay(
				key => key == Keys[2] ? last.Task : Task.FromResult<RadarVolume?>(Volume(key)),
				key => key == Keys[2] ? (2_000_000, 4_000_000) : null);
			var load = radar.LoadSelectedPastEventAsync(); // frames 0 and 1 land at once; frame 2 is still downloading

			radar.RefreshLoopGateCells();
			Assert.Equal(new[] { 1.0, 1.0, 0.5 }, radar.LoopGate.DownloadCells);
			Assert.EndsWith("· 2.0 of 4.0 MB", radar.LoopGate.DownloadCaption);
			Assert.StartsWith("1 at once", radar.LoopGate.DownloadDetail);

			last.SetResult(Volume(Keys[2]));
			Assert.True(await load);
			Assert.Equal(new[] { 1.0, 1.0, 1.0 }, radar.LoopGate.DownloadCells);
			Assert.Equal("All volumes in", radar.LoopGate.DownloadCaption);
		}

		[Fact]
		public async Task A_build_cell_steps_through_decoding_reflectivity_and_built()
		{
			var radar = await LoadedThreeFrames();
			radar.OnRadarFrameReady(0, hasData: true); // frame 0: reflectivity drawn, velocity to come
			radar.OnRadarFrameReady(2, hasData: true); // frame 2: fully built
			radar.SetBuildProgress(1, 3, new[] { true, true, true }, new[] { false, false, true }, new[] { false, true, false });

			Assert.Equal(new[] { RadarViewModel.LitStep, RadarViewModel.DecodingStep, 1.0 }, radar.LoopGate.BuildCells);
			Assert.EndsWith("· Velocity next", radar.LoopGate.BuildCaption); // the OLDEST frame in progress
			Assert.StartsWith("1 at once", radar.LoopGate.BuildDetail);
		}

		[Fact]
		public async Task A_built_frame_behind_a_slower_one_counts_although_the_scrubber_hides_it()
		{
			var radar = await LoadedThreeFrames();
			for (var i = 0; i < 3; i++) radar.OnRadarFrameReady(i, hasData: true);
			radar.SetBuildProgress(1, 3, new[] { true, true, true }, new[] { true, false, true });

			Assert.False(radar.Segments[2].IsReady);   // the scrubber reveals left to right …
			Assert.Equal(2, radar.LoopGate.Built);      // … the loading screen counts what IS built
			Assert.Equal(1.0, radar.LoopGate.BuildCells[2]);
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
