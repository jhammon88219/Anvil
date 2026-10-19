using System;
using System.Linq;
using Anvil.Models;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// PastCast's activity expander — what the LOADED EVENT downloaded (LAYER · RESULT · SOURCE, idea A): a layer row's
	/// states and Retry, the one summary line (both states), the rows' layer order, and the radar row's wording.
	/// </summary>
	public class PastActivityTests
	{
		private static PastLayerStatus Row(string id, string name) => new(id, name, "IEM");

		private static TemporalActivityViewModel Past(params PastLayerStatus[] rows) =>
			new(null, Array.Empty<FeedStatus>(), () => "hidden", _ => { }, pastLayers: rows);

		[Fact]
		public void A_row_reads_each_state()
		{
			var r = Row(LayerOrder.Reports, "Storm reports");
			Assert.Equal("—", r.ResultText);
			r.Loading();
			Assert.Equal(PastLayerState.Loading, r.State);
			r.Loaded("214 reports that day");
			Assert.True(r.IsIn);
			r.Empty();
			Assert.Equal("none in window", r.ResultText);
			Assert.True(r.IsIn);
			r.Failed("SPC didn't answer");
			Assert.StartsWith("failed ", r.ResultText);
			Assert.Equal("SPC didn't answer", r.Reason);
			Assert.False(r.IsIn);
			r.Idle();
			Assert.Equal(string.Empty, r.Reason); // a new state never keeps an old failure's words
		}

		[Fact]
		public void Retry_shows_only_on_a_failed_row_with_an_owner_and_runs_it()
		{
			var r = Row(LayerOrder.Damage, "Damage surveys");
			r.Failed(null);
			Assert.False(r.CanRetry); // no owner hook yet
			var runs = 0;
			r.OnRetry(() => runs++);
			Assert.True(r.CanRetry);
			r.Retry();
			Assert.Equal(1, runs);
			r.Loaded("12 tracks");
			Assert.False(r.CanRetry);
			r.Retry();
			Assert.Equal(1, runs); // a loaded row never refetches
		}

		[Fact]
		public void The_summary_follows_the_load()
		{
			var radar = Row(LayerOrder.Radar, "Radar");
			var damage = Row(LayerOrder.Damage, "Damage surveys");
			var cells = Row(LayerOrder.Cells, "Storm cells");
			var vm = Past(radar, damage, cells);
			Assert.Equal("Load an event to see what it downloads", vm.Summary);
			Assert.True(vm.HasContent);
			Assert.False(vm.HasFeedTable);

			radar.Loading("10 of 96 frames");
			damage.Loading();
			cells.Loaded("412 cells");
			Assert.Equal("Loading · 1 of 3 layers in", vm.Summary);

			radar.Loaded("96 frames");
			damage.Empty();
			Assert.Equal("All 3 layers loaded", vm.Summary);
			Assert.False(vm.SummaryHasFailure);

			damage.Failed(null);
			Assert.Equal("2 of 3 layers loaded · Damage surveys failed", vm.Summary);
			Assert.True(vm.SummaryHasFailure);

			cells.Failed(null);
			Assert.Equal("1 of 3 layers loaded · 2 failed: Damage surveys, Storm cells", vm.Summary);
		}

		[Fact]
		public void Not_shown_counts_as_in()
		{
			var outlook = Row(LayerOrder.Outlook, "SPC outlook");
			var reports = Row(LayerOrder.Reports, "Storm reports");
			var vm = Past(outlook, reports);
			outlook.Off();
			reports.Loaded("3 reports that day");
			Assert.Equal("All 2 layers loaded", vm.Summary);
		}

		[Fact]
		public void Rows_follow_pastcasts_layer_order_and_a_repeated_id_is_harmless()
		{
			var radar = Row(LayerOrder.Radar, "Radar");
			var cells = Row(LayerOrder.Cells, "Storm cells");
			var damage = Row(LayerOrder.Damage, "Damage surveys");
			var vm = Past(radar, cells, damage);
			vm.ApplyLayerOrder(new[] { LayerOrder.Damage, LayerOrder.Radar, LayerOrder.Damage, LayerOrder.Cells });
			Assert.Equal(new[] { damage, radar, cells }, vm.PastLayers);
		}

		[Fact]
		public void The_title_carries_the_event_and_when_it_finished()
		{
			var vm = Past(Row(LayerOrder.Radar, "Radar"));
			vm.SetEvent("KTLX · May 20, 2013 · 1:00 PM–3:00 PM", null);
			Assert.Equal(string.Empty, vm.LoadedText);
			vm.SetEvent("KTLX · May 20, 2013 · 1:00 PM–3:00 PM", DateTimeOffset.Now);
			Assert.StartsWith("loaded ", vm.LoadedText);
		}

		[Theory]
		[InlineData(0, 0, "finding volumes…")]
		[InlineData(61, 96, "61 of 96 frames")]
		public void Radar_row_counts_built_frames(int built, int total, string expected) =>
			Assert.Equal(expected, PastRadarLoadRow.LoadingText(built, total));

		[Fact]
		public void Counts_read_as_words()
		{
			Assert.Equal("1 report", PastLayerStatus.Count(1, "report", "reports"));
			Assert.Equal("214 reports", PastLayerStatus.Count(214, "report", "reports"));
		}

		[Fact]
		public void Nowcast_activity_has_no_past_table()
		{
			var vm = new TemporalActivityViewModel(null, new[] { new FeedStatus(LayerOrder.Warnings, "Warnings", null) },
				() => "hidden", _ => { });
			Assert.False(vm.HasPastLayers);
			Assert.True(vm.HasFeedTable);
			Assert.Equal("Warnings starting", vm.Summary); // one feed is named
		}
	}
}
