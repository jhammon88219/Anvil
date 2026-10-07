using System;
using System.Collections.Generic;
using System.Linq;
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
	/// NowCast's NEW LIVE FRAME in the bar's activity slot (replaced the map's sweep pulse, 2026-10-07): it lights when a
	/// poll brings a newer scan, steps to "building" when a worker takes it, and says "Complete" once the frame is in the
	/// loop — then holds like "Loop ready". A loop's FIRST load is not news. A real NowCast loop over a fake archive +
	/// map; frame-ready and build-progress events are fed by hand, as the page would.
	/// </summary>
	public class LiveFrameActivityTests
	{
		private static readonly RadarSite Ktlx = new("KTLX", "Norman", 35.333, -97.278);
		private static readonly string[] Keys = { "2024/05/07/KTLX/KTLX20240507_033006_V06",
			"2024/05/07/KTLX/KTLX20240507_033512_V06", "2024/05/07/KTLX/KTLX20240507_034018_V06" };

		private sealed class ManualDelay
		{
			private TaskCompletionSource _tcs = new();
			public Task Wait(int _) => _tcs.Task;
			public void Fire() { var t = _tcs; _tcs = new(); t.SetResult(); }
		}

		private sealed class Rig
		{
			public RadarViewModel Radar = null!;
			public BarActivityViewModel Bar = null!;
			public readonly ManualDelay Delay = new();
			public readonly List<LiveFrameStage> Stages = new();
			public int LiveSlotRequests; // AddRadarFrameAsync into the live slot (index 3)
			public RadarVolume Live = LiveAt(45);
		}

		private static RadarVolume LiveAt(int minute) =>
			new("https://radarlevel2/live.V06", Ktlx, new DateTimeOffset(2024, 5, 7, 3, minute, 0, TimeSpan.Zero), "VCP 212", new[] { 0.5f });

		private static async Task WaitFor(Func<bool> condition)
		{
			var deadline = DateTime.UtcNow.AddSeconds(5);
			while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
			Assert.True(condition());
		}

		// A loaded NowCast loop: 3 archive frames + the live frame appended by the FIRST load.
		private static async Task<Rig> LoadedLiveLoop()
		{
			var rig = new Rig();
			var settings = new AppSettings();
			var svc = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			var archive = Null<ILevel2RadarService>.Create(new()
			{
				["GetRecentKeysAsync"] = _ => Task.FromResult<IReadOnlyList<string>>(Keys),
				["EnsureCachedAsync"] = a => Task.FromResult<RadarVolume?>(new RadarVolume("https://radarlevel2/x.V06", Ktlx,
					Level2RadarService.ParseVolumeTime((string)a![1]!)!.Value, "VCP 212", new[] { 0.5f })),
				["GetLiveFrameAsync"] = _ => Task.FromResult<RadarVolume?>(rig.Live),
			});
			var map = Null<IMapService>.Create(new()
			{
				["AddRadarFrameAsync"] = a =>
				{
					if ((int)a![1]! == Keys.Length) Interlocked.Increment(ref rig.LiveSlotRequests);
					return Task.CompletedTask;
				},
			});
			rig.Radar = new RadarViewModel(map, Null<IRadarSiteProvider>.Create(), archive, Null<IDowEventProvider>.Create(), svc, null);
			rig.Bar = new BarActivityViewModel(rig.Delay.Wait);
			rig.Bar.WatchLiveFrame(rig.Radar);
			rig.Radar.LiveFrameActivity += (_, a) => rig.Stages.Add(a.Stage);
			await rig.Radar.OnMapsReadyAsync();
			rig.Radar.SelectedRadarOption = new RadarOption("KTLX", Ktlx);

			await WaitFor(() => rig.LiveSlotRequests == 1);             // the backfill is done, the live append is pending
			for (var i = 0; i < Keys.Length; i++) rig.Radar.OnRadarFrameReady(i, true);
			rig.Radar.OnRadarFrameReady(Keys.Length, true);             // live frame decoded → appended + shown
			Assert.Equal(Keys.Length, (int)rig.Radar.CurrentFrameIndex);
			return rig;
		}

		// A newer live volume, re-decoded into the live slot by the poll's in-place update (not yet landed).
		private static async Task LiveUpdateFound(Rig rig)
		{
			rig.Live = LiveAt(47);
			await WaitFor(() =>
			{
				if (rig.LiveSlotRequests < 2) _ = rig.Radar.ForceLiveFrameCheckAsync(); // skipped until the load finishes
				return rig.LiveSlotRequests >= 2;
			});
		}

		[Fact]
		public async Task The_first_loads_live_frame_is_not_announced()
		{
			var rig = await LoadedLiveLoop();
			Assert.Empty(rig.Stages);
			Assert.False(rig.Bar.IsShown);
		}

		[Fact]
		public async Task A_new_scan_lights_builds_then_says_complete_and_holds()
		{
			var rig = await LoadedLiveLoop();
			await LiveUpdateFound(rig);
			Assert.True(rig.Bar.IsShown);
			Assert.Equal(BarActivityKind.LiveFrame, rig.Bar.Kind);
			Assert.Equal("New scan · KTLX", rig.Bar.Title);
			Assert.EndsWith("· downloaded", rig.Bar.Detail);
			Assert.Equal(0, rig.Bar.Progress);
			Assert.Equal(1, rig.Bar.Secondary);                          // its chunks are on disk before it's known to be new

			var decoding = new bool[Keys.Length + 1];
			decoding[Keys.Length] = true;
			rig.Radar.SetBuildProgress(Keys.Length, Keys.Length + 1, null, null, decoding);
			Assert.EndsWith("· building", rig.Bar.Detail);
			Assert.Equal(1.0 / 3, rig.Bar.Progress, 3);

			rig.Radar.OnRadarFrameReady(Keys.Length, true);             // the new returns landed
			Assert.Equal(new[] { LiveFrameStage.Found, LiveFrameStage.Decoding, LiveFrameStage.Shown }, rig.Stages);
			Assert.Equal(BarActivityTone.Done, rig.Bar.Tone);
			Assert.Equal("Complete", rig.Bar.Detail);
			Assert.StartsWith("New frame · KTLX ", rig.Bar.Title);
			Assert.Equal(1, rig.Bar.Progress);

			rig.Delay.Fire();                                            // the hold runs out
			await WaitFor(() => !rig.Bar.IsShown);
		}

		[Fact]
		public async Task A_new_scan_while_scrubbed_back_still_completes()
		{
			var rig = await LoadedLiveLoop();
			rig.Radar.CurrentFrameIndex = 0;
			await LiveUpdateFound(rig);
			rig.Radar.OnRadarFrameReady(Keys.Length, true);
			Assert.Equal(new[] { LiveFrameStage.Found, LiveFrameStage.Shown }, rig.Stages);
			Assert.Equal("Complete", rig.Bar.Detail);
		}

		[Fact]
		public async Task Leaving_the_site_before_it_lands_clears_the_slot()
		{
			var rig = await LoadedLiveLoop();
			await LiveUpdateFound(rig);
			rig.Radar.SelectedRadarOption = null;
			await WaitFor(() => rig.Stages.Contains(LiveFrameStage.Dropped));
			Assert.False(rig.Bar.IsShown);
		}
	}
}
