using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Guards the offline SPC risk catalog — the embedded table of NOAA's published risk levels and
	/// colours that replaced three hand-typed C# tables.
	///
	/// <para>TWO KINDS of test here, and the split is deliberate:</para>
	/// <list type="number">
	/// <item><b>Shape invariants</b> (always run, no network). They assert the things a corrupt or
	/// half-regenerated catalog would break — ordering, the per-hazard CIG counts SPC defines, parseable
	/// hexes — and they are what make a bad regeneration fail fast.</item>
	/// <item><b>The live diff</b> (opt-in, <c>Trait("Category","Network")</c>). It re-fetches NOAA's
	/// symbology and diffs it against the embedded catalog. This is the ONLY mechanism that can notice
	/// SPC changing something: the catalog is frozen at build time by design, so without it a service
	/// change goes unnoticed — which is exactly what happened with the conditional-intensity change of
	/// 2026-03-02, still undetected here six months later.</item>
	/// </list>
	///
	/// <para>
	/// Run the offline set with the default filter; run everything (including the network diff) with
	/// <c>dotnet test</c> and no filter, or isolate it with
	/// <c>--filter Category=Network</c>. CI should exclude it (<c>--filter Category!=Network</c>) so a
	/// NOAA outage never reds the build.
	/// </para>
	/// </summary>
	public class SpcRiskCatalogTests
	{
		// Every product Anvil actually renders an outlook for.
		public static IEnumerable<object[]> AllProducts() =>
			Enum.GetValues<SpcOutlookType>().Select(t => new object[] { t });

		[Fact]
		public void Catalog_LoadsFromEmbeddedResource()
		{
			// The whole offline premise: no file, no network, no cache — it is in the assembly.
			Assert.False(string.IsNullOrWhiteSpace(SpcRiskCatalog.GeneratedUtc));
			Assert.Contains("26-11", SpcRiskCatalog.ServiceChange);
			Assert.NotEmpty(SpcRiskCatalog.Products);
		}

		[Theory]
		[MemberData(nameof(AllProducts))]
		public void EveryProduct_HasAScale(SpcOutlookType type)
		{
			Assert.NotEmpty(SpcRiskCatalog.ScaleFor(type).Levels);
		}

		[Theory]
		[MemberData(nameof(AllProducts))]
		public void SolidLevels_AreOrderedLeastSevereFirst(SpcOutlookType type)
		{
			// Ordering is SPC's `dn` ordinal, not the order someone typed the rows in — that is the
			// point of carrying dn at all.
			var solid = SpcRiskCatalog.ScaleFor(type).SolidLevels.ToArray();
			for (var i = 1; i < solid.Length; i++)
			{
				Assert.True(solid[i].Dn > solid[i - 1].Dn,
					$"{type}: {solid[i].Code} (dn {solid[i].Dn}) does not follow {solid[i - 1].Code} (dn {solid[i - 1].Dn})");
			}
		}

		[Theory]
		[MemberData(nameof(AllProducts))]
		public void IntensityGroups_ComeAfterSolidLevels(SpcOutlookType type)
		{
			// Solid and CIG levels are SEPARATE ordinal namespaces (tornado has a 2% level and CIG2 both
			// at dn 2), so they are concatenated, never interleaved. A sort that merged them would put
			// CIG1 ahead of the 2% area and read as the lowest step on the scale.
			var kinds = SpcRiskCatalog.ScaleFor(type).Levels.Select(l => l.Kind).ToArray();
			var firstCig = Array.IndexOf(kinds, SpcLevelKind.ConditionalIntensity);
			if (firstCig >= 0)
			{
				Assert.DoesNotContain(SpcLevelKind.Solid, kinds.Skip(firstCig));
			}
		}

		[Theory]
		[InlineData(SpcOutlookType.Tornado, 3)]               // SPC defines three groups for tornado
		[InlineData(SpcOutlookType.Wind, 3)]                  // and wind
		[InlineData(SpcOutlookType.Hail, 2)]                  // but only TWO for hail
		[InlineData(SpcOutlookType.ProbabilisticCombined, 2)] // Day 3 combined: two
		[InlineData(SpcOutlookType.Categorical, 0)]           // the categorical outlook is never hatched
		[InlineData(SpcOutlookType.ExtendedProbabilistic, 0)]
		[InlineData(SpcOutlookType.FireWeather, 0)]
		[InlineData(SpcOutlookType.ExtendedFireWeather, 0)]
		public void IntensityGroupCount_MatchesWhatSpcDefines(SpcOutlookType type, int expected)
		{
			// A third hail group would be an invented level; a missing tornado group would silently stop
			// the map hatching the most intense areas SPC draws.
			Assert.Equal(expected, SpcRiskCatalog.ScaleFor(type).IntensityGroups.Count());
		}

		[Theory]
		[MemberData(nameof(AllProducts))]
		public void EveryLevel_HasParseableColoursAndAName(SpcOutlookType type)
		{
			foreach (var level in SpcRiskCatalog.ScaleFor(type).Levels)
			{
				Assert.False(string.IsNullOrWhiteSpace(level.Code));
				Assert.False(string.IsNullOrWhiteSpace(level.OfficialName));
				AssertHex(level.Fill, $"{type}/{level.Code} fill");
				AssertHex(level.Stroke, $"{type}/{level.Code} stroke");
			}
		}

		[Fact]
		public void IntensityGroups_EachHaveADistinctHatchPattern()
		{
			// The pattern is the ONLY thing telling the groups apart — every CIG level is black — so two
			// groups sharing a pattern makes them indistinguishable on the map.
			foreach (var type in Enum.GetValues<SpcOutlookType>())
			{
				var patterns = SpcRiskCatalog.ScaleFor(type).IntensityGroups.Select(g => g.Hatch).ToArray();
				Assert.DoesNotContain(SpcHatchPattern.None, patterns);
				Assert.Equal(patterns.Length, patterns.Distinct().Count());
			}
		}

		[Fact]
		public void LegacySignCode_ResolvesToCig1()
		{
			// Everything PastCast can replay predates the CIG migration and carries "SIGN". Losing this
			// alias would silently stop historical outlooks hatching at all.
			var level = SpcRiskCatalog.Level(SpcOutlookType.Tornado, SpcRiskCatalog.LegacySignificantCode);
			Assert.NotNull(level);
			Assert.Equal("CIG1", level!.Code);
			Assert.True(level.IsConditionalIntensity);
		}

		[Fact]
		public void DnLookup_IgnoresIntensityGroups()
		{
			// Tornado carries BOTH a 2% probability level and CIG2 at dn 2. A dn lookup must return the
			// probability area; returning the intensity group would colour a 2% region as hatching.
			var level = SpcRiskCatalog.Level(SpcOutlookType.Tornado, 2);
			Assert.NotNull(level);
			Assert.Equal("0.02", level!.Code);
			Assert.Equal(SpcLevelKind.Solid, level.Kind);
		}

		[Fact]
		public void FireWeather_ColoursEachRiskDistinctly()
		{
			// The regression this whole change exists for: the live ArcGIS feed publishes no symbology,
			// so before the catalog every fire level rendered as one flat grey.
			var fills = SpcRiskCatalog.ScaleFor(SpcOutlookType.FireWeather)
				.SolidLevels.Select(l => l.Fill).ToArray();
			Assert.Equal(3, fills.Length);
			Assert.Equal(3, fills.Distinct().Count());
			Assert.DoesNotContain("#888888", fills);
		}

		[Fact]
		public void ProbabilityColours_DifferBetweenTornadoAndWind()
		{
			// The single shared probability table was wrong by construction: SPC gives tornado and
			// wind/hail DIFFERENT colours at the same percentage. If these ever match, someone has
			// collapsed the per-hazard scales back into one.
			var tornado = SpcRiskCatalog.Level(SpcOutlookType.Tornado, "0.15");
			var wind = SpcRiskCatalog.Level(SpcOutlookType.Wind, "0.15");
			Assert.NotNull(tornado);
			Assert.NotNull(wind);
			Assert.NotEqual(tornado!.Fill, wind!.Fill);
		}

		[Fact]
		public void WindScale_CarriesTheHighEndLevels()
		{
			// Wind runs to 90% where tornado and hail stop at 60%. The old table capped everything at
			// 60%, so a high-end derecho drew areas with no legend row at all.
			Assert.NotNull(SpcRiskCatalog.Level(SpcOutlookType.Wind, "0.75"));
			Assert.NotNull(SpcRiskCatalog.Level(SpcOutlookType.Wind, "0.90"));
			Assert.Null(SpcRiskCatalog.Level(SpcOutlookType.Hail, "0.75"));
		}

		// ── The live diff ───────────────────────────────────────────────────────────────────────────

		[Fact]
		[Trait("Category", "Network")]
		public async Task EmbeddedCatalog_StillMatchesNoaasPublishedSymbology()
		{
			// One representative layer per shape: a categorical scale, a per-hazard probability scale,
			// and a conditional-intensity layer. Enough to catch a palette or level change; the full
			// walk is the generator's job.
			var checks = new (SpcOutlookType Type, int Layer, SpcLevelKind Kind)[]
			{
				(SpcOutlookType.Categorical, 1, SpcLevelKind.Solid),
				(SpcOutlookType.Tornado, 3, SpcLevelKind.Solid),
				(SpcOutlookType.Wind, 7, SpcLevelKind.Solid),
				(SpcOutlookType.Hail, 5, SpcLevelKind.Solid),
				(SpcOutlookType.Tornado, 2, SpcLevelKind.ConditionalIntensity),
			};

			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
			http.DefaultRequestHeaders.Add("User-Agent", "Anvil-tests/1.0");

			foreach (var (type, layer, kind) in checks)
			{
				var url = "https://mapservices.weather.noaa.gov/vector/rest/services/outlooks/" +
					$"SPC_wx_outlks/MapServer/{layer}?f=json";
				using var doc = JsonDocument.Parse(await http.GetStringAsync(url));

				var published = doc.RootElement
					.GetProperty("drawingInfo").GetProperty("renderer").GetProperty("uniqueValueInfos")
					.EnumerateArray()
					.Select(info => Hex(info.GetProperty("symbol")))
					.OrderBy(hex => hex, StringComparer.Ordinal)
					.ToArray();

				var embedded = SpcRiskCatalog.ScaleFor(type).Levels
					.Where(l => l.Kind == kind)
					.Select(l => l.Fill)
					.OrderBy(hex => hex, StringComparer.Ordinal)
					.ToArray();

				Assert.True(published.SequenceEqual(embedded),
					$"{type} ({kind}) has drifted from NOAA layer {layer}.\n" +
					$"  published: {string.Join(", ", published)}\n" +
					$"  embedded:  {string.Join(", ", embedded)}\n" +
					"Re-run tools/make_spc_catalog.py and commit the regenerated catalog.");
			}
		}

		private static string Hex(JsonElement symbol)
		{
			var c = symbol.GetProperty("color");
			return $"#{c[0].GetInt32():X2}{c[1].GetInt32():X2}{c[2].GetInt32():X2}";
		}

		private static void AssertHex(string value, string what)
		{
			Assert.True(
				value.Length == 7 && value[0] == '#' &&
				int.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _),
				$"{what} is not a #RRGGBB colour: '{value}'");
		}
	}
}
