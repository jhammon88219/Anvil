using System;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Retired radar ids (moved/renamed: KLIX → KHDC on 2023-11-27) — hidden live, replayable only for windows
	/// that start on or before the last day the id has data. <see cref="RadarSiteEra"/> is the one rule.
	/// </summary>
	public class RadarSiteEraTests
	{
		private static readonly RadarSite Klix = new("KLIX", "Slidell", 30.3367, -89.8253, RetiredOn: new DateOnly(2023, 11, 27));
		private static readonly RadarSite Khdc = new("KHDC", "Hammond", 30.5193, -90.4074);

		private static DateTimeOffset Utc(int y, int m, int d, int h = 0) => new(y, m, d, h, 0, 0, TimeSpan.Zero);

		[Fact]
		public void WorkingSite_AlwaysInEra()
		{
			Assert.True(RadarSiteEra.IsInEra(Khdc, null));
			Assert.True(RadarSiteEra.IsInEra(Khdc, Utc(2021, 8, 29)));
		}

		[Fact]
		public void Retired_HiddenLive() => Assert.False(RadarSiteEra.IsInEra(Klix, null));

		// Hurricane Ida's landfall, 2021-08-29 — KLIX's data exists, so it must be pickable.
		[Fact]
		public void Retired_ShownForAnOlderReplay() => Assert.True(RadarSiteEra.IsInEra(Klix, Utc(2021, 8, 29, 16)));

		[Fact]
		public void Retired_ShownOnItsLastDay_LateInTheDay() => Assert.True(RadarSiteEra.IsInEra(Klix, Utc(2023, 11, 27, 23)));

		[Fact]
		public void Retired_HiddenTheDayAfter() => Assert.False(RadarSiteEra.IsInEra(Klix, Utc(2023, 11, 28)));

		// The day is the UTC day: 7 PM CST on Nov 27 is already Nov 28 UTC, after KLIX's last data.
		[Fact]
		public void Retired_ComparesTheUtcDay_NotLocal() =>
			Assert.False(RadarSiteEra.IsInEra(Klix, new DateTimeOffset(2023, 11, 27, 19, 0, 0, TimeSpan.FromHours(-6))));
	}
}
