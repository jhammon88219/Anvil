using System;
using Anvil.Models;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Settings → Units: the formatter every readout goes through (Models/Map/UnitFormat). ⚠️ Only the PURE overloads
	/// (an explicit UnitSet) — never UnitFormat.Apply, which is global and would leak into tests running beside these.
	/// </summary>
	public class UnitFormatTests
	{
		private static readonly DateTimeOffset At = new(2024, 5, 20, 22, 4, 5, TimeSpan.Zero);
		private static readonly UnitSet Utc = UnitSet.Default with { Zone = "utc" };

		[Fact]
		public void Utc_IsAlways24HourZ_EvenWhen12HourIsPicked()
		{
			Assert.Equal("22:04Z", UnitFormat.Clock(At, Utc));
			Assert.Equal("22:04Z", UnitFormat.Clock(At, Utc with { Clock = "24h" }));
			Assert.Equal("22:04:05Z", UnitFormat.ClockSeconds(At, Utc));
			Assert.True(Utc.Is24Hour);
		}

		[Fact]
		public void Utc_TheDateIsTheUtcDate()
		{
			// 22:04Z on May 20 is still May 20 in UTC, whatever the PC's zone.
			Assert.Equal("May 20", UnitFormat.Date(At, "MMM d", Utc));
		}

		[Fact]
		public void Local_FollowsTheClockChoice()
		{
			var twelve = UnitFormat.Clock(At, UnitSet.Default);
			var twentyFour = UnitFormat.Clock(At, UnitSet.Default with { Clock = "24h" });
			Assert.Contains(' ', twelve);                  // "5:04 PM"
			Assert.DoesNotContain(' ', twentyFour);        // "17:04"
			Assert.Equal(At.ToLocalTime().ToString("HH:mm"), twentyFour);
		}

		[Fact]
		public void Span_24Hour_IsBothClocks()
		{
			Assert.Equal("22:04–23:30Z", UnitFormat.ClockSpan(At, At.AddMinutes(86), Utc));
		}

		[Theory]
		[InlineData("kt", "24 kt")]
		[InlineData("mph", "28 mph")]
		[InlineData("kmh", "45 km/h")]
		[InlineData("ms", "12.5 m/s")]
		public void Speed_FromMetresPerSecond(string unit, string expected) =>
			Assert.Equal(expected, UnitFormat.SpeedFromMs(12.5, UnitSet.Default with { Speed = unit }));

		[Fact]
		public void Speed_FromMphAndKt_RoundTrip()
		{
			Assert.Equal("70 mph", UnitFormat.SpeedFromMph(70, UnitSet.Default));
			Assert.Equal("43 kt", UnitFormat.SpeedFromKt(43, UnitSet.Default with { Speed = "kt" }));
		}

		[Theory]
		[InlineData("kft", "22.4 kft")]
		[InlineData("km", "6.8 km")]
		[InlineData("ft", "22,400 ft")]
		public void Height_FromKft(string unit, string expected) =>
			Assert.Equal(expected, UnitFormat.HeightFromKft(22.4, UnitSet.Default with { Height = unit }));

		[Theory]
		[InlineData("in", "1.75 in")]
		[InlineData("cm", "4.4 cm")]
		public void Hail_FromInches(string unit, string expected) =>
			Assert.Equal(expected, UnitFormat.HailFromInches(1.75, UnitSet.Default with { Hail = unit }));

		[Fact]
		public void UnknownTokens_FallBackToTheDefaults()
		{
			var set = new UnitSet("13h", "mars", "furlongs", "cubits", "golf").Normalized();
			Assert.Equal(UnitSet.Default, set);
		}

		[Fact]
		public void PageJson_CarriesTokensOnly()
		{
			Assert.Equal("{\"clock\":\"12h\",\"zone\":\"utc\",\"speed\":\"mph\",\"height\":\"kft\",\"hail\":\"in\",\"date\":\"written\",\"coords\":\"decimal\"}",
				UnitFormat.PageJson(Utc));
		}

		// ── Date (General B, 2026-11-16) ──

		[Theory]
		[InlineData("written", "May 20, 2024", "May 20")]
		[InlineData("monthfirst", "5/20/2024", "5/20")]
		[InlineData("dayfirst", "20/5/2024", "20/5")]
		[InlineData("iso", "2024-05-20", "05-20")]
		public void Date_FollowsTheChosenStyle(string style, string full, string shortForm)
		{
			var u = Utc with { Date = style };
			Assert.Equal(full, UnitFormat.DateFull(At, u));
			Assert.Equal(shortForm, UnitFormat.DateShort(At, u));
		}

		[Fact]
		public void Date_Weekday_PrefixesTheDay()
		{
			var weekday = At.UtcDateTime.ToString("ddd", System.Globalization.CultureInfo.CurrentCulture);
			Assert.Equal(weekday + " 2024-05-20", UnitFormat.DateWeekday(At, Utc with { Date = "iso" }, year: true));
		}

		[Fact]
		public void Date_Utc_TheDateIsTheUtcDate()
		{
			// 23:30Z on May 20 is May 21 or 20 locally depending on the PC — in UTC it is always the 20th.
			var late = new DateTimeOffset(2024, 5, 20, 23, 30, 0, TimeSpan.Zero);
			Assert.Equal("2024-05-20", UnitFormat.DateFull(late, Utc with { Date = "iso" }));
		}

		// ── Coordinates ──

		[Fact]
		public void Coordinates_Decimal_NamesTheHemispheres()
		{
			Assert.Equal("35.333° N, 97.278° W", UnitFormat.LatLon(35.333, -97.278, UnitSet.Default));
			Assert.Equal("33.900° S, 151.200° E", UnitFormat.LatLon(-33.9, 151.2, UnitSet.Default));
		}

		[Fact]
		public void Coordinates_Dms_CarriesIntoTheNextMinute()
		{
			var dms = UnitSet.Default with { Coords = "dms" };
			Assert.Equal("35°19′59″ N, 97°16′41″ W", UnitFormat.LatLon(35.333, -97.278, dms));
			Assert.Equal("36°00′00″ N, 97°00′00″ W", UnitFormat.LatLon(35.99999, -96.99999, dms)); // 59.99″ rounds UP
		}

		[Fact]
		public void UnknownDateAndCoordTokens_FallBackToTheDefaults()
		{
			var set = (UnitSet.Default with { Date = "julian", Coords = "utm" }).Normalized();
			Assert.Equal(UnitSet.Default, set);
		}

		[Fact]
		public void StormReport_TimeOn_UsesTheConvectiveDay()
		{
			// 0130 belongs to the NEXT morning of the 12Z→12Z day that started May 20.
			var r = new StormReportItem("hail", 35, -97, "0130", "175", "Norman", "Cleveland", "OK");
			Assert.Equal(new DateTimeOffset(2024, 5, 21, 1, 30, 0, TimeSpan.Zero), r.TimeOn(new DateOnly(2024, 5, 20)));
			var bad = r with { Time = "UNK" };
			Assert.Null(bad.TimeOn(new DateOnly(2024, 5, 20)));
		}
	}
}
