using System.Collections.Generic;
using System.Text.Json.Nodes;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="TropicalService"/>'s pure parts: the VTEC ETN → storm key rule, NHC's ATCF storm list, and the live
	/// WWA rewrite into the page schema (the NWS's colour + priority on every zone, surge zones taking the only storm).
	/// </summary>
	/// <remarks>Real-data check: <c>TiltCheck -- --tropical</c> (live + Helene's landfall window).</remarks>
	public class TropicalServiceTests
	{
		[Theory]
		[InlineData("1009", 2026, "AL092026")]   // TS Isaias 2026 (checked against NHC's list, 2026-10-07)
		[InlineData("1009", 2024, "AL092024")]   // Helene 2024
		[InlineData("1015", 2020, "AL152020")]
		[InlineData("", 2026, "")]               // live storm surge: no ETN
		[InlineData("37", 2024, "")]             // an office's own number (Extreme Wind Warning)
		[InlineData("2018", 2026, "")]           // another basin's range — not verified, so unnamed
		public void StormKey_IsTheAtlanticStormFromTheEtn(string etn, int year, string expected) =>
			Assert.Equal(expected, TropicalService.StormKey(etn, year));

		[Fact]
		public void StormList_NamesEachStorm_WithItsClass()
		{
			const string text =
				"    ISAIAS, AL, L, , , , , 09, 2026, TS, O, 2026093000, 9999999999, , 022, , , 1, WARNING, 4, AL092026\n" +
				"    HELENE, AL, L, , , , , 09, 2024, HU, S, 2024092312, 2024092818, , , , , , ARCHIVE, , AL092024\n" +
				"    INVEST, EP, E, , , , , 95, 2026, DB, O, 2026083112, 9999999999, , , , , , METWATCH, , EP952026\n";

			var names = TropicalService.ParseStormList(text);

			Assert.Equal("Tropical Storm Isaias", names["AL092026"]);
			Assert.Equal("Hurricane Helene", names["AL092024"]);
			Assert.False(names.ContainsKey("EP952026"));    // invests aren't storms
		}

		private static string Wwa(params (string Phenom, string Sig, string Event)[] features)
		{
			var arr = new JsonArray();
			foreach (var (p, s, e) in features)
			{
				arr.Add(new JsonObject
				{
					["type"] = "Feature",
					["geometry"] = new JsonObject { ["type"] = "Polygon", ["coordinates"] = new JsonArray(new JsonArray(
						new JsonArray(-88.0, 30.0), new JsonArray(-87.0, 30.0), new JsonArray(-87.0, 31.0), new JsonArray(-88.0, 30.0))) },
					["properties"] = new JsonObject { ["phenom"] = p, ["sig"] = s, ["event"] = e, ["wfo"] = "KMOB" },
				});
			}
			return new JsonObject { ["type"] = "FeatureCollection", ["features"] = arr }.ToJsonString();
		}

		[Fact]
		public void Live_EveryZoneCarriesTheNwsColourAndPriority_AndSurgeTakesTheOnlyStorm()
		{
			var json = Wwa(("HU", "A", "1009"), ("SS", "A", ""), ("TR", "A", "1009"), ("TO", "A", "120"));
			var storms = new Dictionary<string, string> { ["AL092026"] = "Tropical Storm Isaias" };

			Assert.True(TropicalService.TryRewriteLive(json, storms, 2026, out var page, out var zones));

			Assert.Equal(3, zones.Count);                                  // the tornado watch isn't tropical
			Assert.All(zones, z => Assert.Equal("AL092026", z.StormKey));  // surge included
			var features = JsonNode.Parse(page)!["features"]!.AsArray();
			var hu = features[0]!["properties"]!;
			Assert.Equal("HU.A", (string?)hu["pid"]);
			Assert.Equal("#FF00FF", (string?)hu["fill"]);                  // the NWS's Hurricane Watch magenta
			Assert.Equal(54, (int)hu["prio"]!);
			Assert.Equal("Tropical Storm Isaias", (string?)features[1]!["properties"]!["storm"]);
		}

		[Fact]
		public void Live_TwoStorms_LeaveSurgeUnnamed()
		{
			var json = Wwa(("HU", "A", "1009"), ("TR", "A", "1010"), ("SS", "A", ""));

			Assert.True(TropicalService.TryRewriteLive(json, new Dictionary<string, string>(), 2026, out _, out var zones));

			Assert.Equal(string.Empty, zones[2].StormKey);
		}

		[Fact]
		public void Live_NotAFeatureCollection_Fails() =>
			Assert.False(TropicalService.TryRewriteLive("{\"error\":{}}", new Dictionary<string, string>(), 2026, out _, out _));

		[Fact]
		public void Products_AreInNwsPriorityOrder() =>
			Assert.Equal(new[] { "EW.W", "SS.W", "HU.W", "TR.W", "SS.A", "HU.A", "TR.A" },
				System.Linq.Enumerable.Select(TropicalProducts.All, p => p.Id));
	}
}
