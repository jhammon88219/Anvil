using System;
using System.Threading.Tasks;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The bar's activity readout (<see cref="BarActivityViewModel"/>): one slot, the highest-ranked activity
	/// shows and the rest wait as "+n"; a finish flash yields after its timer. (The PastCast loop's line is gone —
	/// a PastCast load shows only on its gate, which has no escape since 2026-10-10.)
	/// </summary>
	public class BarActivityTests
	{
		// A flash timer the test releases by hand.
		private sealed class ManualDelay
		{
			private TaskCompletionSource _tcs = new();
			public Task Wait(int _) => _tcs.Task;
			public void Fire() { var t = _tcs; _tcs = new(); t.SetResult(); }
		}

		[Fact]
		public void The_top_ranked_activity_shows_and_the_rest_wait()
		{
			var bar = new BarActivityViewModel(new ManualDelay().Wait);
			Assert.False(bar.IsShown);

			bar.Set(BarActivityKind.SiteCheck, "Checking radar sites", "84 / 159", 0.53, tone: BarActivityTone.Housekeeping);
			Assert.True(bar.IsShown);
			Assert.Equal("Checking radar sites", bar.Title);
			Assert.Equal("", bar.QueuedText);
			Assert.False(bar.HasSecondary);

			bar.Set(BarActivityKind.Search, "Searching places", "", 0.13, 0.31); // ranks above the site check
			Assert.Equal(BarActivityKind.Search, bar.Kind);
			Assert.Equal("+1", bar.QueuedText);
			Assert.True(bar.HasSecondary);

			bar.Clear(BarActivityKind.Search);
			Assert.Equal("Checking radar sites", bar.Title);
			Assert.Equal("", bar.QueuedText);

			bar.Clear(BarActivityKind.SiteCheck);
			Assert.False(bar.IsShown);
		}

		[Fact]
		public void A_flash_yields_when_its_timer_runs_out_unless_replaced()
		{
			var delay = new ManualDelay();
			var bar = new BarActivityViewModel(delay.Wait);

			bar.Flash(BarActivityKind.SiteCheck, "Site check complete", "181 online · 23 offline", BarActivityTone.Done, fullBar: true);
			Assert.True(bar.IsShown);
			Assert.Equal(1.0, bar.Progress);
			delay.Fire();
			Assert.False(bar.IsShown);

			bar.Flash(BarActivityKind.SiteCheck, "Site check complete", "", BarActivityTone.Done, fullBar: true);
			bar.Set(BarActivityKind.SiteCheck, "Checking radar sites", "0 / 159", 0); // a new pass before it expired
			delay.Fire();
			Assert.True(bar.IsShown); // the stale timer must not hide the new pass
			Assert.Equal("Checking radar sites", bar.Title);
		}

		[Fact]
		public void There_is_no_loop_activity_any_more()
		{
			// The PastCast loop's bar line went with the gate's escape (2026-10-10) — don't bring it back.
			Assert.DoesNotContain("Loop", Enum.GetNames<BarActivityKind>());
		}
	}
}
