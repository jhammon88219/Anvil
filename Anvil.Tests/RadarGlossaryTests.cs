using System;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The glossary's "now" lines — the only part of a hint computed from live data, and the part that can
	/// quietly start lying. The freshness sentences must keep naming the SAME thresholds the app grades by
	/// (<see cref="RadarSiteStatus.Staleness"/> and <see cref="RadarGlossary.RecentKnee"/>).
	/// </summary>
	public class RadarGlossaryTests
	{
		[Fact]
		public void DataAge_FreshReadsAsNormal() =>
			Assert.Contains("arriving normally", RadarGlossary.DataAge(TimeSpan.FromMinutes(5)).Now);

		[Fact]
		public void DataAge_PastTheKneeIsLateButStillOnline()
		{
			var now = RadarGlossary.DataAge(RadarGlossary.RecentKnee + TimeSpan.FromMinutes(1)).Now;
			Assert.Contains("later than usual", now);
			Assert.Contains("still counted as online", now);
		}

		[Fact]
		public void DataAge_PastStalenessReadsOffline() =>
			Assert.Contains("offline", RadarGlossary.DataAge(RadarSiteStatus.Staleness + TimeSpan.FromMinutes(1)).Now);

		[Fact]
		public void DataAge_AtTheThresholdsKeepsTheKinderWording()
		{
			Assert.Contains("arriving normally", RadarGlossary.DataAge(RadarGlossary.RecentKnee).Now);
			Assert.Contains("still counted as online", RadarGlossary.DataAge(RadarSiteStatus.Staleness).Now);
		}

		[Fact]
		public void DataAge_ContextNamesTheRealThresholds()
		{
			var context = RadarGlossary.DataAge(null).Context;
			Assert.Contains($"{RadarGlossary.RecentKnee.TotalMinutes:0} minutes", context);
			Assert.Contains($"{RadarSiteStatus.Staleness.TotalMinutes:0}", context);
		}

		[Fact]
		public void DataAge_NoScanSaysSo() =>
			Assert.Contains("No scan time", RadarGlossary.DataAge(null).Now);

		[Theory]
		[InlineData("VCP 35 · clear-air", "Clear-air mode")]
		[InlineData("VCP 212 · precip · SAILS/MRLE ×1", "Precipitation mode")]
		[InlineData("—", "hasn't reported")]
		[InlineData("", "hasn't reported")]
		public void ScanPattern_ReadsTheRegime(string mode, string expected) =>
			Assert.Contains(expected, RadarGlossary.ScanPattern(mode).Now);

		[Theory]
		[InlineData("VCP 212 · precip", "Volume coverage pattern 212")]
		[InlineData("VCP ?", "Volume coverage pattern")]
		public void ScanPattern_TechnicalLineCarriesTheNumber(string mode, string expected) =>
			Assert.Equal(expected, RadarGlossary.ScanPattern(mode).Technical);

		[Theory]
		[InlineData(20.0, "still low over your area")]
		[InlineData(100.0, "few thousand feet up")]
		[InlineData(220.0, "edge of useful range")]
		public void Distance_ReadsTheRange(double miles, string expected) =>
			Assert.Contains(expected, RadarGlossary.Distance(miles).Now);

		[Fact]
		public void Distance_WithoutAMarkerAsksForOne() =>
			Assert.Contains("location marker", RadarGlossary.Distance(null).Now);

		[Fact]
		public void Status_ReplayDayNeverBorrowsLiveWords()
		{
			var replay = RadarGlossary.Status(SiteAvailability.Offline, replayDay: true).Now;
			Assert.Contains("replay day", replay);
			Assert.DoesNotContain("maintenance", replay);
		}

		[Fact]
		public void Status_UncheckedIsNotOffline() =>
			Assert.Contains("Not checked yet", RadarGlossary.Status(SiteAvailability.Unknown, replayDay: false).Now);

		[Theory]
		[InlineData(RadarSiteClass.Operational, "NEXRAD")]
		[InlineData(RadarSiteClass.Tdwr, "Terminal Doppler")]
		[InlineData(RadarSiteClass.Research, "Research")]
		public void Network_NamesTheNetwork(RadarSiteClass siteClass, string expected) =>
			Assert.Contains(expected, RadarGlossary.Network(siteClass).Technical);

		[Fact]
		public void StaticCards_HaveNoNowBlock()
		{
			Assert.Equal(string.Empty, RadarGlossary.Coordinates().Now);
			Assert.Equal(string.Empty, RadarGlossary.Network(RadarSiteClass.Operational).Now);
		}
	}
}
