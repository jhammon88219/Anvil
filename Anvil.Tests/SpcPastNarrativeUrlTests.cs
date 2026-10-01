using System;
using System.Linq;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="SpcOutlookService.PastNarrativeUrls"/>: a historical issuance → SPC's archive page(s). The page is
	/// named by the ISSUANCE's UTC date, so Day 1's 01Z and every Day 2/3 cycle land off the valid day. The
	/// Moore (May 20, 2013) names below were checked against the live archive.
	/// </summary>
	public class SpcPastNarrativeUrlTests
	{
		private const string Archive = "https://www.spc.noaa.gov/products/outlook/archive/2013/";
		private static readonly DateOnly Moore = new(2013, 5, 20);

		[Fact]
		public void Day1_1630_IsOnTheValidDay() =>
			Assert.Equal(Archive + "day1otlk_20130520_1630.html", SpcOutlookService.PastNarrativeUrls(Moore, 1, 16).Single());

		[Fact]
		public void Day1_01Z_IsTheNextUtcDay() =>
			Assert.Equal(Archive + "day1otlk_20130521_0100.html", SpcOutlookService.PastNarrativeUrls(Moore, 1, 1).Single());

		[Fact]
		public void Day2_Afternoon_IsTheDayBefore_1730First() =>
			Assert.Equal(Archive + "day2otlk_20130519_1730.html", SpcOutlookService.PastNarrativeUrls(Moore, 2, 17).First());

		[Fact]
		public void Day3_Morning_IsTwoDaysBefore_AndTriesTheOldStamp() =>
			Assert.Contains(Archive + "day3otlk_20130518_0730.html", SpcOutlookService.PastNarrativeUrls(Moore, 3, 8));

		[Fact]
		public void UnknownCycle_HasNoPage() => Assert.Empty(SpcOutlookService.PastNarrativeUrls(Moore, 1, 99));
	}
}
