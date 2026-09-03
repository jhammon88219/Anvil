using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Supercell motion by the Internal Dynamics method of Bunkers et al. (2000): the 0–6 km mean wind, plus
	/// a fixed 7.5 m/s deviation perpendicular to the 0–6 km shear — right for a right-mover, left for a
	/// left-mover. Pure function: no I/O, no logging, no clock.
	/// </summary>
	/// <remarks>
	/// Contract and constants: <c>docs/03-bunkers-storm-motion-spec.md</c>. Golden values:
	/// <c>docs/04-test-vectors.md</c> BUNK-01..06, pinned by <c>Anvil.Tests/BunkersStormMotionTests.cs</c>.
	///
	/// <para>⚠️ NEVER EXTRAPOLATE. If the profile does not actually reach 5500–6000 m there is no shear head
	/// and there is no Bunkers motion — return the named failure. Substituting the highest sampled level
	/// manufactures a confident vector that is not grounded in observation, which is the worst failure mode
	/// in this domain because it renders as a clean arrow on a map.</para>
	///
	/// <para>⚠️ THE LAYER MEAN IS HEIGHT-WEIGHTED (trapezoidal), not a plain average of levels. Profile levels
	/// are irregularly spaced by construction, so an unweighted mean silently over-weights whichever part of
	/// the layer is densely sampled. BUNK-01 passes either way; BUNK-02 is the case that tells them apart.</para>
	///
	/// <para>⚠️ There is a SECOND implementation of this math in <c>Assets/Map/js/radar-decode.js</c>
	/// (<c>bunkersFromProfile</c>), used by the Level II VAD path in the WebView, and a Python mirror in
	/// <c>tools/storm_motion_check.py</c>. Three copies of one algorithm is a drift hazard — the intended
	/// convergence is for the JS path to hand its profile to this function instead. Until then, a change here
	/// must be mirrored there.</para>
	/// </remarks>
	public static class BunkersStormMotion
	{
		/// <summary>Deviation magnitude, m/s, right of the 0–6 km shear. Bunkers et al. (2000).
		/// ⚠️ Do not tune per storm — it was chosen to minimise bulk error across a development dataset.</summary>
		public const double DeviationMs = 7.5;

		public const double MeanWindTopM = 6000.0;
		public const double ShearTailTopM = 500.0;
		public const double ShearHeadBottomM = 5500.0;
		public const double ShearHeadTopM = 6000.0;

		/// <summary>Below this the direction orthogonal to the shear is undefined (doc 03 §3 step 7).</summary>
		public const double MinShearMs = 1.0;

		/// <summary>Max allowed gap between consecutive levels inside 0–6 km (doc 01 §5).</summary>
		public const double MaxGapM = 1500.0;

		/// <summary>Minimum levels inside 0–6 km for the mean to be representative (doc 01 §5).</summary>
		public const int MinLevels = 4;

		/// <summary>Computes right- and left-mover motions, or a named failure. Never throws.</summary>
		public static StormMotionResult Compute(WindProfile? profile)
		{
			var when = profile?.ValidTimeUtc ?? default;
			var source = profile?.Source;

			// ⚠️ NoProfile is for a MISSING profile only. A profile that exists but carries no levels is
			// TooFewLevels — the distinction matters because they mean different things to a provider chain:
			// "this source returned nothing" vs "this source answered, and the answer is unusable".
			if (profile is null || profile.Levels is null)
			{
				return StormMotionResult.NoSolution(StormMotionFailure.NoProfile, when, source);
			}

			var levels = profile.Levels.OrderBy(static l => l.HeightAglM).ToList();

			// --- coverage validation (doc 03 §5). Order matters: it decides WHICH failure is reported. ---
			var inLayer = levels.Where(l => l.HeightAglM >= 0 && l.HeightAglM <= MeanWindTopM).ToList();
			if (inLayer.Count < MinLevels)
			{
				return StormMotionResult.NoSolution(StormMotionFailure.TooFewLevels, when, source);
			}

			if (!levels.Any(l => l.HeightAglM <= ShearTailTopM))
			{
				return StormMotionResult.NoSolution(StormMotionFailure.InsufficientSurface, when, source);
			}

			if (!levels.Any(l => l.HeightAglM >= ShearHeadBottomM && l.HeightAglM <= ShearHeadTopM))
			{
				return StormMotionResult.NoSolution(StormMotionFailure.InsufficientDepth, when, source);
			}

			for (var i = 1; i < inLayer.Count; i++)
			{
				if (inLayer[i].HeightAglM - inLayer[i - 1].HeightAglM > MaxGapM)
				{
					return StormMotionResult.NoSolution(StormMotionFailure.GapTooLarge, when, source);
				}
			}

			// --- the method itself (doc 03 §3) ---
			var mean = MeanLayer(levels, 0, MeanWindTopM);
			var tail = MeanLayer(levels, 0, ShearTailTopM);
			var head = MeanLayer(levels, ShearHeadBottomM, ShearHeadTopM);
			if (mean is null || tail is null || head is null)
			{
				return StormMotionResult.NoSolution(StormMotionFailure.TooFewLevels, when, source);
			}

			var shear = new MotionVector(head.Value.U - tail.Value.U, head.Value.V - tail.Value.V);
			var shearMag = shear.SpeedMs;
			if (shearMag < MinShearMs)
			{
				return StormMotionResult.NoSolution(
					StormMotionFailure.DegenerateShear, when, source, mean, shear);
			}

			// (shear.V, -shear.U)/|shear| is the unit vector 90° CLOCKWISE from the shear — "to the right of"
			// it in a u-east/v-north frame. ⚠️ Getting this sign wrong swaps the movers, and looks almost
			// right on a southwesterly hodograph while being completely wrong on a northwesterly one.
			var rx = DeviationMs * (shear.V / shearMag);
			var ry = DeviationMs * (-shear.U / shearMag);

			var right = new MotionVector(mean.Value.U + rx, mean.Value.V + ry);
			var left = new MotionVector(mean.Value.U - rx, mean.Value.V - ry);

			return new StormMotionResult(right, left, mean, shear, source, when, StormMotionFailure.None)
			{
				LevelCount = levels.Count,
				ProfileTopM = levels[^1].HeightAglM,
			};
		}

		/// <summary>
		/// Height-weighted (trapezoidal) mean wind over [h0,h1], or null when the layer holds no observation.
		/// Endpoints are INTERPOLATED to the layer edges only where the profile brackets them; where it simply
		/// stops, the integral stops with it. Never extrapolates.
		/// </summary>
		internal static MotionVector? MeanLayer(IReadOnlyList<WindProfileLevel> ascending, double h0, double h1)
		{
			var inLayer = ascending.Where(l => l.HeightAglM >= h0 && l.HeightAglM <= h1).ToList();
			if (inLayer.Count == 0)
			{
				return null;
			}

			var knots = new List<(double H, double U, double V)>(inLayer.Count + 2);
			knots.AddRange(inLayer.Select(l => (l.HeightAglM, l.U, l.V)));

			var below = ascending.LastOrDefault(l => l.HeightAglM < h0);
			var above = ascending.FirstOrDefault(l => l.HeightAglM > h1);
			if (below is not null)
			{
				var e = Interpolate(h0, below.HeightAglM, below.U, below.V, knots[0].H, knots[0].U, knots[0].V);
				if (e is not null)
				{
					knots.Insert(0, e.Value);
				}
			}

			if (above is not null)
			{
				var last = knots[^1];
				var e = Interpolate(h1, last.H, last.U, last.V, above.HeightAglM, above.U, above.V);
				if (e is not null)
				{
					knots.Add(e.Value);
				}
			}

			double iu = 0, iv = 0, dz = 0;
			for (var i = 0; i + 1 < knots.Count; i++)
			{
				var d = knots[i + 1].H - knots[i].H;
				if (d <= 0)
				{
					continue;
				}

				iu += (knots[i].U + knots[i + 1].U) / 2 * d;
				iv += (knots[i].V + knots[i + 1].V) / 2 * d;
				dz += d;
			}

			if (dz <= 0)
			{
				// One level (or coincident heights): the trapezoid degenerates to the level itself.
				return new MotionVector(inLayer.Average(l => l.U), inLayer.Average(l => l.V));
			}

			return new MotionVector(iu / dz, iv / dz);
		}

		private static (double H, double U, double V)? Interpolate(
			double at, double h0, double u0, double v0, double h1, double u1, double v1)
		{
			if (h1 <= h0)
			{
				return null;
			}

			var f = (at - h0) / (h1 - h0);
			return (at, u0 + (f * (u1 - u0)), v0 + (f * (v1 - v0)));
		}
	}
}
