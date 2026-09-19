using Anvil.Models;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The Radar Atlas's place filters rest entirely on one generated field (<see cref="RadarSite.State"/>,
	/// stamped in by <c>tools/make_site_states.py</c>) and one mapping off it. This guards the mapping and
	/// the row label that reads it.
	///
	/// <para>⚠️ The DATA is guarded by the generator, not here: <c>py -3 tools/make_site_states.py --check</c>
	/// re-resolves every bundled site and exits non-zero if any can't be placed — the same regenerate-and-check
	/// pattern the ramp and saved-event catalogs use. The site files live in Anvil.App, which this project
	/// deliberately can't see.</para>
	/// </summary>
	public class RadarSiteRegionTests
	{
		// The five OCONUS buckets, each with the case that drove it into existence.
		[Theory]
		[InlineData("AK", RadarSiteRegion.Alaska)]
		[InlineData("HI", RadarSiteRegion.Hawaii)]
		[InlineData("PR", RadarSiteRegion.Caribbean)]
		[InlineData("VI", RadarSiteRegion.Caribbean)]
		[InlineData("GU", RadarSiteRegion.Pacific)]
		[InlineData("JP", RadarSiteRegion.Pacific)]   // Kadena AB, Okinawa
		[InlineData("KR", RadarSiteRegion.Pacific)]   // Kunsan / Camp Humphreys
		[InlineData("PT", RadarSiteRegion.Atlantic)]  // Lajes Field, Azores
		public void OconusStates_MapToTheirOwnRegion(string state, RadarSiteRegion expected) =>
			Assert.Equal(expected, RadarSiteRegions.For(state));

		// The 48 + DC are the DEFAULT, so a new state never needs a code change.
		[Theory]
		[InlineData("OK")]
		[InlineData("FL")]
		[InlineData("DC")]
		public void AnyOtherState_IsConus(string state) =>
			Assert.Equal(RadarSiteRegion.Conus, RadarSiteRegions.For(state));

		// A site the generator couldn't place has no region either, and simply misses both filters —
		// it must never fall into CONUS by accident.
		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("   ")]
		public void NoState_HasNoRegion(string? state) =>
			Assert.Null(RadarSiteRegions.For(state));

		[Fact]
		public void StateCode_IsCaseAndWhitespaceInsensitive() =>
			Assert.Equal(RadarSiteRegion.Alaska, RadarSiteRegions.For(" ak "));

		// CONUS is an initialism; the others are words. The filter shows these verbatim.
		[Fact]
		public void ConusDisplayName_StaysUpperCase() =>
			Assert.Equal("CONUS", RadarSiteRegions.DisplayName(RadarSiteRegion.Conus));

		[Fact]
		public void Row_AppendsTheStateToTheName() =>
			Assert.Equal("Norman · OK",
				new RadarSiteRow(new RadarSite("KTLX", "Norman", 35.333, -97.278, State: "OK")).NameAndState);

		// No state, no separator — a trailing " · " would read as missing data.
		[Fact]
		public void Row_WithoutAState_IsJustTheName() =>
			Assert.Equal("Norman",
				new RadarSiteRow(new RadarSite("KTLX", "Norman", 35.333, -97.278)).NameAndState);

		[Fact]
		public void Row_ExposesTheRegionItsStateImplies() =>
			Assert.Equal(RadarSiteRegion.Caribbean,
				new RadarSiteRow(new RadarSite("TJUA", "San Juan", 18.116, -66.078, State: "PR")).Region);
	}
}
