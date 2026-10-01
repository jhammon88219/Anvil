using System.Threading.Tasks;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;
using static Anvil.Tests.TemporalWindowPersistenceTests;

namespace Anvil.Tests
{
	/// <summary>
	/// The loop holding gate's state machine (<see cref="LoopHoldingGateViewModel"/>): it raises on a load, reports
	/// only once the load's loop has begun, releases on Complete, offers the escape through a confirm, and a Cancel
	/// owns the gate until its popup is acknowledged — a load that bails meanwhile can't hide it.
	/// </summary>
	public class LoopHoldingGateTests
	{
		private static (LoopHoldingGateViewModel Gate, AppSettings Settings, int[] Cancels) NewGate(bool hold = true)
		{
			var settings = new AppSettings { HoldPastCastLoads = hold };
			var svc = Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			var cancels = new int[1];
			var gate = new LoopHoldingGateViewModel(svc, () => { cancels[0]++; return Task.CompletedTask; });
			return (gate, settings, cancels);
		}

		[Fact]
		public void Begin_raises_the_gate_only_while_holding_is_on()
		{
			var (on, _, _) = NewGate(hold: true);
			on.Begin("KTBW", "KTBW · Sep 28, 2022");
			Assert.True(on.IsShown);
			Assert.Equal("Finding volumes…", on.DownloadedText);

			var (off, _, _) = NewGate(hold: false);
			off.Begin("KTBW", "KTBW · Sep 28, 2022");
			Assert.False(off.IsShown);
		}

		[Fact]
		public void Progress_and_release_wait_for_the_loop_to_begin()
		{
			var (gate, _, _) = NewGate();
			gate.Begin("KTBW", "x");

			gate.Report(39, 22, 14); // a previous loop's straggler — before Arm
			gate.Complete();
			Assert.True(gate.IsShown);
			Assert.Equal(0, gate.Total);

			gate.Arm();
			gate.Report(39, 22, 14);
			Assert.Equal("Downloaded 22 of 39", gate.DownloadedText);
			Assert.Equal("14 of 39 frames built", gate.BuiltText);
			Assert.Equal(14.0 / 39, gate.BuiltFraction, 6);

			gate.Report(39, 39, 39);
			Assert.Equal("All downloaded", gate.DownloadedText);
			gate.Complete();
			Assert.False(gate.IsShown);
		}

		[Fact]
		public void The_escape_goes_through_a_confirm_and_can_turn_holding_off()
		{
			var (gate, settings, _) = NewGate();
			gate.Begin("KTBW", "x");
			gate.Arm();

			gate.RequestEscape();
			Assert.Equal(LoopGateState.ConfirmingEscape, gate.State);
			gate.KeepWaiting();
			Assert.Equal(LoopGateState.Holding, gate.State);

			gate.RequestEscape();
			gate.NeverHoldAgain = true;
			gate.UseMap();
			Assert.False(gate.IsShown);
			Assert.False(settings.HoldPastCastLoads);

			gate.Begin("KTBW", "next load"); // holding is off now: the next load is not held
			Assert.False(gate.IsShown);
		}

		[Fact]
		public void A_one_time_escape_leaves_holding_on_and_resets_for_the_next_load()
		{
			var (gate, settings, _) = NewGate();
			gate.Begin("KTBW", "x");
			gate.RequestEscape();
			gate.UseMap();
			Assert.False(gate.IsShown);
			Assert.True(settings.HoldPastCastLoads);

			gate.Begin("KTBW", "y");
			Assert.True(gate.IsShown);
			Assert.False(gate.NeverHoldAgain);
		}

		[Fact]
		public async Task Cancel_owns_the_gate_until_its_popup_is_acknowledged()
		{
			var (gate, _, cancels) = NewGate();
			gate.Begin("KTBW", "x");
			gate.Arm();
			gate.Report(39, 22, 14);

			await gate.CancelLoadAsync();
			Assert.Equal(1, cancels[0]);
			Assert.Equal(LoopGateState.Cancelling, gate.State);
			Assert.False(gate.AreActionsEnabled);

			gate.Abandon();   // the load unwinding returns false — must not hide a cancel in progress
			gate.Complete();
			Assert.Equal(LoopGateState.Cancelling, gate.State);

			gate.ShowCancelled(14, 39);
			Assert.True(gate.IsCancelledShown);
			Assert.False(gate.IsProgressShown);
			Assert.Contains("14 of 39 frames", gate.KeptText);
			Assert.Contains("Load again to resume", gate.KeptText);

			gate.AcknowledgeCancelled();
			Assert.False(gate.IsShown);
		}

		[Fact]
		public void Cancelling_with_nothing_kept_says_so()
		{
			var (gate, _, _) = NewGate();
			gate.Begin("KTBW", "x");
			gate.ShowCancelled(0, 39);
			Assert.Contains("nothing was kept", gate.KeptText);
			Assert.Contains("Load again to resume", gate.KeptText);
		}

		[Fact]
		public void A_failed_load_or_a_site_change_drops_the_gate()
		{
			var (gate, _, _) = NewGate();
			gate.Begin("KTBW", "x");
			gate.Abandon();
			Assert.False(gate.IsShown);

			gate.Begin("KTBW", "y");
			gate.RequestEscape();
			gate.Dismiss();
			Assert.False(gate.IsShown);
		}
	}
}
