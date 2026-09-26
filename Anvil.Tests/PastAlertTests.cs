using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// PastCast warnings + watches: the shapefile reader, the row → page-feature build and the "in effect at
	/// this frame" count. Both fixtures are REAL IEM watchwarn.py shapefile exports (fetched 2026-09-26):
	/// TSA's warnings for 2024-05-06/07 (Barnsdall — TO.W.47 upgraded to CATASTROPHIC in a follow-up) and
	/// OUN's watch counties for 2013-05-20 (Moore — tornado watch 191).
	/// </summary>
	public class PastAlertTests
	{
		private static byte[] Fixture(string name) =>
			File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

		private static DateTimeOffset Z(int y, int mo, int d, int h, int m) => new(y, mo, d, h, m, 0, TimeSpan.Zero);

		private static readonly DateTimeOffset BarnsdallStart = Z(2024, 5, 7, 2, 0);
		private static readonly DateTimeOffset BarnsdallEnd = Z(2024, 5, 7, 3, 30);

		private static (string Json, System.Collections.Generic.List<PastAlert> Alerts) Warnings() =>
			PastAlertService.Build(IemShapefile.Read(Fixture("iem-warnings-TSA-20240507.zip")), BarnsdallStart, BarnsdallEnd, watches: false);

		[Fact]
		public void Warning_IsDrawnPerPolygonVersion_WithThatVersionsTier()
		{
			var (_, alerts) = Warnings();
			var tor47 = alerts.Where(a => a.Key == "TSA.TO.W.47.2024").OrderBy(a => a.Start).ToList();

			// NEW (considerable) → CON (catastrophic: the Barnsdall emergency) ×2 → CON (considerable again).
			Assert.Equal(new[] { 1, 2, 2, 1 }, tor47.Select(a => a.Tier));
			Assert.Equal(Z(2024, 5, 7, 2, 35), tor47[0].Start);
			Assert.Equal(Z(2024, 5, 7, 3, 15), tor47[^1].End);
		}

		[Fact]
		public void InEffect_CountsOneAlertPerKey_AtItsCurrentVersion()
		{
			var (_, alerts) = Warnings();

			var at0240 = PastAlertsViewModel.InEffect(alerts, Z(2024, 5, 7, 2, 40));
			var tor47 = Assert.Single(at0240, a => a.Key == "TSA.TO.W.47.2024");
			Assert.Equal(2, tor47.Tier);
			Assert.Equal(1, WarningThreatCounts.From(at0240.Select(a => (a.Phenom, a.Tier))).TornadoEmergency);

			Assert.Equal(1, Assert.Single(PastAlertsViewModel.InEffect(alerts, Z(2024, 5, 7, 3, 0)), a => a.Key == "TSA.TO.W.47.2024").Tier);
			Assert.DoesNotContain(PastAlertsViewModel.InEffect(alerts, Z(2024, 5, 7, 3, 20)), a => a.Key == "TSA.TO.W.47.2024");
		}

		[Fact]
		public void Warnings_DropTheEndingStatements_AndStayInTheWindow()
		{
			var (json, alerts) = Warnings();

			Assert.All(alerts, a => Assert.True(a.End > BarnsdallStart && a.Start < BarnsdallEnd));
			Assert.All(alerts, a => Assert.Contains(a.Phenom, new[] { "TO", "SV", "FF" }));
			// TO.W.44 ended (EXP) at 02:00 — its closing statement is not an area in effect.
			Assert.DoesNotContain(alerts, a => a.Key == "TSA.TO.W.44.2024" && a.Start >= Z(2024, 5, 7, 2, 0));

			var doc = JsonDocument.Parse(json).RootElement.GetProperty("features");
			Assert.Equal(alerts.Count, doc.GetArrayLength());
			var f = doc[0];
			Assert.Equal("MultiPolygon", f.GetProperty("geometry").GetProperty("type").GetString());
			var ring = f.GetProperty("geometry").GetProperty("coordinates")[0][0];
			Assert.Equal(ring[0][0].GetDouble(), ring[ring.GetArrayLength() - 1][0].GetDouble()); // closed ring
			Assert.True(f.GetProperty("properties").GetProperty("t1").GetInt64() > f.GetProperty("properties").GetProperty("t0").GetInt64());
		}

		[Fact]
		public void Watches_AreCountyFilled_AndCountOncePerWatch()
		{
			var rows = IemShapefile.Read(Fixture("iem-watches-OUN-20130520.zip"));
			var (json, alerts) = PastAlertService.Build(rows, Z(2013, 5, 20, 19, 0), Z(2013, 5, 21, 1, 0), watches: true);

			Assert.Equal(34, alerts.Count);                                 // one per COUNTY of watch 191
			Assert.All(alerts, a => Assert.Equal("TO.A.191.2013", a.Key));
			Assert.Single(PastAlertsViewModel.InEffect(alerts, Z(2013, 5, 20, 20, 0)));

			// Counties cancelled at 00:39/00:46 drop out; the watch (8 counties continuing) still counts once.
			var counties = alerts.Count(a => a.IsInEffectAt(Z(2013, 5, 21, 0, 50)));
			Assert.Equal(8, counties);
			Assert.Single(PastAlertsViewModel.InEffect(alerts, Z(2013, 5, 21, 0, 50)));
			Assert.Equal(34, JsonDocument.Parse(json).RootElement.GetProperty("features").GetArrayLength());
		}

		[Fact]
		public void ACancellationStatement_IsNeverAnAreaInEffect()
		{
			// ⚠️ In IEM's rows an EXP/CAN statement usually has POLY_END before POLY_BEG, so the span check
			// alone drops it — but not always. The STATUS rule must hold on its own.
			static IemShapefile.Record Row(string status) => new(
				new System.Collections.Generic.Dictionary<string, string>
				{
					["WFO"] = "TSA", ["PHENOM"] = "TO", ["SIG"] = "W", ["ETN"] = "9", ["VTEC_YR"] = "2024", ["STATUS"] = status,
					["POLY_BEG"] = "202405070230", ["POLY_END"] = "202405070300", ["DAMAGTAG"] = "", ["EMERGENC"] = "F",
				},
				new() { new() { new() { new[] { -96.0, 36.0 }, new[] { -96.0, 36.5 }, new[] { -95.5, 36.5 }, new[] { -96.0, 36.0 } } } });

			var built = PastAlertService.Build(new[] { Row("CON"), Row("CAN"), Row("EXP") }, BarnsdallStart, BarnsdallEnd, watches: false);
			Assert.Single(built.Alerts);
		}

		[Fact]
		public void AWindowNothingOverlaps_IsEmpty()
		{
			var rows = IemShapefile.Read(Fixture("iem-watches-OUN-20130520.zip"));
			Assert.Empty(PastAlertService.Build(rows, Z(2013, 5, 21, 4, 0), Z(2013, 5, 21, 5, 0), watches: true).Alerts);
		}

		[Fact]
		public void GroupRings_HolesJoinTheirOuterRing_IslandsStaySeparate()
		{
			static System.Collections.Generic.List<double[]> Square(double x, double y, double s, bool clockwise)
			{
				var pts = new[] { new[] { x, y }, new[] { x, y + s }, new[] { x + s, y + s }, new[] { x + s, y }, new[] { x, y } };
				return (clockwise ? pts : pts.Reverse()).ToList();
			}

			var polygons = IemShapefile.GroupRings(new()
			{
				Square(0, 0, 10, clockwise: true),   // outer
				Square(2, 2, 2, clockwise: false),   // its hole
				Square(20, 0, 5, clockwise: true),   // an island: a second polygon
			});

			Assert.Equal(2, polygons.Count);
			Assert.Equal(2, polygons[0].Count);
			Assert.Single(polygons[1]);
		}
	}
}
