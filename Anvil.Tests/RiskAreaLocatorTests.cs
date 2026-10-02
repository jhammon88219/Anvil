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

		// A REAL past (IEM) file — the Atlas "Outlook Test" event's 01Z issuance, May 6 2024. Its LABEL is blank and the
		// category is "threshold" (the live feed uses LABEL); reading LABEL alone found no areas ("Can't check").
		[Fact]
		public void PastFile_ReadsTheCategoryFromThreshold()
		{
			var past = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spc-cat-past-20240506-d1-c01.geojson"));
			Assert.Equal("HIGH", RiskAreaLocator.Locate(past, 35.339, -97.487)!.Code); // Moore, OK
			Assert.False(RiskAreaLocator.Locate(past, 39.74, -104.99)!.IsInside);   // Denver
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
			Assert.Equal("You are in the", vm.YouText);
			Assert.True(vm.HasYouPill);
			Assert.Equal("High risk area ·", vm.YouPillWords);
			Assert.Equal("5 of 5", vm.YouPillNumber);
			Assert.Equal("for this outlook.", vm.YouTrail);
			Assert.Equal(vm.HighCard.Fill, vm.YouPillInk);        // the pill wears the card band's pair
			Assert.Equal(vm.HighCard.BandFill, vm.YouPillFill);
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
			Assert.Equal("You are outside the risk areas for this outlook.", vm.YouText);
			Assert.False(vm.HasYouPill);
			Assert.StartsWith("· the nearest edge is 69", vm.YouDetail); // ~111 km
			Assert.Contains("mi south of you · Salina, KS", vm.YouDetail);
		}

		// PastCast: the SAME sentence ("for this outlook" is the reminder), no extra tail.
		[Fact]
		public void Line_PastCast_SameWording()
		{
			var vm = new OutlookDiscussionViewModel();
			vm.SetPosition(36.5, -97, null);
			vm.Load(Disc(), isArchive: true, areasFile: AreasFile());
			Assert.Equal("You are in the", vm.YouText);
			Assert.Equal("Marginal risk area ·", vm.YouPillWords);
			Assert.Equal("1 of 5", vm.YouPillNumber);
			Assert.Equal(string.Empty, vm.YouDetail);
		}

		// The card's title band: SPC's colour at 38% brightness, so the name (in SPC's colour) reads on it.
		[Theory]
		[InlineData("#66A366", "#FF273E27")]
		[InlineData("#FFEE99EE", "#FF5A3A5A")]
		[InlineData("not a colour", "#FF3A3A3A")]
		public void Band_IsADeepShadeOfTheFill(string fill, string expected) => Assert.Equal(expected, OutlookRiskCard.Deep(fill));

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
