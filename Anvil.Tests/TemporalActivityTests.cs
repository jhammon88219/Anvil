using System;
using System.Linq;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The temporal window's ACTIVITY EXPANDER: the one tab's back-and-forth (Hidden → Minimal → Full → Minimal → Hidden),
	/// the per-window persistence, the feed rows' wording and their order (the window's layer order).
	/// </summary>
	public class TemporalActivityTests
	{
		private sealed class InlineDispatcher : IDispatcher
		{
			public void Post(Action action) => action();
		}

		private static readonly IDispatcher Inline = new InlineDispatcher();

		[Fact]
		public void Tab_goes_up_then_back_down_through_minimal()
		{
			var s = ActivityExpanderStates.Hidden;
			var seen = new[] { s }.ToList();
			for (var i = 0; i < 4; i++) { s = ActivityExpanderStates.Next(s); seen.Add(s); }
			Assert.Equal(new[] { "hidden", "min-up", "full", "min-down", "hidden" }, seen);
		}

		[Fact]
		public void Minimal_remembers_which_way_it_was_heading()
		{
			Assert.True(ActivityExpanderStates.OpensMore(ActivityExpanderStates.MinimalRising));   // ˄ More
			Assert.False(ActivityExpanderStates.OpensMore(ActivityExpanderStates.MinimalFalling)); // ˅ Less
			Assert.True(ActivityExpanderStates.IsMinimal(ActivityExpanderStates.MinimalRising));
			Assert.True(ActivityExpanderStates.IsMinimal(ActivityExpanderStates.MinimalFalling));
		}

		[Fact]
		public void Unknown_token_falls_back_to_hidden()
		{
			Assert.Equal(ActivityExpanderStates.Hidden, ActivityExpanderStates.Normalize("bogus"));
			Assert.Equal(ActivityExpanderStates.Hidden, ActivityExpanderStates.Normalize(null));
		}

		[Fact]
		public void Advance_persists_every_step_and_relabels_the_tab()
		{
			var saved = "full";
			var vm = new TemporalActivityViewModel(null, new[] { new FeedStatus(LayerOrder.Warnings, "Warnings", Inline) },
				() => saved, t => saved = t, Inline);
			Assert.True(vm.IsFull);
			Assert.Equal("Less", vm.TabLabel);

			vm.Advance();
			Assert.Equal("min-down", saved);
			Assert.True(vm.IsMinimal);
			Assert.Equal("Less", vm.TabLabel);

			vm.Advance();
			Assert.Equal("hidden", saved);
			Assert.Equal("More", vm.TabLabel);
		}

		[Fact]
		public void Feeds_follow_the_layer_order()
		{
			var w = new FeedStatus(LayerOrder.Warnings, "Warnings", Inline);
			var r = new FeedStatus(LayerOrder.Reports, "Storm reports", Inline);
			var c = new FeedStatus(LayerOrder.Cells, "Storm cells", Inline);
			var vm = new TemporalActivityViewModel(null, new[] { w, r, c }, () => "hidden", _ => { }, Inline);

			vm.ApplyLayerOrder(new[] { LayerOrder.Cells, LayerOrder.Radar, LayerOrder.Warnings, LayerOrder.Reports });

			Assert.Equal(new[] { c, w, r }, vm.Feeds);
		}

		[Fact]
		public void A_mode_without_activity_has_no_content()
		{
			var vm = new TemporalActivityViewModel(null, Array.Empty<FeedStatus>(), () => "hidden", _ => { }, Inline);
			Assert.False(vm.HasContent);
		}

		[Fact]
		public void Feed_text_tracks_check_success_and_failure()
		{
			var f = new FeedStatus(LayerOrder.Watches, "Watches", Inline);
			Assert.Equal("—", f.UpdatedText);
			Assert.Equal(string.Empty, f.CountdownText);

			f.Scheduled(TimeSpan.FromMinutes(2));
			Assert.Equal("every 2 min", f.Cadence);
			Assert.StartsWith("in ", f.CountdownText);

			f.Checking();
			Assert.Equal("Checking", f.UpdatedText);
			Assert.Equal(string.Empty, f.CountdownText); // no countdown while it checks

			f.Succeeded();
			Assert.Equal(FeedState.Ok, f.State);
			Assert.NotNull(f.LastUpdated);
			Assert.Contains(":", f.UpdatedText);

			f.Failed();
			Assert.StartsWith("Failed ", f.UpdatedText);
			Assert.StartsWith("retry ", f.CountdownText);
		}

		[Theory]
		[InlineData(15, "every 15 s")]
		[InlineData(60, "every 60 s")]
		[InlineData(120, "every 2 min")]
		[InlineData(900, "every 15 min")]
		public void Cadence_words(int seconds, string expected) =>
			Assert.Equal(expected, FeedStatus.CadenceWords(TimeSpan.FromSeconds(seconds)));
	}
}
