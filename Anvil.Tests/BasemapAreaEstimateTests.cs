using System.Linq;
using Anvil.Models;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The offline-map size estimate (BasemapAreas.EstimateMegabytes): the areas + the world overview, LESS what two
	/// ticked areas share. Before the overlaps, Full detail of every area said "about 40 GB" for a file that came out
	/// 31.5 GB — Canada's box covers the northern Lower 48, and that strip was counted twice.
	/// </summary>
	public class BasemapAreaEstimateTests
	{
		private static double Sum(bool full, params BasemapArea[] areas) =>
			areas.Sum(a => full ? a.FullMegabytes : a.StandardMegabytes) + BasemapAreas.WorldOverviewMegabytes;

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void OneArea_IsItsCostPlusTheOverview(bool full)
		{
			foreach (var a in BasemapAreas.All)
				Assert.Equal(Sum(full, a), BasemapAreas.EstimateMegabytes(new[] { a }, full), 6);
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void Lower48AndCanada_CountTheSharedStripOnce(bool full)
		{
			var overlap = BasemapAreas.Overlaps.Single(o => o.Codes.Contains("l48") && o.Codes.Contains("ca"));
			var shared = full ? overlap.FullMegabytes : overlap.StandardMegabytes;
			Assert.True(shared > 0);
			Assert.Equal(Sum(full, BasemapAreas.Lower48, BasemapAreas.Canada) - shared,
				BasemapAreas.EstimateMegabytes(new[] { BasemapAreas.Lower48, BasemapAreas.Canada }, full), 6);
		}

		[Fact]
		public void AnOverlap_OnlyCountsWhenBothAreasAreTicked()
		{
			// Lower 48 + Alaska share nothing; Canada isn't ticked, so neither of its overlaps applies.
			Assert.Equal(Sum(true, BasemapAreas.Lower48, BasemapAreas.Alaska),
				BasemapAreas.EstimateMegabytes(new[] { BasemapAreas.Lower48, BasemapAreas.Alaska }, true), 6);
		}

		[Fact]
		public void EveryArea_AtFullDetail_MatchesTheMeasuredFile()
		{
			// The real all-areas Full download on the 2026-11-11 build: 31,470 MB on disk (header + directories + tiles).
			// The estimate counts tile data only, so it lands a little under; within 3 % is the bar.
			var estimate = BasemapAreas.EstimateMegabytes(BasemapAreas.All, true);
			Assert.InRange(estimate, 31470 * 0.97, 31470 * 1.03);
		}

		[Fact]
		public void Overlaps_NameRealAreas_InListOrder()
		{
			foreach (var o in BasemapAreas.Overlaps)
			{
				var a = BasemapAreas.ByCode(o.Codes[0]);
				var b = BasemapAreas.ByCode(o.Codes[1]);
				Assert.NotNull(a);
				Assert.NotNull(b);
				Assert.True(BasemapAreas.All.ToList().IndexOf(a!) < BasemapAreas.All.ToList().IndexOf(b!));
			}
		}
	}
}
