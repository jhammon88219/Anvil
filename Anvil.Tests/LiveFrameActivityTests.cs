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
	/// NowCast's live polls in the bar's activity slot (replaced the map's sweep pulse, 2026-10-07). EVERY poll lights
	/// it — "Checking … for a new scan" with the chunk download as the thin line — and ends on "No new scan", or on a
	/// new frame's found → building → painting → "Complete", which waits until the page has DRAWN the frame. A loop's
	/// FIRST load is not announced. A real NowCast loop over a fake archive + map; frame-ready, build-progress and
	/// paint reports are fed by hand, as the page would.
	/// </summary>
	public class LiveFrameActivityTests
	{
		private static readonly RadarSite Ktlx = new("KTLX", "Norman", 35.333, -97.278);
		private static readonly string[] Keys = { "2024/05/07/KTLX/KTLX20240507_033006_V06",
			"2024/05/07/KTLX/KTLX20240507_033512_V06", "2024/05/07/KTLX/KTLX20240507_034018_V06" };

		// Where a NEWER scan lands: the first load's live frame (index 3) is HELD as its own frame, the new scan appends.
		private static int NewSlot => Keys.Length + 1;

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
			public readonly List<int> PaintWatches = new();
			public int LiveSlotRequests; // AddRadarFrameAsync into a live slot (index 3, then 4 after a HOLD)
			public RadarVolume Live = LiveAt(45);
			// Set → the next live fetch reports its chunks through it and waits for the test to release it.
			public TaskCompletionSource? HoldFetch;
			public IProgress<(int Done, int Total)>? FetchProgress;
		}

		private static RadarVolume LiveAt(int minute) =>
			new("https://radarlevel2/live.V06", Ktlx, new DateTimeOffset(2024, 5, 7, 3, 0, 0, TimeSpan.Zero).AddMinutes(minute), "VCP 212", new[] { 0.5f });

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
				["GetLiveFrameAsync"] = a =>
				{
					if (rig.HoldFetch is not { } hold) return Task.FromResult<RadarVolume?>(rig.Live);
					rig.FetchProgress = (IProgress<(int, int)>?)a![3];
					return hold.Task.ContinueWith(_ => (RadarVolume?)rig.Live);
				},
			});
			var map = Null<IMapService>.Create(new()
			{
				["AddRadarFrameAsync"] = a =>
				{
					if ((int)a![1]! >= Keys.Length) Interlocked.Increment(ref rig.LiveSlotRequests);
					return Task.CompletedTask;
				},
				["WatchRadarPaintAsync"] = a => { lock (rig.PaintWatches) rig.PaintWatches.Add((int)a![0]!); return Task.CompletedTask; },
			});
			rig.Radar = new RadarViewModel(map, Null<IRadarSiteProvider>.Create(), archive, Null<IDowEventProvider>.Create(), svc, null);
			rig.Bar = new BarActivityViewModel(rig.Delay.Wait);
			rig.Bar.WatchLiveFrame(rig.Radar);
			rig.Radar.LiveFrameActivity += (_, a) => { lock (rig.Stages) rig.Stages.Add(a.Stage); };
			await rig.Radar.OnMapsReadyAsync();
			rig.Radar.SelectedRadarOption = new RadarOption("KTLX", Ktlx);

			await WaitFor(() => rig.LiveSlotRequests == 1);             // the backfill is done, the live append is pending
			for (var i = 0; i < Keys.Length; i++) rig.Radar.OnRadarFrameReady(i, true);
			rig.Radar.OnRadarFrameReady(Keys.Length, true);             // live frame decoded → appended + shown
			Assert.Equal(Keys.Length, (int)rig.Radar.CurrentFrameIndex);
			return rig;
		}

		// One poll (the bar's "check now" path — the same one the timer runs). Retried until the first load is done.
		private static async Task Poll(Rig rig, Func<bool> done)
		{
			await WaitFor(() =>
			{
				if (!done()) _ = rig.Radar.ForceLiveFrameCheckAsync();
				return done();
			});
		}

		private static LiveFrameStage[] Distinct(Rig rig)
		{
			lock (rig.Stages) return rig.Stages.Where((s, i) => i == 0 || rig.Stages[i - 1] != s).ToArray();
		}

		[Fact]
		public async Task The_first_loads_live_frame_is_not_announced()
		{
			var rig = await LoadedLiveLoop();
			Assert.Empty(rig.Stages);
			Assert.False(rig.Bar.IsShown);
		}

		[Fact]
		public async Task A_poll_with_nothing_newer_still_lights_and_says_so()
		{
			var rig = await LoadedLiveLoop();
			await Poll(rig, () => rig.Stages.Contains(LiveFrameStage.Unchanged));
			Assert.Equal(new[] { LiveFrameStage.Checking, LiveFrameStage.Unchanged }, Distinct(rig));
			Assert.Equal("No new scan", rig.Bar.Title);
			Assert.EndsWith("scan is the newest", rig.Bar.Detail);
			Assert.Equal(BarActivityTone.Housekeeping, rig.Bar.Tone);
			rig.Delay.Fire();
			await WaitFor(() => !rig.Bar.IsShown);
		}

		[Fact]
		public async Task The_poll_shows_its_chunk_download()
		{
			var rig = await LoadedLiveLoop();
			rig.HoldFetch = new TaskCompletionSource();
			await Poll(rig, () => rig.FetchProgress is not null);
			Assert.Equal("Checking for a new scan", rig.Bar.Title);

			rig.FetchProgress!.Report((2, 4));
			await WaitFor(() => rig.Bar.Detail == "2 of 4 chunks");
			Assert.Equal(0.5, rig.Bar.Secondary);
			rig.HoldFetch.SetResult();
			await WaitFor(() => rig.Stages.Contains(LiveFrameStage.Unchanged));
		}

		[Fact]
		public async Task A_new_scan_says_complete_only_once_it_is_painted()
		{
			var rig = await LoadedLiveLoop();
			rig.Live = LiveAt(47);
			await Poll(rig, () => rig.LiveSlotRequests >= 2);
			Assert.Equal(new[] { NewSlot }, rig.PaintWatches);          // armed before the decode
			Assert.Equal("New scan", rig.Bar.Title);
			Assert.EndsWith("· downloaded", rig.Bar.Detail);
			Assert.Equal(1, rig.Bar.Secondary);

			var decoding = new bool[NewSlot + 1];
			decoding[NewSlot] = true;
			rig.Radar.SetBuildProgress(NewSlot, NewSlot + 1, null, null, decoding);
			Assert.EndsWith("· building", rig.Bar.Detail);
			Assert.Equal(1.0 / 3, rig.Bar.Progress, 3);

			rig.Radar.OnRadarFrameReady(NewSlot, true);                 // decoded — NOT yet drawn
			Assert.EndsWith("· painting", rig.Bar.Detail);
			Assert.Equal(2.0 / 3, rig.Bar.Progress, 3);
			Assert.NotEqual(BarActivityTone.Done, rig.Bar.Tone);

			rig.Radar.OnRadarPainted(NewSlot);                          // the page drew it
			Assert.Equal(new[] { LiveFrameStage.Checking, LiveFrameStage.Found, LiveFrameStage.Decoding,
				LiveFrameStage.Painting, LiveFrameStage.Shown }, Distinct(rig));
			Assert.Equal(BarActivityTone.Done, rig.Bar.Tone);
			Assert.Equal("Complete", rig.Bar.Detail);
			Assert.StartsWith("New frame · ", rig.Bar.Title);
			Assert.DoesNotContain("KTLX", rig.Bar.Title);

			rig.Delay.Fire();
			await WaitFor(() => !rig.Bar.IsShown);
		}

		[Fact]
		public async Task A_paint_reported_before_frame_ready_completes_at_frame_ready()
		{
			var rig = await LoadedLiveLoop();
			rig.Live = LiveAt(47);
			await Poll(rig, () => rig.LiveSlotRequests >= 2);
			rig.Radar.OnRadarPainted(NewSlot);
			rig.Radar.OnRadarFrameReady(NewSlot, true);
			Assert.Equal("Complete", rig.Bar.Detail);
		}

		[Fact]
		public async Task A_new_scan_while_scrubbed_back_is_ready_when_built_there_is_nothing_to_paint()
		{
			var rig = await LoadedLiveLoop();
			rig.Radar.CurrentFrameIndex = 0;
			rig.Live = LiveAt(47);
			await Poll(rig, () => rig.LiveSlotRequests >= 2);
			rig.Radar.OnRadarFrameReady(NewSlot, true);
			Assert.DoesNotContain(LiveFrameStage.Painting, rig.Stages);
			Assert.Equal("Ready", rig.Bar.Detail);                      // in the loop, but not drawn: not "Complete"
		}

		// ── HELD LIVE FRAMES: a newer scan no longer overwrites the live slot (the SAILS cells rebuilding between polls) ──

		[Fact]
		public async Task A_newer_scan_keeps_the_previous_one_as_its_own_frame()
		{
			var rig = await LoadedLiveLoop();
			rig.Live = LiveAt(47);
			await Poll(rig, () => rig.LiveSlotRequests >= 2);
			rig.Radar.OnRadarFrameReady(NewSlot, true);

			Assert.Equal(NewSlot, rig.Radar.MaxFrameIndex);                     // one more frame, not the same count
			Assert.Equal(NewSlot, (int)rig.Radar.CurrentFrameIndex);            // following the newest
			Assert.Equal(LiveAt(47).VolumeTime, rig.Radar.AgeReferenceTime);
			Assert.EndsWith("· live", rig.Radar.RadarFrameDetail);
			rig.Radar.CurrentFrameIndex = Keys.Length;                          // the held scan is still there, untouched
			Assert.Equal(LiveAt(45).VolumeTime, rig.Radar.AgeReferenceTime);
			Assert.EndsWith("· live", rig.Radar.RadarFrameDetail);              // a chunks scan, not an archive one
		}

		[Fact]
		public async Task A_newer_scan_does_not_move_a_playhead_scrubbed_back()
		{
			var rig = await LoadedLiveLoop();
			rig.Radar.CurrentFrameIndex = 1;
			rig.Live = LiveAt(47);
			await Poll(rig, () => rig.LiveSlotRequests >= 2);
			rig.Radar.OnRadarFrameReady(NewSlot, true);
			Assert.Equal(1, (int)rig.Radar.CurrentFrameIndex);
			Assert.Equal(NewSlot, rig.Radar.MaxFrameIndex);
		}

		[Fact]
		public async Task Past_the_held_cap_a_newer_scan_updates_the_slot_in_place()
		{
			var rig = await LoadedLiveLoop();
			const int cap = 8; // RadarLoopEngine.MaxHeldLiveFrames
			for (var k = 1; k <= cap + 1; k++)
			{
				rig.Live = LiveAt(45 + 2 * k);
				var requests = k + 1;
				await Poll(rig, () => rig.LiveSlotRequests >= requests);
				// Held → the new scan appends one past the old slot; at the cap it re-decodes INTO the slot.
				var slot = Keys.Length + Math.Min(k, cap);
				rig.Radar.OnRadarFrameReady(slot, true);
				Assert.Equal(slot, rig.Radar.MaxFrameIndex);
			}
			Assert.Equal(LiveAt(45 + 2 * (cap + 1)).VolumeTime, rig.Radar.AgeReferenceTime);
		}

		[Fact]
		public async Task Leaving_the_site_before_it_lands_clears_the_slot()
		{
			var rig = await LoadedLiveLoop();
			rig.Live = LiveAt(47);
			await Poll(rig, () => rig.LiveSlotRequests >= 2);
			rig.Radar.SelectedRadarOption = null;
			await WaitFor(() => rig.Stages.Contains(LiveFrameStage.Dropped));
			Assert.False(rig.Bar.IsShown);
		}

		[Fact]
		public async Task The_console_age_describes_the_frame_on_screen_not_the_newest()
		{
			var rig = await LoadedLiveLoop();
			Assert.Equal(LiveAt(45).VolumeTime, rig.Radar.AgeReferenceTime);       // on the newest (the live frame)
			rig.Radar.CurrentFrameIndex = 0;                                      // scrubbed back to the oldest
			Assert.Equal(Level2RadarService.ParseVolumeTime(Keys[0]), rig.Radar.AgeReferenceTime);
			Assert.EndsWith(" ago", rig.Radar.RadarAgeText);
		}

		// ── IDLE: the slot never disappears; NowCast idle = the next check + the expected scan (or the interval) ──

		[Fact]
		public async Task Idle_while_polling_shows_the_countdown_and_what_is_due_without_a_site_id()
		{
			var rig = await LoadedLiveLoop();
			await WaitFor(() => rig.Radar.IsLivePolling);                  // the poll loop has scheduled its next check
			await WaitFor(() => rig.Bar.Title.StartsWith("Next check in", StringComparison.Ordinal)); // the 1 s tick
			Assert.False(rig.Bar.IsShown);                                  // dimmed, not gone
			Assert.Equal(BarActivityTone.Idle, rig.Bar.Tone);
			Assert.Matches(@"^Next check in \d+:\d\d$", rig.Bar.Title);
			Assert.True(rig.Bar.HasSecondary);
			Assert.DoesNotContain("KTLX", rig.Bar.Title + rig.Bar.Detail);

			rig.Radar.LivePollingModeIndex = 1;                             // fixed time
			Assert.Equal("every 30 s", rig.Bar.Detail);
			rig.Radar.RefreshIntervalIndex = 3;
			await WaitFor(() => rig.Bar.Detail == "every 60 s");           // the next tick re-reads it
		}

		[Fact]
		public async Task Idle_with_no_live_poll_is_an_empty_plate()
		{
			var rig = await LoadedLiveLoop();
			rig.Radar.SelectedRadarOption = null;
			await WaitFor(() => !rig.Radar.IsLivePolling);
			await WaitFor(() => rig.Bar.Title.Length == 0);                // the next tick
			Assert.False(rig.Bar.IsShown);
			Assert.Equal(string.Empty, rig.Bar.Title);
			Assert.False(rig.Bar.HasSecondary);
		}
	}
}
