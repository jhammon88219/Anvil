using Anvil.Models;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The two persisted range-ruler/ring preferences: <see cref="ScopeColors"/> and <see cref="RulerAnchors"/>.
	/// </summary>
	/// <remarks>
	/// ⚠️ <see cref="ScopeColors.Normalize"/> is a GATE, not a tidy-up: its output is written into the map
	/// page as a CSS custom-property value (map.js setScopeColor). Anything but a bare <c>#RRGGBB</c> or empty
	/// must come out empty — the injection cases below are the point of this file.
	/// </remarks>
	public class ScopePreferencesTests
	{
		[Theory]
		[InlineData("#22D3EE", "#22D3EE")]
		[InlineData("#22d3ee", "#22D3EE")]       // upper-cased so a preset matches however it was written
		[InlineData("#ABCDEF", "#ABCDEF")]       // a valid hex that is NOT a preset survives (hand-edited file)
		[InlineData("", "")]
		[InlineData(null, "")]
		[InlineData("22D3EE", "")]               // no '#'
		[InlineData("#22D3E", "")]               // too short
		[InlineData("#22D3EEFF", "")]            // alpha is not offered
		[InlineData("#GGGGGG", "")]
		[InlineData("red", "")]
		[InlineData("#fff;x", "")]               // CSS injection shapes must never pass
		[InlineData("#12345)", "")]
		public void ScopeColor_Normalize_OnlyLetsBareHexThrough(string? token, string expected) =>
			Assert.Equal(expected, ScopeColors.Normalize(token));

		[Fact]
		public void ScopeColor_FirstPresetIsTheThemeDefault() =>
			Assert.Equal(ScopeColors.ThemeDefault, ScopeColors.Presets[0].Hex);

		[Fact]
		public void ScopeColor_EveryPresetRoundTripsItsIndex()
		{
			for (var i = 0; i < ScopeColors.Presets.Count; i++)
			{
				Assert.Equal(ScopeColors.Presets[i].Hex, ScopeColors.Normalize(ScopeColors.Presets[i].Hex));
				Assert.Equal(i, ScopeColors.IndexOf(ScopeColors.FromIndex(i)));
			}
		}

		[Fact]
		public void ScopeColor_CustomHexHasNoSwatch_AndOutOfRangeIsThemeDefault()
		{
			Assert.Equal(-1, ScopeColors.IndexOf("#ABCDEF"));
			Assert.Equal(0, ScopeColors.IndexOf("garbage"));    // normalizes to the theme default
			Assert.Equal(ScopeColors.ThemeDefault, ScopeColors.FromIndex(-1));
			Assert.Equal(ScopeColors.ThemeDefault, ScopeColors.FromIndex(99));
		}

		[Theory]
		[InlineData("Site", "Site")]
		[InlineData("Location", "Location")]
		[InlineData("location", "Site")]    // tokens are ours, case-sensitive by design
		[InlineData("", "Site")]
		[InlineData(null, "Site")]
		public void RulerAnchor_Normalize_FallsBackToSite(string? token, string expected) =>
			Assert.Equal(expected, RulerAnchors.Normalize(token));

		[Fact]
		public void RulerAnchor_IndexAndTokenRoundTrip()
		{
			Assert.Equal(RulerAnchors.All.Count, RulerAnchors.Labels.Count);
			for (var i = 0; i < RulerAnchors.All.Count; i++)
			{
				Assert.Equal(i, RulerAnchors.IndexOf(RulerAnchors.FromIndex(i)));
			}
			Assert.Equal(RulerAnchors.Site, RulerAnchors.FromIndex(7));
		}
	}
}
