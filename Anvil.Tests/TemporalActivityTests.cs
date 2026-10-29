using System;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The temporal pane's ACTIVITY EXPANDER: the one tab's two states (Hidden ⇄ Full), the per-window persistence, the
	/// FEED table's wording (clock times, never countdowns), the collapsed summary line, and the rows' layer order.
	/// </summary>
	public class TemporalActivityTests
	{
		private sealed class InlineDispatcher : IDispatcher
		{
			public void Post(Action action) => action();
		}

		private static readonly IDispatcher Inline = new InlineDispatcher();

		private static FeedStatus Feed(string id, string name) => new(id, name, Inline);

		[Fact]
		public void Tab_toggles_between_two_states()
		{
			Assert.Equal(ActivityExpanderStates.Full, ActivityExpanderStates.Next(ActivityExpanderStates.Hidden));
			Assert.Equal(ActivityExpanderStates.Hidden, ActivityExpanderStates.Next(ActivityExpanderStates.Full));
		}

		[Theory]
		[InlineData("min-up", "full")]   // the retired Minimal tokens read as Full
		[InlineData("min-down", "full")]
		[InlineData("bogus", "hidden")]
		[InlineData(null, "hidden")]
		public void Tokens_normalize(string? token, string expected) =>
			Assert.Equal(expected, ActivityExpanderStates.Normalize(token));

		[Fact]
		public void Advance_persists_and_relabels_the_tab()
		{
			var saved = "hidden";
			var vm = new TemporalActivityViewModel(null, new[] { Feed(LayerOrder.Warnings, "Warnings") }, () => saved, t => saved = t);
			Assert.True(vm.IsHidden);
			Assert.Equal("More", vm.TabLabel);

			vm.Advance();
			Assert.Equal("full", saved);
			Assert.True(vm.IsFull);
			Assert.Equal("Less", vm.TabLabel);

			vm.Advance();
			Assert.Equal("hidden", saved);
		}

		[Fact]
		public void Feeds_follow_the_layer_order()
		{
			var w = Feed(LayerOrder.Warnings, "Warnings");
			var r = Feed(LayerOrder.Reports, "Storm reports");
			var c = Feed(LayerOrder.Cells, "Storm cells");
			var vm = new TemporalActivityViewModel(null, new[] { w, r, c }, () => "hidden", _ => { });

			vm.ApplyLayerOrder(new[] { LayerOrder.Cells, LayerOrder.Radar, LayerOrder.Warnings, LayerOrder.Reports });

			Assert.Equal(new[] { c, w, r }, vm.Feeds);
		}

		[Fact]
		public void A_mode_without_activity_has_no_content()
		{
			var vm = new TemporalActivityViewModel(null, Array.Empty<FeedStatus>(), () => "hidden", _ => { });
			Assert.False(vm.HasContent);
		}

		[Fact]
		public void Feed_columns_are_clock_times_never_countdowns()
		{
			var f = Feed(LayerOrder.Watches, "Watches");
			Assert.Equal("—", f.LastText);
			Assert.Equal("—", f.NextText);

			f.Scheduled(TimeSpan.FromMinutes(2));
			Assert.Equal("2 min", f.Every);
			Assert.Matches(@"^\d{1,2}:\d\d:\d\d", f.NextText);   // a clock time, not "in 1:59"

			f.Checking();
			Assert.Equal("Checking", f.LastText);

			f.Succeeded();
			Assert.Equal(FeedState.Ok, f.State);
			Assert.Matches(@"^\d{1,2}:\d\d:\d\d", f.LastText);

			f.Failed();
			Assert.StartsWith("failed ", f.LastText);
		}

		[Fact]
		public void Summary_names_only_a_feed_in_trouble()
		{
			var w = Feed(LayerOrder.Warnings, "Warnings");
			var t = Feed(LayerOrder.Tropical, "Tropical");
			var vm = new TemporalActivityViewModel(null, new[] { w, t }, () => "hidden", _ => { });
			Assert.Equal("Feeds starting", vm.Summary);

			w.Succeeded();
			Assert.StartsWith("1 of 2 feeds current · last ", vm.Summary);

			t.Succeeded();
			Assert.StartsWith("All 2 feeds current · last ", vm.Summary);
			Assert.False(vm.SummaryHasFailure);

			t.Failed();
			Assert.StartsWith("Tropical failed at ", vm.Summary);
			Assert.True(vm.SummaryHasFailure);

			w.Failed();
			Assert.Equal("2 feeds failed: Warnings, Tropical", vm.Summary);
		}

		[Theory]
		[InlineData(15, "15 s")]
		[InlineData(60, "60 s")]
		[InlineData(120, "2 min")]
		[InlineData(300, "5 min")]
		public void Every_words(int seconds, string expected) =>
			Assert.Equal(expected, FeedStatus.EveryWords(TimeSpan.FromSeconds(seconds)));
	}
}
