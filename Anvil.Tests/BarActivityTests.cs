using System.Threading.Tasks;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;
using static Anvil.Tests.TemporalWindowPersistenceTests;

namespace Anvil.Tests
{
	/// <summary>
	/// The bar's activity readout (<see cref="BarActivityViewModel"/>): one slot, the highest-ranked activity
	/// shows and the rest wait as "+n"; a finish flash yields after its timer; and the PastCast loop appears
	/// there only while its load runs WITHOUT the gate on screen.
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

		private static LoopHoldingGateViewModel NewGate(bool hold = true)
		{
			var settings = new AppSettings { HoldPastCastLoads = hold };
			var svc = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			return new LoopHoldingGateViewModel(svc, () => Task.CompletedTask);
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

			bar.Set(BarActivityKind.Loop, "Loading the loop · KTLX", "5 of 39 built", 0.13, 0.31);
			Assert.Equal(BarActivityKind.Loop, bar.Kind);
			Assert.Equal("+1", bar.QueuedText);
			Assert.True(bar.HasSecondary);

			bar.Clear(BarActivityKind.Loop);
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
		public void The_loop_shows_in_the_bar_only_while_its_gate_is_not_up()
		{
			var gate = NewGate();
			var bar = new BarActivityViewModel(new ManualDelay().Wait);
			bar.WatchLoop(gate);

			gate.Begin("KTLX", "x");
			Assert.False(bar.IsShown); // the gate is carrying it

			gate.Arm();
			gate.Report(39, 31, 22);
			gate.RequestEscape();
			gate.UseMap();                       // the escape: the load carries on in the bar
			Assert.True(bar.IsShown);
			Assert.Equal("Loading the loop · KTLX", bar.Title);
			Assert.Equal("22 of 39 built", bar.Detail);
			Assert.True(bar.CanReopen);

			gate.Report(39, 39, 30);             // counts keep flowing after the escape
			Assert.Equal("30 of 39 built", bar.Detail);

			bar.Reopen();                        // "Hold the map again"
			Assert.True(gate.IsShown);
			Assert.False(bar.IsShown);
		}

		[Fact]
		public void A_load_finished_in_the_bar_flashes_ready_and_one_finished_under_the_gate_does_not()
		{
			var delay = new ManualDelay();
			var gate = NewGate(hold: false);     // holding off: every load is the bar's
			var bar = new BarActivityViewModel(delay.Wait);
			bar.WatchLoop(gate);

			gate.Begin("KTLX", "x");
			Assert.Equal("Finding volumes…", bar.Detail);
			gate.Arm();
			gate.Report(39, 39, 39);
			gate.Complete();
			Assert.Equal("Loop ready", bar.Title);
			Assert.Equal("39 frames", bar.Detail);
			delay.Fire();
			Assert.False(bar.IsShown);

			var held = NewGate(hold: true);
			var bar2 = new BarActivityViewModel(delay.Wait);
			bar2.WatchLoop(held);
			held.Begin("KTLX", "x");
			held.Arm();
			held.Report(39, 39, 39);
			held.Complete();
			Assert.False(bar2.IsShown);
		}

		[Fact]
		public void A_failed_load_clears_the_bar()
		{
			var gate = NewGate(hold: false);
			var bar = new BarActivityViewModel(new ManualDelay().Wait);
			bar.WatchLoop(gate);
			gate.Begin("KTLX", "x");
			Assert.True(bar.IsShown);
			gate.Abandon();
			Assert.False(bar.IsShown);
		}
	}
}
