using System;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The one site-freshness rule (<see cref="RadarSiteStatus"/>) and the row's two labels. The labels are the
	/// regression: the NETWORK used to read "Operational" beside an Offline dot, a site read "Online" before
	/// anything had checked it, and PastCast's replay-day availability borrowed the live words.
	/// </summary>
	public class RadarSiteStatusTests
	{
		private static readonly DateTimeOffset Now = new(2026, 9, 15, 18, 44, 0, TimeSpan.Zero);

		[Fact]
		public void Fresh_AtTheThreshold() =>
			Assert.True(RadarSiteStatus.IsFresh(Now - RadarSiteStatus.Staleness, Now));

		[Fact]
		public void Stale_JustPastTheThreshold() =>
			Assert.False(RadarSiteStatus.IsFresh(Now - RadarSiteStatus.Staleness - TimeSpan.FromSeconds(1), Now));

		// The real case that raised this: KDFX's newest archive volume was 07:37Z at 18:44Z.
		[Fact]
		public void Kdfx_ElevenHoursStale_IsNotFresh() =>
			Assert.False(RadarSiteStatus.IsFresh(new DateTimeOffset(2026, 9, 15, 7, 37, 28, TimeSpan.Zero), Now));

		[Fact]
		public void Row_StartsUnknown_NotOnline_NotOffline()
		{
			var row = new RadarSiteRow(new RadarSite("KBUF", "Buffalo", 42.95, -78.74));
			Assert.Equal(SiteAvailability.Unknown, row.Availability);
			Assert.False(row.IsOffline);
			Assert.Equal("Checking…", row.StatusLabel);
		}

		[Theory]
		[InlineData(SiteAvailability.Online, false, "Online")]
		[InlineData(SiteAvailability.Offline, false, "Offline")]
		[InlineData(SiteAvailability.Online, true, "Data on replay day")]
		[InlineData(SiteAvailability.Offline, true, "No data on replay day")]
		public void Row_StatusLabel_NamesItsScope(SiteAvailability availability, bool replayDay, string expected)
		{
			var row = new RadarSiteRow(new RadarSite("KDFX", "Laughlin AFB", 29.273, -100.28));
			row.SetAvailability(availability, replayDay);
			Assert.Equal(expected, row.StatusLabel);
			Assert.Equal(availability == SiteAvailability.Offline, row.IsOffline);
		}

		[Fact]
		public void ClassLabel_IsTheNetwork_NotAStatus()
		{
			Assert.Equal("NEXRAD", new RadarSiteRow(new RadarSite("KDFX", "Laughlin AFB", 29.273, -100.28)).ClassLabel);
			Assert.Equal("TDWR", new RadarSiteRow(new RadarSite("TMCI", "Kansas City", 39.5, -94.74, RadarSiteClass.Tdwr)).ClassLabel);
		}
	}
}
