using System;
using System.Linq;
using Anvil.Models;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Offline map naming (the user's calls, 2026-10-20): the name says the options, the same options again are ALWAYS kept
	/// beside (-2, -3…, the next free number), and with every copy deleted the next one is the plain name again.
	/// </summary>
	public class BasemapFileNamesTests
	{
		private static readonly BasemapArea[] L48Ak = { BasemapAreas.Lower48, BasemapAreas.Alaska };

		[Fact]
		public void The_name_says_the_detail_and_the_areas()
		{
			Assert.Equal("anvil-map-standard-l48-ak.pmtiles",
				BasemapFileNames.NextFileName(false, L48Ak, Array.Empty<string>()));
			Assert.Equal("anvil-map-full-l48-ak-hi-pr-gu-ca.pmtiles",
				BasemapFileNames.NextFileName(true, BasemapAreas.All, Array.Empty<string>()));
		}

		[Fact]
		public void Areas_are_named_in_list_order_whatever_order_they_were_ticked()
		{
			var picked = new[] { BasemapAreas.Canada, BasemapAreas.Hawaii, BasemapAreas.Lower48 };
			Assert.Equal("anvil-map-standard-l48-hi-ca", BasemapFileNames.BaseName(false, picked));
		}

		[Fact]
		public void The_same_options_again_are_kept_beside_with_the_next_free_number()
		{
			var existing = new[] { "anvil-map-standard-l48-ak.pmtiles", "ANVIL-MAP-STANDARD-L48-AK-2.PMTILES" };
			Assert.Equal("anvil-map-standard-l48-ak-3.pmtiles", BasemapFileNames.NextFileName(false, L48Ak, existing));
		}

		[Fact]
		public void A_deleted_middle_copy_is_reused_and_other_options_never_collide()
		{
			var existing = new[] { "anvil-map-standard-l48-ak.pmtiles", "anvil-map-standard-l48-ak-3.pmtiles", "anvil-map-full-l48-ak.pmtiles" };
			Assert.Equal("anvil-map-standard-l48-ak-2.pmtiles", BasemapFileNames.NextFileName(false, L48Ak, existing));
		}

		[Fact]
		public void With_every_copy_deleted_the_name_resets_to_plain()
		{
			Assert.Equal("anvil-map-standard-l48-ak.pmtiles",
				BasemapFileNames.NextFileName(false, L48Ak, new[] { "usa_full.pmtiles", "anvil-map-full-l48-ak-2.pmtiles" }));
		}

		[Fact]
		public void No_area_is_not_a_map()
		{
			Assert.Throws<ArgumentException>(() => BasemapFileNames.BaseName(false, Array.Empty<BasemapArea>()));
		}

		[Fact]
		public void A_name_reads_back_as_its_options()
		{
			var o = BasemapFileNames.TryParse(@"C:\Maps\anvil-map-full-l48-ak-2.pmtiles");
			Assert.NotNull(o);
			Assert.True(o!.IsFullDetail);
			Assert.Equal(L48Ak, o.Areas);
			Assert.Equal(2, o.Copy);
			Assert.Equal("Full · Lower 48 states, Alaska", o.Description);
		}

		[Theory]
		[InlineData("usa_full.pmtiles")]
		[InlineData("anvil-map-standard.pmtiles")]          // no area
		[InlineData("anvil-map-huge-l48.pmtiles")]           // no such detail
		[InlineData("anvil-map-standard-l48-zz.pmtiles")]    // no such area
		[InlineData("anvil-map-standard-ak-l48.pmtiles")]    // not list order — this rule never writes it
		[InlineData("anvil-map-standard-l48-l48.pmtiles")]   // an area twice
		[InlineData("anvil-map-standard-l48-1.pmtiles")]     // copy numbers start at 2
		[InlineData("anvil-map-standard-l48.mbtiles")]
		public void Names_the_rule_could_not_have_written_are_not_maps(string name)
		{
			Assert.Null(BasemapFileNames.TryParse(name));
		}

		[Fact]
		public void Every_name_the_rule_writes_reads_back()
		{
			foreach (var full in new[] { false, true })
			{
				for (var mask = 1; mask < 1 << BasemapAreas.All.Count; mask++)
				{
					var areas = BasemapAreas.All.Where((_, i) => (mask & (1 << i)) != 0).ToList();
					var o = BasemapFileNames.TryParse(BasemapFileNames.NextFileName(full, areas, Array.Empty<string>()));
					Assert.NotNull(o);
					Assert.Equal(full, o!.IsFullDetail);
					Assert.Equal(areas, o.Areas);
					Assert.Equal(1, o.Copy);
				}
			}
		}

		[Fact]
		public void Area_codes_are_unique_and_no_box_crosses_the_date_line()
		{
			Assert.Equal(BasemapAreas.All.Count, BasemapAreas.All.Select(a => a.Code).Distinct().Count());
			Assert.All(BasemapAreas.All.SelectMany(a => a.Boxes), b => Assert.True(b.West < b.East && b.South < b.North));
		}

		[Fact]
		public void Alaskas_far_aleutians_past_the_date_line_are_inside_alaska()
		{
			Assert.True(BasemapAreas.Alaska.Contains(52.9, 173.2));    // Attu
			Assert.True(BasemapAreas.Alaska.Contains(61.2, -149.9));   // Anchorage
			Assert.False(BasemapAreas.Alaska.Contains(35.3, -97.5));
		}

		[Theory]
		[InlineData("ca", 0, "Radars coming soon")]
		[InlineData("ca", 3, "3 radars")]
		[InlineData("gu", 1, "1 radar")]
		[InlineData("hi", 0, "0 radars")]
		public void Radar_counts_read_in_words(string code, int count, string words)
		{
			Assert.Equal(words, BasemapAreaOption.RadarWords(BasemapAreas.ByCode(code)!, count));
		}

		[Theory]
		[InlineData(0.9, "1 MB")]
		[InlineData(444, "444 MB")]
		[InlineData(4400, "4.4 GB")]
		[InlineData(19000, "19 GB")]
		public void Sizes_read_in_words(double megabytes, string words)
		{
			Assert.Equal(words, BasemapSetupViewModel.SizeWords(megabytes));
		}
	}
}
