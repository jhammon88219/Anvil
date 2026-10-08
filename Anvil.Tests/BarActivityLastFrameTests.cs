using System;
using Anvil.Models;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>The bar's idle "Last frame" line: paint time against the SAME due time the idle line showed
	/// (predicted end + the planner's upload slack), regime-aware drawn frames only.</summary>
	public class BarActivityLastFrameTests
	{
		private static readonly DateTimeOffset End = new(2026, 10, 8, 21, 42, 41, 800, TimeSpan.Zero);

		private static LivePollTimingRecord Frame(double paintedAfterEndSec, string mode = "regime", bool drawn = true) =>
			new("frame", End, "KEVX", 112, null, mode, PredictedEndUtc: End,
				PaintedUtc: End.AddSeconds(paintedAfterEndSec), Drawn: drawn);

		[Fact]
		public void Delta_is_measured_from_the_due_time_shown_not_the_predicted_end()
		{
			// KEVX 4:42 PM: end 21:42:41.8, due 21:42:44.8 (+3 s slack), painted 21:42:47.8 → 3.0 s after due.
			Assert.Equal("Last frame: on screen 3.0 s after due", BarActivityViewModel.LastFrameWords(Frame(6.0)));
		}

		[Fact]
		public void Early_paint_reads_before_due()
		{
			Assert.Equal("Last frame: on screen 1.0 s before due", BarActivityViewModel.LastFrameWords(Frame(2.0)));
		}

		[Fact]
		public void Hidden_on_fixed_time_polling_or_an_undrawn_frame()
		{
			Assert.Equal(string.Empty, BarActivityViewModel.LastFrameWords(Frame(6.0, mode: "fixed")));
			Assert.Equal(string.Empty, BarActivityViewModel.LastFrameWords(Frame(6.0, drawn: false)));
		}
	}
}
