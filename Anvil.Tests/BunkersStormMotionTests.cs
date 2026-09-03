using System;
using System.Collections.Generic;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Golden vectors BUNK-01..06 from <c>docs/04-test-vectors.md</c>, computed externally.
	///
	/// <para>⚠️ Do NOT adjust a tolerance to make one of these pass. If the implementation disagrees with a
	/// golden value, either the implementation is wrong or the spec needs a documented amendment — say which.</para>
	///
	/// <para>⚠️ BUNK-01 CANNOT distinguish a trapezoidal layer mean from an unweighted average (its hodograph
	/// is linear on evenly spaced levels, where the two agree). BUNK-02 is the case that can, which is why
	/// both are here. The same pair caught this exact bug in the JS implementation.</para>
	/// </summary>
	public class BunkersStormMotionTests
	{
		private const double Tol = 1e-4;

		private static WindProfile Profile(Func<double, (double U, double V)> gen,
			double topM = 6000.0, double stepM = 250.0, double fromM = 0.0)
		{
			var levels = new List<WindProfileLevel>();
			for (var z = fromM; z <= topM + 1e-9; z += stepM)
			{
				var (u, v) = gen(z);
				levels.Add(new WindProfileLevel(z, u, v, 0, 0, 0));
			}

			return new WindProfile(levels, DateTime.UnixEpoch, "test", "TEST", 0);
		}

		private static WindProfile FromHeights(params double[] heights)
		{
			var levels = new List<WindProfileLevel>();
			foreach (var h in heights)
			{
				levels.Add(new WindProfileLevel(h, 10, 0, 0, 0, 0));
			}

			return new WindProfile(levels, DateTime.UnixEpoch, "test", "TEST", 0);
		}

		// BUNK-02's quarter-circle generator, used by several cases.
		private static (double, double) Bunk02(double z)
		{
			var t = Math.PI / 2 * (z / 6000.0);
			return ((20 * Math.Sin(t)) + 2, (-20 * Math.Cos(t)) + 12);
		}

		[Fact]
		public void Bunk01_StraightWesterly()
		{
			var r = BunkersStormMotion.Compute(Profile(z => (5 + (20 * z / 6000.0), 0)));
			Assert.True(r.HasSolution);
			Assert.Equal(15.0, r.MeanWind!.Value.U, Tol);
			Assert.Equal(0.0, r.MeanWind!.Value.V, Tol);
			Assert.Equal(18.33333, r.ShearVector!.Value.U, Tol);

			// The shear is due east, so the right mover must deviate due SOUTH by exactly D.
			// A +7.5 here instead of -7.5 means the sign in the deviation is inverted.
			Assert.Equal(15.0, r.RightMover!.Value.U, Tol);
			Assert.Equal(-7.5, r.RightMover!.Value.V, Tol);
			Assert.Equal(15.0, r.LeftMover!.Value.U, Tol);
			Assert.Equal(7.5, r.LeftMover!.Value.V, Tol);

			Assert.Equal(16.77051, r.RightMover!.Value.SpeedMs, Tol);
			Assert.Equal(32.599, r.RightMover!.Value.SpeedKt, 1e-2);
			Assert.Equal(116.565, r.RightMover!.Value.HeadingDeg, 1e-2);
			Assert.Equal(63.435, r.LeftMover!.Value.HeadingDeg, 1e-2);
		}

		[Fact]
		public void Bunk02_CurvedHodograph_DistinguishesTheWeightedMean()
		{
			var r = BunkersStormMotion.Compute(Profile(Bunk02));
			Assert.True(r.HasSolution);

			// THE discriminator: an unweighted average of levels gives 14.61874 here, not 14.72785.
			Assert.Equal(14.72785, r.MeanWind!.Value.U, Tol);
			Assert.Equal(-0.72785, r.MeanWind!.Value.V, Tol);

			Assert.Equal(20.03115, r.RightMover!.Value.U, Tol);
			Assert.Equal(-6.03115, r.RightMover!.Value.V, Tol);
			Assert.Equal(9.42455, r.LeftMover!.Value.U, Tol);
			Assert.Equal(4.57545, r.LeftMover!.Value.V, Tol);

			Assert.Equal(20.91941, r.RightMover!.Value.SpeedMs, Tol);
			Assert.Equal(10.47649, r.LeftMover!.Value.SpeedMs, Tol);
			Assert.Equal(106.756, r.RightMover!.Value.HeadingDeg, 1e-2);

			// On a curved hodograph the movers have DIFFERENT speeds. An implementation that mirrors the
			// left mover about the mean wind gives them equal speeds and passes BUNK-01 while failing here.
			Assert.True(Math.Abs(r.RightMover!.Value.SpeedMs - r.LeftMover!.Value.SpeedMs) > 5.0);
		}

		[Fact]
		public void Bunk03_NorthwestFlow_QuadrantStressTest()
		{
			var r = BunkersStormMotion.Compute(
				Profile(z => (-(5 + (20 * z / 6000.0)), -(2 + (8 * z / 6000.0)))));
			Assert.True(r.HasSolution);
			Assert.Equal(-17.78543, r.RightMover!.Value.U, Tol);
			Assert.Equal(0.96358, r.RightMover!.Value.V, Tol);
			Assert.Equal(273.101, r.RightMover!.Value.HeadingDeg, 1e-2);
			Assert.Equal(223.296, r.LeftMover!.Value.HeadingDeg, 1e-2);
		}

		[Fact]
		public void Bunk04_DegenerateShear()
		{
			var r = BunkersStormMotion.Compute(Profile(_ => (12.0, 0.0)));
			Assert.Equal(StormMotionFailure.DegenerateShear, r.Failure);
			Assert.False(r.HasSolution);
			Assert.Null(r.RightMover);

			// Must not throw, must not return NaN, must not pass the mean wind off as the motion.
			Assert.NotNull(r.MeanWind);
			Assert.Equal(12.0, r.MeanWind!.Value.U, Tol);
			Assert.False(double.IsNaN(r.MeanWind!.Value.V));
		}

		[Fact]
		public void Bunk05_InsufficientDepth_NoExtrapolation()
		{
			// Tops at 4250 m: no 5500-6000 m level, so there is no Bunkers motion. The old JS bug was to
			// borrow the highest sampled level as the "6 km" wind and emit a confident vector anyway.
			var r = BunkersStormMotion.Compute(Profile(Bunk02, topM: 4250.0));
			Assert.Equal(StormMotionFailure.InsufficientDepth, r.Failure);
			Assert.Null(r.RightMover);
		}

		[Fact]
		public void Bunk06_NullAndSparseInputs()
		{
			Assert.Equal(StormMotionFailure.NoProfile, BunkersStormMotion.Compute(null).Failure);

			Assert.Equal(StormMotionFailure.TooFewLevels,
				BunkersStormMotion.Compute(new WindProfile(
					new List<WindProfileLevel>(), DateTime.UnixEpoch, "t", "T", 0)).Failure);

			Assert.Equal(StormMotionFailure.TooFewLevels,
				BunkersStormMotion.Compute(FromHeights(0, 6000)).Failure);

			Assert.Equal(StormMotionFailure.GapTooLarge,
				BunkersStormMotion.Compute(FromHeights(0, 250, 500, 6000)).Failure);

			// Every 250 m from 750 m: plenty of levels and full depth, but no surface level.
			Assert.Equal(StormMotionFailure.InsufficientSurface,
				BunkersStormMotion.Compute(Profile(Bunk02, fromM: 750.0)).Failure);
		}

		[Fact]
		public void ConvOne_DirectionConventions()
		{
			// Asserted in ONE test so a crossed pair of conversions cannot pass.
			void Check(double u, double v, double heading)
				=> Assert.Equal(heading, new MotionVector(u, v).HeadingDeg, 1e-3);

			Check(0, 10, 0.0);
			Check(10, 0, 90.0);
			Check(0, -10, 180.0);
			Check(-10, 0, 270.0);
			Check(15, -7.5, 116.565);
			Check(22, 12, 61.390);
		}

		[Fact]
		public void MeanLayerNeverExtrapolatesBelowTheProfile()
		{
			// Profile starts at 200 m; the 0-500 m mean must integrate 200..500 (linear mean at 350 m),
			// not invent a level at 0.
			var levels = new List<WindProfileLevel>
			{
				new(200, 2, 0, 0, 0, 0),
				new(400, 4, 0, 0, 0, 0),
			};
			var m = BunkersStormMotion.MeanLayer(levels, 0, 500);
			Assert.NotNull(m);
			Assert.Equal(3.0, m!.Value.U, 1e-9);
		}
	}
}
