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
	/// The sweep pulse (the user's design): it plays when a new LATEST frame is drawn, so you watch the radar "get" the
	/// frame. An in-place live update used to pulse even while you were scrubbed back to an older frame (2026-10-04).
	/// A real NowCast loop over a fake archive + map; frame-ready events are fed by hand, as the page would.
	/// </summary>
	public class LiveSweepTests
	{
		private static readonly RadarSite Ktlx = new("KTLX", "Norman", 35.333, -97.278);
		private static readonly string[] Keys = { "2024/05/07/KTLX/KTLX20240507_033006_V06",
			"2024/05/07/KTLX/KTLX20240507_033512_V06", "2024/05/07/KTLX/KTLX20240507_034018_V06" };

		private sealed class Rig
		{
			public RadarViewModel Radar = null!;
			public int Pulses;
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

		// A loaded NowCast loop: 3 archive frames + the live frame appended (its pulse already counted).
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
				["PulseRadarSweepAsync"] = _ => { Interlocked.Increment(ref rig.Pulses); return Task.CompletedTask; },
				["AddRadarFrameAsync"] = a =>
				{
					if ((int)a![1]! == Keys.Length) Interlocked.Increment(ref rig.LiveSlotRequests);
					return Task.CompletedTask;
				},
			});
			rig.Radar = new RadarViewModel(map, Null<IRadarSiteProvider>.Create(), archive, Null<IDowEventProvider>.Create(), svc, null);
			await rig.Radar.OnMapsReadyAsync();
			rig.Radar.SelectedRadarOption = new RadarOption("KTLX", Ktlx);

			await WaitFor(() => rig.LiveSlotRequests == 1);             // the backfill is done, the live append is pending
			for (var i = 0; i < Keys.Length; i++) rig.Radar.OnRadarFrameReady(i, true);
			rig.Radar.OnRadarFrameReady(Keys.Length, true);             // live frame decoded → appended + shown
			Assert.Equal(Keys.Length, (int)rig.Radar.CurrentFrameIndex);
			Assert.Equal(1, rig.Pulses);
			return rig;
		}

		// A newer live volume, re-decoded into the live slot (the poll's in-place update), then landing.
		private static async Task LiveUpdateLands(Rig rig)
		{
			rig.Live = LiveAt(47);
			await WaitFor(() =>
			{
				if (rig.LiveSlotRequests < 2) _ = rig.Radar.ForceLiveFrameCheckAsync(); // skipped until the load finishes
				return rig.LiveSlotRequests >= 2;
			});
			rig.Radar.OnRadarFrameReady(Keys.Length, true);
		}

		[Fact]
		public async Task A_live_update_on_screen_sweeps()
		{
			var rig = await LoadedLiveLoop();
			await LiveUpdateLands(rig);
			Assert.Equal(2, rig.Pulses);
		}

		[Fact]
		public async Task A_live_update_while_scrubbed_back_does_not_sweep()
		{
			var rig = await LoadedLiveLoop();
			rig.Radar.CurrentFrameIndex = 0;
			await LiveUpdateLands(rig);
			Assert.Equal(1, rig.Pulses);
		}
	}
}
