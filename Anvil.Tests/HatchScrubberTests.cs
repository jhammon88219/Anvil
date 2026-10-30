using System.Linq;
using Anvil.Models;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>The hatch scrubber: its style token, its fill ladder and its cell geometry.</summary>
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

		[Theory]
		[InlineData(0.0, false, HatchScrubberLadder.Empty)]
		[InlineData(0.1, false, HatchScrubberLadder.Downloading)]
		[InlineData(0.49, false, HatchScrubberLadder.Downloading)]
		[InlineData(0.5, false, HatchScrubberLadder.Building)]
		[InlineData(0.9, false, HatchScrubberLadder.Building)]   // built but not yet revealed (MaxUnlitFill) never reads ready
		[InlineData(1.0, false, HatchScrubberLadder.Ready)]
		[InlineData(0.0, true, HatchScrubberLadder.Ready)]
		[InlineData(double.NaN, false, HatchScrubberLadder.Empty)]
		public void Ladder_follows_the_fill(double fill, bool ready, int expected) =>
			Assert.Equal(expected, HatchScrubberLadder.Level(fill, ready));

		[Fact]
		public void Slant_never_exceeds_half_the_cell()
		{
			Assert.Equal(6, HatchScrubberGeometry.Slant(20, 12));
			Assert.Equal(2, HatchScrubberGeometry.Slant(4, 12));
			Assert.Equal(0, HatchScrubberGeometry.Slant(0, 12));
		}

		[Fact]
		public void Empty_cell_has_no_lines()
		{
			Assert.Empty(HatchScrubberGeometry.Lines(20, 12, HatchScrubberLadder.Empty));
			Assert.Empty(HatchScrubberGeometry.Lines(0, 12, HatchScrubberLadder.Ready));
		}

		[Fact]
		public void Lines_stay_inside_the_parallelogram_and_thicken_with_progress()
		{
			const double w = 20, h = 12;
			var outline = HatchScrubberGeometry.Outline(w, h);
			int Count(int level) => HatchScrubberGeometry.Lines(w, h, level).Count;

			foreach (var level in new[] { HatchScrubberLadder.Downloading, HatchScrubberLadder.Building, HatchScrubberLadder.Ready })
			{
				foreach (var s in HatchScrubberGeometry.Lines(w, h, level))
				{
					Assert.True(Inside(outline, s.X1, s.Y1) && Inside(outline, s.X2, s.Y2), $"level {level}: {s}");
				}
			}
			Assert.True(Count(HatchScrubberLadder.Downloading) < Count(HatchScrubberLadder.Building));
			Assert.True(Count(HatchScrubberLadder.Building) < Count(HatchScrubberLadder.Ready));
			// Only ready cells cross: every building line leans the same way ("/": x grows as y shrinks).
			Assert.All(HatchScrubberGeometry.Lines(w, h, HatchScrubberLadder.Building), s => Assert.True((s.X2 - s.X1) * (s.Y2 - s.Y1) < 0));
			Assert.Contains(HatchScrubberGeometry.Lines(w, h, HatchScrubberLadder.Ready), s => (s.X2 - s.X1) * (s.Y2 - s.Y1) > 0);
		}

		[Fact]
		public void Clip_rejects_a_segment_outside()
		{
			var outline = HatchScrubberGeometry.Outline(20, 12);
			Assert.Null(HatchScrubberGeometry.Clip(outline, 30, 0, 40, 12));
			Assert.NotNull(HatchScrubberGeometry.Clip(outline, -5, 6, 25, 6));
		}

		private static bool Inside((double X, double Y)[] poly, double x, double y)
		{
			// Convex: on the same side of every edge (with a small tolerance for the clip's endpoints).
			int sign = 0;
			for (int i = 0; i < poly.Length; i++)
			{
				var a = poly[i];
				var b = poly[(i + 1) % poly.Length];
				double cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
				if (System.Math.Abs(cross) < 1e-6) { continue; }
				int s = cross > 0 ? 1 : -1;
				if (sign == 0) { sign = s; } else if (s != sign) { return false; }
			}
			return true;
		}
	}
}
