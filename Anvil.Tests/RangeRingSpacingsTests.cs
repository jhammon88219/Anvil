using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The distance rings' spacing is persisted as a VALUE; a hand-edited or retired value must fall back to Auto
	/// rather than draw rings at a step the picker can't show.
	/// </summary>
	public class RangeRingSpacingsTests
	{
		[Theory]
		[InlineData(0, 0)]
		[InlineData(25, 25)]
		[InlineData(100, 100)]
		[InlineData(30, RangeRingSpacings.Auto)]
		[InlineData(-5, RangeRingSpacings.Auto)]
		public void Normalize(int stored, int expected) => Assert.Equal(expected, RangeRingSpacings.Normalize(stored));

		[Fact]
		public void Index_RoundTrips_AndClamps()
		{
			for (var i = 0; i < RangeRingSpacings.All.Count; i++)
			{
				Assert.Equal(i, RangeRingSpacings.IndexOf(RangeRingSpacings.FromIndex(i)));
			}
			Assert.Equal(RangeRingSpacings.Auto, RangeRingSpacings.FromIndex(99));
			Assert.Equal(RangeRingSpacings.Labels.Count, RangeRingSpacings.All.Count);
		}

		[Fact]
		public void Settings_Defaults_AreOutlinePlusVelocity_AutoSpacing()
		{
			var s = new AppSettings();
			Assert.True(s.ShowReflectivityRing);
			Assert.True(s.ShowVelocityRing);
			Assert.False(s.ShowDistanceRings);
			Assert.Equal(RangeRingSpacings.Auto, s.DistanceRingSpacing);

			s.DistanceRingSpacing = 37; // not a picker value
			Assert.Equal(RangeRingSpacings.Auto, s.DistanceRingSpacing);
		}
	}
}
