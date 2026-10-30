using System;
using System.Linq;
using Anvil.Models;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>EDGE-HATCH: the hatch's tokens, sizes and line geometry (<see cref="EdgeHatchGeometry"/>).</summary>
	public class EdgeHatchTests
	{
		[Theory]
		[InlineData(null, "both")]
		[InlineData("bogus", "both")]
		[InlineData("thin", "thin")]
		[InlineData("shadow", "shadow")]
		public void Style_normalizes_to_both(string? token, string expected) =>
			Assert.Equal(expected, EdgeHatchStyles.Normalize(token));

		[Fact]
		public void Style_flags()
		{
			Assert.True(EdgeHatchStyles.Thins("both") && EdgeHatchStyles.Fades("both"));
			Assert.True(EdgeHatchStyles.Thins("thin") && !EdgeHatchStyles.Fades("thin"));
			Assert.True(!EdgeHatchStyles.Thins("fade") && EdgeHatchStyles.Fades("fade"));
			Assert.False(EdgeHatchStyles.IsHatch("shadow"));
			Assert.True(EdgeHatchStyles.IsHatch(null));
			Assert.Equal(EdgeHatchStyles.Tokens.Count, EdgeHatchStyles.Labels.Count);
			Assert.Equal("shadow", EdgeHatchStyles.At(99));
		}

		[Fact]
		public void Sizes_clamp_and_round()
		{
			Assert.Equal(16, EdgeHatchSizes.Width(2));
			Assert.Equal(72, EdgeHatchSizes.Width(500));
			Assert.Equal(35, EdgeHatchSizes.Width(double.NaN));
			Assert.Equal(3, EdgeHatchSizes.Spacing(0));
			Assert.Equal(1.5, EdgeHatchSizes.Weight(1.4));
			Assert.Equal(2, EdgeHatchSizes.Weight(9));
			Assert.Equal(10, EdgeHatchSizes.Opacity(0));
			Assert.Equal(100, EdgeHatchSizes.Opacity(double.NaN));
		}

		[Theory]
		[InlineData(null, "")]
		[InlineData("purple", "")]
		[InlineData("#22d3ee", "#22D3EE")]
		[InlineData("surface", "")]         // retired tokens migrate
		[InlineData("black", "#000000")]
		[InlineData("white", "#FFFFFF")]
		public void Colour_normalizes_to_hex_or_surface(string? token, string expected) =>
			Assert.Equal(expected, EdgeHatchColors.Normalize(token));

		[Fact]
		public void Colour_presets_and_custom()
		{
			Assert.Equal(0, EdgeHatchColors.IndexOf(""));          // the "A" chip = bar surface
			Assert.Equal(EdgeHatchColors.Presets.Count - 1, EdgeHatchColors.IndexOf("black"));
			Assert.Equal(-1, EdgeHatchColors.IndexOf("#123456"));   // custom
			Assert.Equal("", EdgeHatchColors.FromIndex(-5));

			var s = new Anvil.Services.AppSettings();
			var vm = new Anvil.ViewModels.EdgeHatchViewModel(s);
			vm.ColorIndex = EdgeHatchColors.IndexOf("#22D3EE");
			Assert.Equal("#22D3EE", s.EdgeHatchColor);
			vm.ColorHex = "#123456";
			Assert.Equal(-1, vm.ColorIndex);
			Assert.Equal("#123456", vm.ColorHex);
			vm.ColorIndex = -1;                                    // the picker's "custom" index never overwrites
			Assert.Equal("#123456", s.EdgeHatchColor);
		}

		[Fact]
		public void A_retired_faint_white_keeps_its_35_percent()
		{
			var s = new Anvil.Services.AppSettings { EdgeHatchColor = "white" };
			var vm = new Anvil.ViewModels.EdgeHatchViewModel(s);
			Assert.Equal("#FFFFFF", vm.ColorHex);
			Assert.Equal(35, vm.Opacity);
			Assert.Equal(0.35, vm.LineOpacity, 6);

			vm.ColorHex = "#22D3EE";                               // a new colour keeps the opacity it had
			Assert.Equal(35, vm.Opacity);
			vm.Opacity = 80;
			Assert.Equal(80, s.EdgeHatchOpacity);
			Assert.Equal("80%", vm.OpacityText);

			vm.RestoreDefaults();
			Assert.Equal("", s.EdgeHatchColor);
			Assert.Equal(100, vm.Opacity);
		}

		[Fact]
		public void View_model_writes_settings_and_restores_defaults()
		{
			var s = new Anvil.Services.AppSettings();
			var vm = new Anvil.ViewModels.EdgeHatchViewModel(s);
			Assert.Equal(EdgeHatchStyles.IndexOf("both"), vm.StyleIndex);   // the defaults
			Assert.Equal(35, vm.Width);
			Assert.Equal(5, vm.Spacing);
			Assert.Equal(2, vm.Weight);
			Assert.True(vm.IsPaneHatch);

			vm.StyleIndex = EdgeHatchStyles.IndexOf("shadow");
			vm.Width = 50.4;
			vm.Weight = 0.7;
			vm.OnPanes = false;
			Assert.Equal("shadow", s.EdgeHatchStyle);
			Assert.Equal(50, s.EdgeHatchWidth);
			Assert.Equal(0.5, s.EdgeHatchWeight);
			Assert.False(vm.IsHatch || vm.IsPaneHatch);
			Assert.Equal("50 px", vm.WidthText);

			vm.RestoreDefaults();
			Assert.Equal("both", s.EdgeHatchStyle);
			Assert.Equal(35, s.EdgeHatchWidth);
			Assert.True(s.EdgeHatchOnPanes);
		}

		[Fact]
		public void No_band_no_lines()
		{
			Assert.Empty(EdgeHatchGeometry.Build(0, 35, 5, true));
			Assert.Empty(EdgeHatchGeometry.Build(400, 0, 5, true));
			Assert.Empty(EdgeHatchGeometry.Build(double.NaN, 35, 5, false));
		}

		[Fact]
		public void Every_line_starts_on_the_edge_or_a_side_and_stays_in_the_band()
		{
			const double length = 400, depth = 35;
			foreach (var thin in new[] { true, false })
			{
				var lines = EdgeHatchGeometry.Build(length, depth, 5, thin);
				Assert.NotEmpty(lines);
				foreach (var s in lines)
				{
					foreach (var (x, y) in new[] { (s.X1, s.Y1), (s.X2, s.Y2) })
					{
						Assert.InRange(x, -1e-9, length + 1e-9);
						Assert.InRange(y, -1e-9, depth + 1e-9);
					}
					Assert.Equal(Math.Abs(s.X2 - s.X1), Math.Abs(s.Y2 - s.Y1), 6);   // 45°
				}
				Assert.Contains(lines, s => s.X2 > s.X1);                               // both families
				Assert.Contains(lines, s => s.X2 < s.X1);
			}
		}

		[Fact]
		public void Without_thinning_interior_lines_run_the_full_depth()
		{
			var lines = EdgeHatchGeometry.Build(400, 35, 5, thin: false);
			var interior = lines.Where(s => s.Y1 == 0 && s.X1 > 40 && s.X1 < 360).ToList();
			Assert.NotEmpty(interior);
			Assert.All(interior, s => Assert.Equal(35, s.Y2, 6));
		}

		[Fact]
		public void Thinning_halves_the_lines_at_each_step()
		{
			var lines = EdgeHatchGeometry.Build(4000, 32, 4, thin: true);
			var interior = lines.Where(s => s.Y1 == 0 && s.X1 > 100 && s.X1 < 3900).ToList();
			int Reaching(double share) => interior.Count(s => s.Y2 >= share * 32 - 1e-6);
			// Everything reaches ¼; about half reach ½; a quarter ¾; an eighth the full depth.
			Assert.Equal(interior.Count, Reaching(0.25));
			Assert.InRange(Reaching(0.5) / (double)interior.Count, 0.45, 0.55);
			Assert.InRange(Reaching(0.75) / (double)interior.Count, 0.2, 0.3);
			Assert.InRange(Reaching(1.0) / (double)interior.Count, 0.1, 0.15);
		}

		// ── The dot-matrix pattern ──

		[Fact]
		public void Pattern_tokens_and_slider_names()
		{
			Assert.Equal("hatch", EdgeHatchPatterns.Normalize("bogus"));
			Assert.Equal(1, EdgeHatchPatterns.IndexOf("dots"));
			var s = new Anvil.Services.AppSettings();
			var vm = new Anvil.ViewModels.EdgeHatchViewModel(s);
			Assert.False(vm.IsDots);
			Assert.Equal("Line spacing", vm.SpacingLabel);
			vm.PatternIndex = 1;
			Assert.Equal("dots", s.EdgeHatchPattern);
			Assert.True(vm.IsDots);
			Assert.Equal("Dot pitch", vm.SpacingLabel);
			Assert.Equal("Dot size", vm.WeightLabel);
			vm.RestoreDefaults();
			Assert.Equal("hatch", s.EdgeHatchPattern);
		}

		[Fact]
		public void Dots_sit_on_the_pitch_grid_inside_the_band()
		{
			var dots = EdgeHatchGeometry.BuildDots(100, 35, 5, thin: false);
			Assert.Equal(20 * 7, dots.Count);                       // every cell, no thinning
			Assert.All(dots, d =>
			{
				Assert.Equal(d.Y1, d.Y2);
				Assert.InRange(d.X2 - d.X1, 0, EdgeHatchGeometry.DotNudge + 1e-9);
				Assert.Equal(2.5, (d.X1 - 2.5) % 5 + 2.5, 6);       // cell centres
				Assert.InRange(d.Y1, 0, 35);
			});
			Assert.Empty(EdgeHatchGeometry.BuildDots(0, 35, 5, true));
		}

		[Fact]
		public void Dot_thinning_dithers_toward_the_map()
		{
			var dots = EdgeHatchGeometry.BuildDots(4000, 40, 5, thin: true);
			int Row(int j) => dots.Count(d => Math.Abs(d.Y1 - (j + 0.5) * 5) < 1e-6);
			int perRow = 4000 / 5;
			Assert.True(Row(0) > Row(3));                            // denser at the edge…
			Assert.True(Row(3) > Row(7));                            // …sparser toward the map
			Assert.InRange(Row(0) / (double)perRow, 0.85, 1.0);      // density 1 − 2.5/40 ≈ 0.94
			Assert.InRange(Row(7) / (double)perRow, 0.0, 0.15);      // density 1 − 37.5/40 ≈ 0.06
		}

		[Fact]
		public void Reach_follows_the_index()
		{
			Assert.Equal(0.25, EdgeHatchGeometry.Reach(1));
			Assert.Equal(0.5, EdgeHatchGeometry.Reach(2));
			Assert.Equal(0.75, EdgeHatchGeometry.Reach(4));
			Assert.Equal(1.0, EdgeHatchGeometry.Reach(8));
			Assert.Equal(1.0, EdgeHatchGeometry.Reach(16));
		}
	}
}
