using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Models;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Which frames a PastCast window loads (<see cref="ReplayFramePlan"/>): every SAILS / MRLE 0.5° pass is a frame,
	/// never thinned inside an event window — the span is shortened around the event instead (what a warning forecaster
	/// loops); only an overview window (&gt; 2 h) falls back to one frame per volume.
	/// </summary>
	public class ReplayFramePlanTests
	{
		// Volumes every 6 min from 00:00Z, keyed like the archive ("KVNX20260424_000000_V06").
		private static List<string> Volumes(int count) => Enumerable.Range(0, count)
			.Select(i => $"2026/04/24/KVNX/KVNX20260424_{new DateTime(2026, 4, 24).AddMinutes(6 * i):HHmmss}_V06").ToList();

		private static Dictionary<string, int> Passes(IEnumerable<string> volumes, int n) => volumes.ToDictionary(v => v, _ => n);

		[Fact]
		public void A_window_that_fits_gets_every_pass_of_every_volume()
		{
			var volumes = Volumes(10);
			var (frames, choice) = ReplayFramePlan.Choose(volumes, Passes(volumes, 4), 60, null, cap: 40);

			Assert.Equal(ReplayFrameChoice.EveryPass, choice);
			Assert.Equal(40, frames.Count);
			Assert.Equal(new[] { volumes[0], volumes[0] + "#2", volumes[0] + "#3", volumes[0] + "#4", volumes[1] }, frames.Take(5));
		}

		[Fact]
		public void An_event_window_over_the_cap_keeps_every_pass_and_centres_on_the_key()
		{
			// 2 h of SAILS ×3 = 20 volumes × 4 passes = 80 frames; the key is the 01:30Z volume.
			var volumes = Volumes(20);
			var focus = new DateTimeOffset(2026, 4, 24, 1, 30, 0, TimeSpan.Zero);
			var (frames, choice) = ReplayFramePlan.Choose(volumes, Passes(volumes, 4), 120, focus, cap: 40);

			Assert.Equal(ReplayFrameChoice.TrimmedAroundEvent, choice);
			Assert.Equal(40, frames.Count);
			// Contiguous: no pass skipped anywhere in the run.
			var all = RadarFrameKey.Expand(volumes, Passes(volumes, 4));
			var start = all.IndexOf(frames[0]);
			Assert.Equal(all.GetRange(start, 40), frames);
			// The 01:30Z volume's passes sit in the middle of the run.
			var keyIndex = frames.IndexOf(volumes[15]);
			Assert.InRange(keyIndex, 15, 25);
		}

		[Fact]
		public void Without_a_key_an_event_window_centres_on_its_middle()
		{
			var volumes = Volumes(20);
			var (frames, _) = ReplayFramePlan.Choose(volumes, Passes(volumes, 4), 120, null, cap: 40);

			// 80 frames, 40 kept from the middle: the run starts ~20 frames in (within a volume of it).
			var all = RadarFrameKey.Expand(volumes, Passes(volumes, 4));
			Assert.InRange(all.IndexOf(frames[0]), 16, 24);
		}

		[Fact]
		public void A_key_near_the_start_slides_the_run_to_the_start()
		{
			var volumes = Volumes(20);
			var focus = new DateTimeOffset(2026, 4, 24, 0, 3, 0, TimeSpan.Zero);
			var (frames, _) = ReplayFramePlan.Choose(volumes, Passes(volumes, 4), 120, focus, cap: 40);

			Assert.Equal(volumes[0], frames[0]);
		}

		[Fact]
		public void An_overview_window_is_one_frame_per_volume()
		{
			var volumes = Volumes(30); // 3 h
			var (frames, choice) = ReplayFramePlan.Choose(volumes, Passes(volumes, 4), 180, null, cap: 40);

			Assert.Equal(ReplayFrameChoice.VolumeStarts, choice);
			Assert.Equal(volumes, frames);
		}

		[Fact]
		public void A_long_overview_thins_its_volumes_evenly()
		{
			var volumes = Volumes(120); // 12 h
			var (frames, choice) = ReplayFramePlan.Choose(volumes, Passes(volumes, 4), 720, null, cap: 40);

			Assert.Equal(ReplayFrameChoice.SampledVolumes, choice);
			Assert.Equal(40, frames.Count);
			Assert.Equal(volumes[0], frames[0]);
			Assert.Equal(volumes[^1], frames[^1]);
			Assert.DoesNotContain(frames, f => f.Contains('#'));
		}

		[Fact]
		public void Clear_air_is_unchanged()
		{
			var volumes = Volumes(12);
			var (frames, choice) = ReplayFramePlan.Choose(volumes, new Dictionary<string, int>(), 120, null, cap: 40);

			Assert.Equal(ReplayFrameChoice.EveryPass, choice);
			Assert.Equal(volumes, frames);
		}
	}
}
