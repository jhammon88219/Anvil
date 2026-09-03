using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Pins the VWP tilt selection: the chosen cuts must let the MERGED wind profile span 0–6 km inside the
	/// VAD's 10–60 km fit window.
	///
	/// <para>⚠️ The behaviour these tests exist to prevent regressing to is "take the lowest N cuts". In
	/// VCP 212 that gave 0.5–5.1° — eight cuts crowded into the bottom 5 km, none of which sampled the
	/// 5.5–6 km Bunkers shear head anywhere usable, so storm motion fell back to mean wind.</para>
	/// </summary>
	public class VwpTiltSelectionTests
	{
		private const double AeM = 8494667.0;

		// The elevation lists used below are real VCP tables.
		private static readonly float[] Vcp212 =
			{ 0.5f, 0.9f, 1.3f, 1.8f, 2.4f, 3.1f, 4.0f, 5.1f, 6.4f, 8.0f, 10.0f, 12.5f, 15.6f, 19.5f };

		private static readonly float[] Vcp35 =
			{ 0.5f, 0.9f, 1.3f, 1.8f, 2.4f, 3.1f, 4.0f, 5.1f, 6.4f, 10.0f, 14.0f, 19.5f };

		private static double BeamHeightM(double rangeM, double elevDeg)
			=> (rangeM * Math.Sin(elevDeg * Math.PI / 180.0)) + (rangeM * rangeM / (2 * AeM));

		/// <summary>Does any cut in the set sample <paramref name="target"/> inside the 10–60 km fit window?</summary>
		private static bool Covered(IEnumerable<float> cuts, double target)
			=> cuts.Any(a => BeamHeightM(10000, a) <= target && BeamHeightM(60000, a) >= target);

		[Fact]
		public void Vcp212_SetReachesTheBunkersShearHead()
		{
			var chosen = Level2RadarService.SelectVwpTargets(Vcp212);
			var all = chosen.Concat(new[] { Vcp212[0] }).ToList();

			// THE regression: 5.1° grazes 5500 m at exactly 60 km and yields no usable rings there. The set
			// must include something that samples the head layer with real range to spare.
			Assert.Contains(all, a => BeamHeightM(55000, a) >= 5750);
			Assert.True(chosen.Max() > 5.1f,
				$"selection topped out at {chosen.Max()}° — that only grazes the shear head at max range");
		}

		[Fact]
		public void Vcp212_SetCoversTheWholeColumn()
		{
			var all = Level2RadarService.SelectVwpTargets(Vcp212).Concat(new[] { Vcp212[0] }).ToList();
			foreach (var target in new double[] { 250, 1250, 2250, 3250, 4250, 5750 })
			{
				Assert.True(Covered(all, target), $"no chosen cut samples {target} m inside 10–60 km");
			}
		}

		[Fact]
		public void Vcp35_SetCoversTheWholeColumn()
		{
			var all = Level2RadarService.SelectVwpTargets(Vcp35).Concat(new[] { Vcp35[0] }).ToList();
			foreach (var target in new double[] { 250, 1250, 2250, 3250, 4250, 5750 })
			{
				Assert.True(Covered(all, target), $"no chosen cut samples {target} m inside 10–60 km");
			}
		}

		[Fact]
		public void RespectsTheTiltBudgetAndCeiling()
		{
			var chosen = Level2RadarService.SelectVwpTargets(Vcp212);
			Assert.True(chosen.Count <= 7, $"budget is 7 higher cuts, got {chosen.Count}");
			Assert.All(chosen, a => Assert.True(a <= 12.0f, $"{a}° is above the ceiling"));
			Assert.All(chosen, a => Assert.True(a > Vcp212[0], "the base cut must not be re-selected"));
			Assert.Equal(chosen.Distinct().Count(), chosen.Count);
			Assert.Equal(chosen.OrderBy(a => a).ToList(), chosen); // ascending, as the extractor expects
		}

		[Fact]
		public void DegradesGracefullyWhenTheVolumeIsShallow()
		{
			// ⚠️ A volume can lack tilts its VCP advertises (KTLX VCP 212 designs 17 and has shipped 12).
			// A shallow set cannot reach the shear head at all — selection must still return the usable low
			// cuts rather than giving up, so the profile at least supports a mean wind.
			var shallow = new[] { 0.5f, 0.9f, 1.3f, 1.8f, 2.4f };
			var chosen = Level2RadarService.SelectVwpTargets(shallow);
			Assert.NotEmpty(chosen);
			Assert.All(chosen, a => Assert.Contains(a, shallow));
			Assert.DoesNotContain(0.5f, chosen);
		}

		[Fact]
		public void HandlesDegenerateInput()
		{
			Assert.Empty(Level2RadarService.SelectVwpTargets(Array.Empty<float>()));
			Assert.Empty(Level2RadarService.SelectVwpTargets(new[] { 0.5f })); // base only, nothing to add
		}
	}
}
