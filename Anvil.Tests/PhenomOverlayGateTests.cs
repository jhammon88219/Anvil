using System.Collections.Generic;
using System.Threading.Tasks;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The watches/warnings draw gate: ticks mean "show while NowCast runs", so the map launches clean with
	/// every box ticked, and the header select-all only ever writes the ticks.
	/// </summary>
	public class PhenomOverlayGateTests
	{
		private class FakeOverlay : PhenomOverlayViewModel
		{
			public readonly List<bool> VisiblePushes = new();
			public readonly List<(bool Tornado, bool Severe)> KindPushes = new();
			public readonly List<bool> FlashFloodPushes = new();
			protected override string SourceUrl => "https://test/x.geojson";
			protected override string ItemNounSingular => "warning";
			protected override string ItemNounPlural => "warnings";
			protected override Task SetVisibleAsync(bool visible) { VisiblePushes.Add(visible); return Task.CompletedTask; }
			protected override Task SetOpacityAsync(double opacity) => Task.CompletedTask;
			protected override Task SetSourceAsync(string url) => Task.CompletedTask;
			protected override Task SetKindsAsync(bool tornado, bool severe, bool flashFlood)
			{
				KindPushes.Add((tornado, severe));
				FlashFloodPushes.Add(flashFlood);
				return Task.CompletedTask;
			}
		}

		// The warnings shape: a third FF type.
		private sealed class FakeFloodOverlay : FakeOverlay
		{
			public override bool SupportsFlashFlood => true;
		}

		[Fact]
		public async Task NoFlashFloodType_FfTickIgnored_NeverPushed()
		{
			var vm = new FakeOverlay { IsModeActive = true };
			await vm.OnMapsReadyAsync();

			vm.ShowTornado = false;
			vm.ShowSevere = false;

			Assert.True(vm.ShowFlashFlood);   // the (unused) field keeps its default…
			Assert.False(vm.AnyShown);        // …but counts for nothing on a watch-shaped overlay
			Assert.False(vm.AllShown);
			Assert.False(vm.IsVisible);
			Assert.DoesNotContain(true, vm.FlashFloodPushes);
		}

		[Fact]
		public async Task FlashFloodOnly_KeepsLayerDrawn_AndFooterNamesIt()
		{
			var vm = new FakeFloodOverlay { IsModeActive = true };
			await vm.OnMapsReadyAsync();

			vm.ShowTornado = false;
			vm.ShowSevere = false;

			Assert.True(vm.AnyShown);
			Assert.Null(vm.AllShown);
			Assert.True(vm.IsVisible);
			Assert.True(vm.FlashFloodPushes[^1]);
			Assert.Equal("Flash flood only", vm.CardFooter);

			vm.ShowTornado = true;
			Assert.Equal("Tornado and flash flood only", vm.CardFooter);
		}

		[Fact]
		public async Task FlashFloodOverlay_ToggleAll_CoversAllThree()
		{
			var vm = new FakeFloodOverlay { IsModeActive = true };
			await vm.OnMapsReadyAsync();

			vm.ShowFlashFlood = false;
			Assert.Null(vm.AllShown);          // TO+SV alone is not "all" when FF exists

			vm.ToggleAll();
			Assert.True(vm.ShowFlashFlood);
			Assert.True(vm.AllShown);
			Assert.Equal(string.Empty, vm.CardFooter);

			vm.ToggleAll();
			Assert.False(vm.ShowFlashFlood);
			Assert.False(vm.IsVisible);
		}

		[Fact]
		public async Task Launch_EverythingTicked_NothingDrawn()
		{
			var vm = new FakeOverlay();
			await vm.OnMapsReadyAsync();

			Assert.True(vm.ShowTornado);
			Assert.True(vm.ShowSevere);
			Assert.True(vm.AllShown);
			Assert.False(vm.IsVisible);
			Assert.Equal(new[] { false }, vm.VisiblePushes);
		}

		[Fact]
		public async Task ModeOffThenOn_TicksSurvive_AndLayerReturns()
		{
			var vm = new FakeOverlay();
			await vm.OnMapsReadyAsync();

			vm.IsModeActive = true;
			Assert.True(vm.IsVisible);

			vm.IsModeActive = false; // e.g. entering PastCast
			Assert.False(vm.IsVisible);
			Assert.True(vm.ShowTornado);
			Assert.True(vm.ShowSevere);

			vm.IsModeActive = true;
			Assert.True(vm.IsVisible);
		}

		[Fact]
		public async Task ToggleAll_PartialGoesAllOn_AllOnGoesAllOff()
		{
			var vm = new FakeOverlay { IsModeActive = true };
			await vm.OnMapsReadyAsync();

			vm.ShowSevere = false;
			Assert.Null(vm.AllShown);

			vm.KindPushes.Clear();
			vm.ToggleAll();
			Assert.True(vm.AllShown);
			Assert.Equal(new[] { (true, true) }, vm.KindPushes); // one push, never a half-applied filter
			Assert.True(vm.IsVisible);

			vm.ToggleAll();
			Assert.False(vm.AllShown);
			Assert.False(vm.IsVisible);
		}

		[Fact]
		public async Task ModeActiveBeforeMapReady_DrawnAtReady()
		{
			var vm = new FakeOverlay { IsModeActive = true };
			await vm.OnMapsReadyAsync();

			Assert.True(vm.IsVisible);
			Assert.Equal(new[] { true }, vm.VisiblePushes);
		}
	}
}
