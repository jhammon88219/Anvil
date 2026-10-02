using System;
using System.IO;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="RiskAreaLocator"/> + the Outlook Discussion's "are you in the risk area" line. Synthetic categorical
	/// GeoJSON in our cache's shape (LABEL per feature): nested squares around 35°N 97°W — TSTM 4°, MRGL 2°, SLGT 1°
	/// (with a 0.2° hole), HIGH 0.2° off to the east.
	/// </summary>
	public class RiskAreaLocatorTests
	{
		private static string Square(string label, double lat, double lon, double half, string? hole = null) =>
			$$"""
			{ "type": "Feature", "properties": { "LABEL": "{{label}}" }, "geometry": { "type": "Polygon", "coordinates": [
			  [[{{lon - half}},{{lat - half}}],[{{lon + half}},{{lat - half}}],[{{lon + half}},{{lat + half}}],[{{lon - half}},{{lat + half}}],[{{lon - half}},{{lat - half}}]]
			  {{hole}} ] } }
			""";

		private static readonly string Areas = "{ \"type\": \"FeatureCollection\", \"features\": [" + string.Join(",",
			Square("TSTM", 35, -97, 4),
			Square("MRGL", 35, -97, 2),
			Square("SLGT", 35, -97, 1, ",[[-97.1,34.9],[-96.9,34.9],[-96.9,35.1],[-97.1,35.1],[-97.1,34.9]]"),
			Square("HIGH", 35, -96.5, 0.2)) + "] }";

		[Fact]
		public void Inside_IsTheHighestCategoryThatContainsThePoint()
		{
			Assert.Equal("HIGH", RiskAreaLocator.Locate(Areas, 35, -96.5)!.Code);
			Assert.Equal("SLGT", RiskAreaLocator.Locate(Areas, 35.5, -97.5)!.Code);
			Assert.Equal("MRGL", RiskAreaLocator.Locate(Areas, 36.5, -97)!.Code);
		}

		[Fact]
		public void Hole_SubtractsFromItsArea() =>
			Assert.Equal("MRGL", RiskAreaLocator.Locate(Areas, 35, -97)!.Code); // inside SLGT's hole, still in MRGL

		// General thunder is not a risk area: inside TSTM only = outside, with the distance to MRGL's edge.
		[Fact]
		public void Outside_GivesTheNearestEdge_AndItsDirection()
		{
			var fix = RiskAreaLocator.Locate(Areas, 38, -97)!; // 1° north of MRGL's top edge (37°N)
			Assert.False(fix.IsInside);
			Assert.InRange(fix.NearestEdgeMeters!.Value, 110_000, 112_500); // ~111 km
			Assert.Equal("south", fix.Bearing);
		}

		[Fact]
		public void NoSevereAreas_IsNull()
		{
			Assert.Null(RiskAreaLocator.Locate("{ \"type\": \"FeatureCollection\", \"features\": [" + Square("TSTM", 35, -97, 4) + "] }", 35, -97));
			Assert.Null(RiskAreaLocator.Locate("not json", 35, -97));
		}

		[Theory]
		[InlineData(0, "north")]
		[InlineData(130, "southeast")]
		[InlineData(-90, "west")]
		[InlineData(359, "north")]
		public void Compass_Points(double degrees, string expected) => Assert.Equal(expected, RiskAreaLocator.Compass(degrees));

		// ── The window's line ──

		private static string Disc(string name = "d1_20250314_2000") =>
			File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"spc-disc-{name}.txt"));

		private static string AreasFile()
		{
			var path = Path.Combine(Path.GetTempPath(), $"anvil-areas-{Guid.NewGuid():N}.geojson");
			File.WriteAllText(path, Areas);
			return path;
		}

		[Fact]
		public void Line_Inside_NamesTheCategory_AndRingsItsCard()
		{
			var vm = new OutlookDiscussionViewModel();
			vm.SetPosition(35, -96.5, "Edmond, OK");
			vm.Load(Disc("d1_20250315_1200"), areasFile: AreasFile(), distanceUnit: "mi"); // a High Risk day
			Assert.Equal("You're in the High Risk (5/5)", vm.YouText);
			Assert.Equal("· Edmond, OK", vm.YouDetail);
			Assert.True(vm.HighCard.IsPresent);
			Assert.True(vm.HighCard.IsYou);
			Assert.False(vm.MarginalCard.IsYou);
		}

		[Fact]
		public void Line_Outside_GivesDistanceAndDirection_InTheUsersUnits()
		{
			var vm = new OutlookDiscussionViewModel();
			vm.SetPosition(38, -97, "Salina, KS");
			vm.Load(Disc(), areasFile: AreasFile(), distanceUnit: "mi");
			Assert.Equal("You're outside the risk areas", vm.YouText);
			Assert.StartsWith("· the nearest edge is 69", vm.YouDetail); // ~111 km
			Assert.Contains("mi south of you · Salina, KS", vm.YouDetail);
		}

		// PastCast: still shown, with a reminder so a replayed High Risk isn't read as today's.
		[Fact]
		public void Line_PastCast_SaysWouldHaveBeen_AndReminds()
		{
			var vm = new OutlookDiscussionViewModel();
			vm.SetPosition(36.5, -97, null);
			vm.Load(Disc(), isArchive: true, areasFile: AreasFile());
			Assert.Equal("You'd have been in the Marginal Risk (1/5)", vm.YouText);
			Assert.Equal("· on this past outlook, not today's", vm.YouDetail);
		}

		[Fact]
		public void Line_States_LocatingAndOff()
		{
			var vm = new OutlookDiscussionViewModel();
			vm.Load(Disc(), areasFile: AreasFile());
			vm.SetLocating();
			Assert.Equal("Finding your location…", vm.YouText);
			vm.SetLocationOff();
			Assert.True(vm.IsLocationOff);
			Assert.StartsWith("Location is off", vm.YouText);
		}
	}
}
