using System;
using System.Linq;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The NowCast Warnings section's DAY LIST: CAP (in effect) joined to IEM (the day so far) on the VTEC event key,
	/// plus what this session saw end; the VTEC parse that makes the key; IEM's sbw_interval parse.
	/// </summary>
	public class WarningDayListTests
	{
		private static readonly DateTimeOffset T0 = new(2026, 10, 21, 18, 0, 0, TimeSpan.Zero);
		private static readonly DateTimeOffset DayStart = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

		private static WarningTarget W(string key, string phenom, DateTimeOffset sent, DateTimeOffset ends, string place = "Cleveland, OK") =>
			new("urn:" + key + ":" + sent.ToUnixTimeSeconds(), phenom, 0, sent, place, "{}", -98, 35, -97, 36, key, ends);

		[Fact]
		public void Vtec_KeyMatchesIem_AndIgnoresTheZeroBegin()
		{
			var v = Vtec.Parse("/O.CON.KOUN.TO.W.0012.000000T0000Z-261021T1830Z/");
			Assert.NotNull(v);
			Assert.Equal("OUN.TO.W.12", v!.Key);
			Assert.Equal(new DateTimeOffset(2026, 10, 21, 18, 30, 0, TimeSpan.Zero), v.Ends);
			Assert.Equal("JSJ.FF.W.319", Vtec.Parse("/O.NEW.TJSJ.FF.W.0319.261020T1826Z-261020T2030Z/")!.Key);
			Assert.Null(Vtec.Parse("not vtec"));
		}

		[Fact]
		public void InEffect_TakesTheEventsFirstIssue_FromIem()
		{
			var list = new WarningDayList();
			var firstIssue = T0.AddMinutes(-40);
			list.SetDay(new[] { W("OUN.TO.W.12", "TO", firstIssue, T0.AddMinutes(30), "Cleveland [OK]") });
			list.SetActive(new[] { W("OUN.TO.W.12", "TO", T0.AddMinutes(-5), T0.AddMinutes(30)) }, T0);   // an SVS: later sent

			var e = Assert.Single(list.Build(DayStart, T0));
			Assert.True(e.InEffect);
			Assert.Equal(firstIssue, e.Target.Sent);
			Assert.Equal("Cleveland, OK", e.Target.Place);                                               // CAP's place wins
		}

		[Fact]
		public void AWarningThatLeavesCap_IsEndedAtOnce_WithoutWaitingForIem()
		{
			var list = new WarningDayList();
			list.SetActive(new[] { W("OUN.SV.W.40", "SV", T0, T0.AddMinutes(45)) }, T0);
			list.SetActive(Array.Empty<WarningTarget>(), T0.AddMinutes(10));                               // cancelled early

			var e = Assert.Single(list.Build(DayStart, T0.AddMinutes(10)));
			Assert.False(e.InEffect);
			Assert.Equal(T0, e.EndedAt);                                                                  // last seen in effect
		}

		[Fact]
		public void EndedBeforeLaunch_ComesFromIem_NewestIssuedFirst_AndYesterdayIsDropped()
		{
			var list = new WarningDayList();
			list.SetDay(new[]
			{
				W("OUN.SV.W.1", "SV", T0.AddHours(-5), T0.AddHours(-4)),
				W("TSA.FF.W.2", "FF", T0.AddHours(-2), T0.AddHours(-1)),
				W("TSA.TO.W.3", "TO", DayStart.AddHours(-3), DayStart.AddMinutes(-30)),                  // ended before 12Z
			});
			list.SetActive(new[] { W("ICT.TO.W.4", "TO", T0.AddMinutes(-3), T0.AddMinutes(40)) }, T0);

			var rows = list.Build(DayStart, T0);
			Assert.Equal(new[] { "ICT.TO.W.4", "TSA.FF.W.2", "OUN.SV.W.1" }, rows.Select(r => r.Target.Key));
			Assert.Equal(new[] { true, false, false }, rows.Select(r => r.InEffect));
			Assert.Equal(T0.AddHours(-1), rows[1].EndedAt);
		}

		[Fact]
		public void DayStart_Is12Z_OfTheConvectiveDay()
		{
			Assert.Equal(DayStart, WarningsViewModel.DayStart(T0));
			Assert.Equal(DayStart, WarningsViewModel.DayStart(DayStart.AddHours(23)));                    // 11Z next morning
		}

		[Fact]
		public void IemDay_KeepsOneRowPerWarningEvent_AndDropsAdvisories()
		{
			const string json = """
			{"type":"FeatureCollection","features":[
			 {"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[-98,35],[-97,35],[-97,36],[-98,35]]]},
			  "properties":{"locations":"Cleveland [OK], McClain [OK]","utc_issue":"2026-10-21T17:20:00Z","utc_expire":"2026-10-21T18:00:00Z",
			   "wfo":"OUN","eventid":12,"phenomena":"TO","significance":"W"}},
			 {"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[-98,35],[-97,35],[-97,36],[-98,35]]]},
			  "properties":{"locations":"Cleveland [OK]","utc_issue":"2026-10-21T17:20:00Z","utc_expire":"2026-10-21T18:00:00Z",
			   "wfo":"OUN","eventid":12,"phenomena":"TO","significance":"W"}},
			 {"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[-66,18],[-65,18],[-65,19],[-66,18]]]},
			  "properties":{"locations":"Dorado [PR]","utc_issue":"2026-10-21T17:00:00Z","utc_expire":"2026-10-21T19:00:00Z",
			   "wfo":"JSJ","eventid":319,"phenomena":"FA","significance":"Y"}}
			]}
			""";
			var w = Assert.Single(PastAlertService.ParseWarningDay(json)!);
			Assert.Equal("OUN.TO.W.12", w.Key);
			Assert.Equal("Cleveland, OK", w.Place);
			Assert.Equal((-98.0, 35.0, -97.0, 36.0), (w.West, w.South, w.East, w.North));
			Assert.Equal(new DateTimeOffset(2026, 10, 21, 18, 0, 0, TimeSpan.Zero), w.Ends);
			Assert.Null(PastAlertService.ParseWarningDay("{}"));
		}

		[Fact]
		public void CapTransform_CarriesTheVtecKeyAndEnd()
		{
			const string cap = """
			{"type":"FeatureCollection","features":[{"type":"Feature",
			 "geometry":{"type":"Polygon","coordinates":[[[-98,35],[-97,35],[-97,36],[-98,35]]]},
			 "properties":{"id":"urn:a","event":"Tornado Warning","expires":"2026-10-21T18:30:00Z","sent":"2026-10-21T17:52:00Z",
			  "areaDesc":"Cleveland, OK","parameters":{"VTEC":["/O.NEW.KOUN.TO.W.0012.261021T1752Z-261021T1830Z/"]}}}]}
			""";
			Assert.True(WarningService.TryTransformCap(cap, out var features, out _));
			var t = Assert.Single(WarningService.TargetsOf(features));
			Assert.Equal("OUN.TO.W.12", t.Key);
			Assert.Equal(new DateTimeOffset(2026, 10, 21, 18, 30, 0, TimeSpan.Zero), t.Ends);
		}
	}
}
