using System.Linq;
using Anvil.Models;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>THE MAP FRAME's corners: each side's band is cut at 45° so the effect TURNS instead of overlapping
	/// (<see cref="EdgeMitre"/>).</summary>
	public class EdgeMitreTests
	{
		[Fact]
		public void Trapezoid_steps_in_by_the_depth_at_a_mitred_end()
		{
			var t = EdgeMitre.Trapezoid(100, 20, mitreStart: true, mitreEnd: true);
			Assert.Equal(new[] { (0.0, 0.0), (100.0, 0.0), (80.0, 20.0), (20.0, 20.0) }, t);
		}

		[Fact]
		public void Trapezoid_without_mitres_is_the_whole_band()
		{
			var t = EdgeMitre.Trapezoid(100, 20, false, false);
			Assert.Equal(new[] { (0.0, 0.0), (100.0, 0.0), (100.0, 20.0), (0.0, 20.0) }, t);
		}

		[Fact]
		public void No_point_of_a_clipped_line_crosses_a_mitre()
		{
			var lines = EdgeHatchGeometry.Build(200, 30, 5, thin: false);
			var clipped = EdgeMitre.Clip(lines, 200, mitreStart: true, mitreEnd: true);
			Assert.NotEmpty(clipped);
			foreach (var s in clipped)
			{
				foreach (var (x, y) in new[] { (s.X1, s.Y1), (s.X2, s.Y2) })
				{
					Assert.True(x >= y - 1e-9, $"({x}, {y}) is past the start mitre");
					Assert.True(x <= 200 - y + 1e-9, $"({x}, {y}) is past the end mitre");
				}
			}
		}

		[Fact]
		public void A_mitre_only_trims_near_its_own_end()
		{
			// Lines in the band's middle, far from both corners, come through untouched.
			var lines = EdgeHatchGeometry.Build(200, 30, 5, thin: false);
			var clipped = EdgeMitre.Clip(lines, 200, mitreStart: true, mitreEnd: true);
			var middle = lines.Where(s => s.X1 is > 60 and < 140 && s.X2 is > 60 and < 140).ToList();
			Assert.All(middle, s => Assert.Contains(s, clipped));
		}

		[Fact]
		public void Unmitred_band_is_unchanged()
		{
			var lines = EdgeHatchGeometry.Build(200, 30, 5, thin: true);
			Assert.Same(lines, EdgeMitre.Clip(lines, 200, false, false));
		}

		[Fact]
		public void Dots_are_kept_or_dropped_whole()
		{
			var dots = EdgeHatchGeometry.BuildDots(100, 30, 5, thin: false);
			var clipped = EdgeMitre.Clip(dots, 100, mitreStart: true, mitreEnd: false);
			Assert.All(clipped, d => Assert.Contains(d, dots));                 // never shortened
			Assert.All(clipped, d => Assert.True(d.X1 >= d.Y1));                // all inside the mitre
			Assert.Contains(dots, d => d.X1 < d.Y1);                            // and some really were outside it
		}
	}
}
