using System;
using System.Collections.Generic;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>The NowCast fold-in's held-frame rule: a newly-arrived archive frame reuses the chunks scan it IS (same
	/// real scan time) instead of being downloaded + built again. Times from the KEVX MESO-SAILS volume of 2026-10-09.</summary>
	public class HeldFrameMatchTests
	{
		private static readonly TimeSpan Slack = TimeSpan.FromSeconds(30);
		private static DateTimeOffset Z(int h, int m, int s) => new(2026, 10, 9, h, m, s, TimeSpan.Zero);

		[Fact]
		public void Each_archive_pass_takes_the_held_scan_with_its_time()
		{
			var held = new List<(int, DateTimeOffset)> { (7, Z(22, 29, 48)), (8, Z(22, 32, 15)), (9, Z(22, 34, 12)), (10, Z(22, 36, 23)) };
			var arrivals = new List<(int, DateTimeOffset?)> { (6, Z(22, 29, 48)), (7, Z(22, 32, 16)), (8, Z(22, 34, 12)), (9, Z(22, 36, 22)) };
			Assert.Equal(new List<(int, int)> { (7, 6), (8, 7), (9, 8), (10, 9) }, HeldFrameMatch.Match(held, arrivals, Slack));
		}

		[Fact]
		public void A_pass_the_live_poll_never_saw_decodes()
		{
			// The poll caught passes 1 and 3 only; pass 2 has no held twin and must not borrow a neighbour's picture.
			var held = new List<(int, DateTimeOffset)> { (7, Z(22, 29, 48)), (8, Z(22, 34, 12)) };
			var arrivals = new List<(int, DateTimeOffset?)> { (6, Z(22, 29, 48)), (7, Z(22, 32, 15)), (8, Z(22, 34, 12)) };
			Assert.Equal(new List<(int, int)> { (7, 6), (8, 8) }, HeldFrameMatch.Match(held, arrivals, Slack));
		}

		[Fact]
		public void A_frame_with_no_prefetched_time_matches_nothing()
		{
			var held = new List<(int, DateTimeOffset)> { (7, Z(22, 29, 48)) };
			var arrivals = new List<(int, DateTimeOffset?)> { (6, null) };
			Assert.Empty(HeldFrameMatch.Match(held, arrivals, Slack));
		}

		// ── BEYOND THE ARCHIVE: which held scans are carried after the archive frames (the rest match or drop) ──

		private static bool Newer(DateTimeOffset live, DateTimeOffset archive) => live > archive + Slack; // = LiveIsNewer

		[Fact]
		public void With_the_newest_scan_time_known_only_later_scans_are_beyond()
		{
			var beyond = HeldFrameMatch.BeyondArchive(Z(22, 36, 23), Z(22, 29, 48), new DateTimeOffset?[] { null, null }, Slack, Newer);
			Assert.False(beyond(Z(22, 36, 23)));
			Assert.True(beyond(Z(22, 38, 30)));
		}

		[Fact]
		public void An_unknown_newest_rescan_does_not_carry_its_own_volumes_scans()
		{
			// KTLH 2026-10-10 00:50Z: archive volume 05:38:38 lands with pass #4 unknown ("planned but not in"). Its held scans
			// 05:40:19-05:44:20 used to be judged against the 05:38:38 STAMP, carried, and decoded again from the archive —
			// three scans twice, time running backwards. Only the next volume's (05:46:34, from the live schedule) is beyond.
			var beyond = HeldFrameMatch.BeyondArchive(null, Z(5, 38, 38), new DateTimeOffset?[] { Z(5, 38, 38), Z(5, 46, 34) }, Slack, Newer);
			Assert.False(beyond(Z(5, 40, 19)));
			Assert.False(beyond(Z(5, 42, 26)));
			Assert.False(beyond(Z(5, 44, 20)));
			Assert.True(beyond(Z(5, 46, 34)));
			Assert.True(beyond(Z(5, 48, 14)));
		}

		[Fact]
		public void An_unknown_newest_with_no_later_volume_carries_nothing()
		{
			var beyond = HeldFrameMatch.BeyondArchive(null, Z(5, 38, 38), new DateTimeOffset?[] { Z(5, 30, 56), Z(5, 38, 38) }, Slack, Newer);
			Assert.False(beyond(Z(5, 46, 34)));
		}

		[Fact]
		public void A_held_scan_is_never_given_out_twice()
		{
			var held = new List<(int, DateTimeOffset)> { (7, Z(22, 30, 0)) };
			var arrivals = new List<(int, DateTimeOffset?)> { (5, Z(22, 29, 40)), (6, Z(22, 30, 5)) };
			// Both arrivals are within slack (impossible for real passes, ≥ ~76 s apart) — the scan still goes to one frame only.
			var pairs = HeldFrameMatch.Match(held, arrivals, Slack);
			Assert.Single(pairs);
		}
	}
}
