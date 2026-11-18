using System;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>Dev → Live source timing's matcher (Services/LiveFeed/LiveSourceTiming): the same product on IEM and Weather
	/// Wire paired by office · AWIPS id · issue minute, and the lead measured from the arrival stamps.</summary>
	public class LiveSourceTimingTests
	{
		private static readonly DateTimeOffset Issued = new(2026, 11, 18, 17, 4, 0, TimeSpan.Zero);

		private static LiveProduct P(string office, string awips, DateTimeOffset issued) =>
			new($"{office}-{awips}", awips, office, issued, issued);

		[Fact]
		public void The_same_product_on_both_is_paired_and_the_lead_is_IEM_minus_Weather_Wire()
		{
			var t = new LiveSourceTiming();
			t.Heard(TimedSource.Nwws, P("KOUN", "SVROUN", Issued), Issued.AddSeconds(4));
			t.Heard(TimedSource.Iem, P("KOUN", "SVROUN", Issued), Issued.AddSeconds(19));

			var s = t.Summary();
			Assert.Equal(1, s.Pairs);
			Assert.Equal(15, s.MedianLeadSeconds);   // Weather Wire 15 s sooner → positive
			Assert.Equal(1.0, s.NwwsFirstShare);
			Assert.Equal(19, s.MedianIemLagSeconds);
			Assert.Equal(4, s.MedianNwwsLagSeconds);
		}

		[Fact]
		public void IEM_first_gives_a_negative_lead()
		{
			var t = new LiveSourceTiming();
			t.Heard(TimedSource.Iem, P("KOUN", "SVROUN", Issued), Issued.AddSeconds(5));
			t.Heard(TimedSource.Nwws, P("KOUN", "SVROUN", Issued), Issued.AddSeconds(8));
			Assert.Equal(-3, t.Summary().MedianLeadSeconds);
			Assert.Equal(0.0, t.Summary().NwwsFirstShare);
		}

		[Theory]
		[InlineData(1)]   // the second arrival is stamped a minute LATER than the first
		[InlineData(-1)]  // … or a minute EARLIER
		public void Issue_stamps_a_minute_apart_still_pair(int minutes)
		{
			var t = new LiveSourceTiming();
			t.Heard(TimedSource.Nwws, P("KOUN", "SVROUN", Issued), Issued.AddSeconds(30));
			t.Heard(TimedSource.Iem, P("KOUN", "SVROUN", Issued.AddMinutes(minutes)), Issued.AddSeconds(50));
			Assert.Equal(1, t.Summary().Pairs);
		}

		[Fact]
		public void Different_products_and_different_offices_never_pair()
		{
			var t = new LiveSourceTiming();
			t.Heard(TimedSource.Nwws, P("KOUN", "SVROUN", Issued), Issued.AddSeconds(3));
			t.Heard(TimedSource.Iem, P("KOUN", "TOROUN", Issued), Issued.AddSeconds(9));
			t.Heard(TimedSource.Iem, P("KTSA", "SVROUN", Issued), Issued.AddSeconds(9));
			t.Heard(TimedSource.Iem, P("KOUN", "SVROUN", Issued.AddMinutes(5)), Issued.AddMinutes(5));
			Assert.Equal(0, t.Summary().Pairs);
		}

		[Fact]
		public void A_repeat_of_a_product_counts_once()
		{
			var t = new LiveSourceTiming();
			t.Heard(TimedSource.Nwws, P("KOUN", "SVROUN", Issued), Issued.AddSeconds(3));
			t.Heard(TimedSource.Nwws, P("KOUN", "SVROUN", Issued), Issued.AddSeconds(7)); // a re-send
			t.Heard(TimedSource.Iem, P("KOUN", "SVROUN", Issued), Issued.AddSeconds(18));
			t.Heard(TimedSource.Iem, P("KOUN", "SVROUN", Issued), Issued.AddSeconds(25)); // IEM's correction, same key
			var s = t.Summary();
			Assert.Equal(1, s.Pairs);
			Assert.Equal(15, s.MedianLeadSeconds); // from the FIRST arrivals
		}

		[Fact]
		public void Percentile_is_nearest_rank_on_a_sorted_array()
		{
			var sorted = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
			Assert.Equal(6, LiveSourceTiming.Percentile(sorted, 0.5));
			Assert.Equal(2, LiveSourceTiming.Percentile(sorted, 0.1));
			Assert.Equal(10, LiveSourceTiming.Percentile(sorted, 0.9));
			Assert.Null(LiveSourceTiming.Percentile(Array.Empty<double>(), 0.5));
		}

		[Fact]
		public void The_card_says_so_until_a_product_is_heard_on_both()
		{
			var text = LiveSourceTimingViewModel.Describe(new LiveSourceTiming().Summary());
			Assert.Contains("0 matched", text);
			Assert.Contains("No product heard on both yet", text);
		}
	}
}
