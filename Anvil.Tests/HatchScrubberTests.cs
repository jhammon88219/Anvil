using System.Linq;
using Anvil.Models;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>The hatch scrubber: its style token and its strip (the edge hatch laid along the loop).</summary>
	public class HatchScrubberTests
	{
		[Fact]
		public void Style_tokens_round_trip()
		{
			Assert.Equal(new[] { "Classic", "Dots", "Hatch" }, ScrubberStyles.All);
			Assert.Equal(ScrubberStyles.All.Count, ScrubberStyles.Labels.Count);
			for (int i = 0; i < ScrubberStyles.All.Count; i++)
			{
				Assert.Equal(i, ScrubberStyles.IndexOf(ScrubberStyles.FromIndex(i)));
			}
			Assert.Equal("Dots", ScrubberStyles.Normalize("bogus"));
			Assert.Equal("Hatch", ScrubberStyles.Normalize("Hatch"));
		}

		[Fact]
		public void A_fully_loaded_strip_is_exactly_the_edge()
		{
			const double length = 420, depth = HatchScrubberStrip.Depth, spacing = 5;
			var fills = Enumerable.Repeat(1.0, 30).ToArray();
			var (lines, current) = HatchScrubberStrip.Build(length, depth, spacing, fills, 7);
			var edge = EdgeHatchGeometry.Build(length, depth, HatchScrubberStrip.Scaled(spacing, depth), thin: true);
			var strip = lines.Concat(current).ToHashSet();
			Assert.Equal(edge.Count, strip.Count);
			Assert.True(edge.All(strip.Contains));
		}

		[Fact]
		public void Nothing_loaded_draws_nothing()
		{
			var (lines, current) = HatchScrubberStrip.Build(420, 24, 5, new double[30], 3);
			Assert.Empty(lines);
			Assert.Empty(current);
		}

		[Fact]
		public void A_line_belongs_to_the_frame_of_its_base_and_grows_with_that_fill()
		{
			const double length = 400, depth = 24;
			var fills = new double[] { 1, 0.5, 0, 0 };
			var (lines, _) = HatchScrubberStrip.Build(length, depth, 5, fills, -1);
			Assert.NotEmpty(lines);
			foreach (var s in lines)
			{
				double baseX = s.X1 - (s.X2 > s.X1 ? s.Y1 : -s.Y1); // walk back along the 45° line to y = 0
				int frame = HatchScrubberStrip.FrameOf(baseX, length / fills.Length, fills.Length);
				Assert.True(fills[frame] > 0);
				Assert.True(s.Y2 <= depth * fills[frame] + 1e-9); // a half-loaded frame reaches at most half the depth
			}
		}

		[Fact]
		public void The_frame_on_screen_comes_back_apart()
		{
			const double length = 400;
			var fills = Enumerable.Repeat(1.0, 4).ToArray();
			var (lines, current) = HatchScrubberStrip.Build(length, 24, 5, fills, 2);
			Assert.NotEmpty(current);
			foreach (var s in current)
			{
				double baseX = s.X1 - (s.X2 > s.X1 ? s.Y1 : -s.Y1);
				Assert.Equal(2, HatchScrubberStrip.FrameOf(baseX, length / 4, 4));
			}
			Assert.DoesNotContain(lines, s => HatchScrubberStrip.FrameOf(s.X1 - (s.X2 > s.X1 ? s.Y1 : -s.Y1), length / 4, 4) == 2);
		}

		[Fact]
		public void The_scrubber_look_is_its_own_not_the_edges()
		{
			var s = new Anvil.Services.AppSettings();
			var look = new Anvil.ViewModels.HatchScrubberLookViewModel(s);
			var edge = new Anvil.ViewModels.EdgeHatchViewModel(s);

			// Defaults = the user's mock-up picks, not the edge's.
			Assert.Equal(HatchScrubberLook.Accent, look.ColorHex);
			Assert.Equal(5, look.Spacing);
			Assert.Equal(1, look.Weight);
			Assert.Equal(60, look.Opacity);

			look.ColorHex = "#22d3ee";
			look.Spacing = 8;
			look.Weight = 1.5;
			look.Opacity = 40;
			Assert.Equal("#22D3EE", s.HatchScrubberColor);
			Assert.Equal(3, look.ColorIndex);
			Assert.Equal(0.4, look.LineOpacity, 9);

			// The edge never moved.
			Assert.Equal(EdgeHatchColors.Surface, edge.ColorHex);
			Assert.Equal(EdgeHatchSizes.DefaultSpacing, edge.Spacing);
			Assert.Equal(EdgeHatchSizes.DefaultWeight, edge.Weight);
			Assert.Equal(EdgeHatchSizes.DefaultOpacity, edge.Opacity);

			look.RestoreDefaults();
			Assert.Equal(HatchScrubberLook.Accent, s.HatchScrubberColor);
			Assert.Equal(60, look.Opacity);
		}

		[Fact]
		public void Spacing_scales_with_the_strip_depth() =>
			Assert.Equal(5.0 * 24 / EdgeHatchSizes.DefaultWidth, HatchScrubberStrip.Scaled(5, 24), 9);
	}
}
