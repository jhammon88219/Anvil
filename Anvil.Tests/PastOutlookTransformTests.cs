using System.Collections.Generic;
using System.Text.Json;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The IEM→renderer transform (<see cref="SpcOutlookColors.TryBuildProduct"/>) that lets historical
	/// outlooks reuse the live outlook render path: it must filter to the requested product's category,
	/// color each feature by threshold (the renderer reads <c>fill</c>/<c>stroke</c>) from
	/// <see cref="SpcRiskCatalog"/>, and label significant areas with a CIG code so they hatch.
	/// Deterministic — no network.
	/// </summary>
	public class PastOutlookTransformTests
	{
		// A minimal IEM-shaped convective collection: categorical SLGT, tornado 5% + significant, hail 15%.
		private const string IemSample = """
		{
		  "type": "FeatureCollection",
		  "features": [
		    { "type": "Feature",
		      "properties": { "category": "CATEGORICAL", "threshold": "SLGT",
		        "issue": "2013-05-20T20:00:00Z", "expire": "2013-05-21T12:00:00Z" },
		      "geometry": { "type": "Polygon", "coordinates": [[[-98,35],[-97,35],[-97,36],[-98,36],[-98,35]]] } },
		    { "type": "Feature",
		      "properties": { "category": "TORNADO", "threshold": "0.05" },
		      "geometry": { "type": "Polygon", "coordinates": [[[-98,35],[-97,35],[-97,36],[-98,36],[-98,35]]] } },
		    { "type": "Feature",
		      "properties": { "category": "TORNADO", "threshold": "SIGN" },
		      "geometry": { "type": "Polygon", "coordinates": [[[-98,35],[-97,35],[-97,36],[-98,36],[-98,35]]] } },
		    { "type": "Feature",
		      "properties": { "category": "HAIL", "threshold": "0.15" },
		      "geometry": { "type": "Polygon", "coordinates": [[[-98,35],[-97,35],[-97,36],[-98,36],[-98,35]]] } }
		  ]
		}
		""";

		private static JsonElement Root() => JsonDocument.Parse(IemSample).RootElement;

		[Fact]
		public void Categorical_KeepsOnlyCategorical_WithSlgtFill()
		{
			var ok = SpcOutlookColors.TryBuildProduct(Root(), SpcOutlookType.Categorical, out var gj, out var times);
			Assert.True(ok);

			using var doc = JsonDocument.Parse(gj);
			var feats = doc.RootElement.GetProperty("features");
			Assert.Equal(1, feats.GetArrayLength()); // only the CATEGORICAL feature survives
			var props = feats[0].GetProperty("properties");
			Assert.Equal("#FFE066", props.GetProperty("fill").GetString()); // SPC Slight yellow
			Assert.Equal("", props.GetProperty("LABEL").GetString());       // categorical is never hatched
			Assert.NotNull(times);
		}

		[Fact]
		public void Tornado_KeepsProbAndSignificant_AndMapsLegacySignToCig1()
		{
			var ok = SpcOutlookColors.TryBuildProduct(Root(), SpcOutlookType.Tornado, out var gj, out _);
			Assert.True(ok);

			using var doc = JsonDocument.Parse(gj);
			var feats = doc.RootElement.GetProperty("features");
			Assert.Equal(2, feats.GetArrayLength()); // 5% + SIGN

			string? signLabel = null;
			foreach (var f in feats.EnumerateArray())
			{
				var p = f.GetProperty("properties");
				if (p.GetProperty("threshold").GetString() == "SIGN")
				{
					signLabel = p.GetProperty("LABEL").GetString();
				}
			}
			// ⚠️ The emitted label is CIG1, not the "SIGN" that came IN. Before NWS service change 26-11
			// (2026-03-02) SPC drew one significant-severe area under that label; every archive older than
			// that — i.e. everything PastCast replays — still carries it, and the renderer now routes
			// hatching by CIG code. SpcRiskCatalog aliases the two so historical outlooks keep hatching.
			Assert.Equal("CIG1", signLabel);
		}

		[Fact]
		public void ProbabilisticCombined_MapsAnySevere_WithProbRamp()
		{
			// Days 2-3 carry a single "ANY SEVERE" combined probabilistic (not separate tor/wind/hail).
			const string day3 = """
			{ "type": "FeatureCollection", "features": [
			  { "type": "Feature", "properties": { "category": "ANY SEVERE", "threshold": "0.15" },
			    "geometry": { "type": "Polygon", "coordinates": [[[-98,35],[-97,35],[-97,36],[-98,36],[-98,35]]] } },
			  { "type": "Feature", "properties": { "category": "CATEGORICAL", "threshold": "SLGT" },
			    "geometry": { "type": "Polygon", "coordinates": [[[-98,35],[-97,35],[-97,36],[-98,36],[-98,35]]] } } ] }
			""";
			var root = JsonDocument.Parse(day3).RootElement;

			var ok = SpcOutlookColors.TryBuildProduct(root, SpcOutlookType.ProbabilisticCombined, out var gj, out _);
			Assert.True(ok);
			using var doc = JsonDocument.Parse(gj);
			var feats = doc.RootElement.GetProperty("features");
			Assert.Equal(1, feats.GetArrayLength()); // only ANY SEVERE, not the categorical
			// ⚠️ This assertion used to demand #FF0000 — and that was the BUG, encoded as a test. The old
			// hand-typed ramp was SPC's stroke colours shifted a level, so a 15% risk drew red where SPC
			// draws it yellow. #FFEB7F is what NOAA's published symbology actually says.
			Assert.Equal("#FFEB7F", feats[0].GetProperty("properties").GetProperty("fill").GetString());
		}

		[Fact]
		public void ColorizeByDn_ColoursAnUnstyledFireFeed()
		{
			// The exact shape NOAA's ArcGIS fire-weather query returns: a numeric `dn` and NO symbology.
			// Before this, outlook.js's coalesce drew every risk level as one flat grey #888888.
			const string arcgis = """
			{ "type": "FeatureCollection", "features": [
			  { "type": "Feature", "properties": { "dn": 5, "valid": "202609111700" },
			    "geometry": { "type": "Polygon", "coordinates": [[[-98,35],[-97,35],[-97,36],[-98,36],[-98,35]]] } },
			  { "type": "Feature", "properties": { "dn": 8, "valid": "202609111700" },
			    "geometry": { "type": "Polygon", "coordinates": [[[-99,35],[-98,35],[-98,36],[-99,36],[-99,35]]] } } ] }
			""";

			var ok = SpcOutlookColors.TryColorizeByDn(arcgis, SpcOutlookType.FireWeather, out var gj);
			Assert.True(ok);

			using var doc = JsonDocument.Parse(gj);
			var feats = doc.RootElement.GetProperty("features");
			var fills = new List<string?>();
			foreach (var f in feats.EnumerateArray())
			{
				var props = f.GetProperty("properties");
				fills.Add(props.GetProperty("fill").GetString());
				Assert.Equal("202609111700", props.GetProperty("valid").GetString()); // existing props kept
			}

			Assert.Equal(new[] { "#E69800", "#FF0000" }, fills);   // Elevated, Critical
			Assert.DoesNotContain("#888888", fills);               // the grey this replaced
		}

		[Fact]
		public void ColorizeByDn_LeavesFeaturesThatAlreadyCarryColours()
		{
			// ⚠️ The precedence rule, per feature. The days 3-8 fire feed ships its own fill/stroke while
			// its PUBLISHED symbology is outline-only, so overwriting from the catalog would replace the
			// colours SPC actually draws. Leaving styled features alone is also what makes this safe to
			// run on every refresh — including a 304 — which is how an already-cached file gets repaired.
			const string styled = """
			{ "type": "FeatureCollection", "features": [
			  { "type": "Feature", "properties": { "dn": 40, "label": "0.40", "fill": "#FFBF80", "stroke": "#FF7F00" },
			    "geometry": { "type": "Polygon", "coordinates": [[[-98,35],[-97,35],[-97,36],[-98,36],[-98,35]]] } } ] }
			""";

			// Nothing needed colouring, so the caller is told to leave the cache untouched.
			Assert.False(SpcOutlookColors.TryColorizeByDn(styled, SpcOutlookType.ExtendedFireWeather, out _));
		}

		[Fact]
		public void MissingProduct_ReturnsFalse()
		{
			// No WIND features in the sample → nothing to draw.
			var ok = SpcOutlookColors.TryBuildProduct(Root(), SpcOutlookType.Wind, out var gj, out _);
			Assert.False(ok);
			Assert.Equal("", gj);
		}
	}
}
