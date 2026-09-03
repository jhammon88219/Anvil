using System;

namespace Anvil.Models
{
	/// <summary>Why no storm motion could be produced. ⚠️ "Storm motion is blank" must ALWAYS be traceable to
	/// one of these — a bare null is what makes the field undiagnosable in the UI.</summary>
	public enum StormMotionFailure
	{
		None = 0,
		NoProfile,
		InsufficientDepth,     // no 5500-6000 m level
		InsufficientSurface,   // no level at or below 500 m
		TooFewLevels,
		GapTooLarge,
		DegenerateShear,       // |shear| < 1 m/s, so "right of the shear" is undefined
	}

	/// <summary>A wind or motion vector in m/s components.</summary>
	public readonly record struct MotionVector(double U, double V)
	{
		public double SpeedMs => Math.Sqrt((U * U) + (V * V));

		public double SpeedKt => SpeedMs / 0.514444;

		/// <summary>Bearing the storm MOVES TOWARD, degrees. ⚠️ Note the argument order — Atan2(U, V) here,
		/// but Atan2(V, U) for a meteorological FROM direction. Crossing them is a classic 180° bug.</summary>
		public double HeadingDeg
		{
			get
			{
				var d = (Math.Atan2(U, V) * 180.0 / Math.PI) % 360.0;
				return d < 0 ? d + 360.0 : d;
			}
		}
	}

	/// <summary>Result of a Bunkers (2000) storm-motion computation.</summary>
	/// <remarks>MeanWind and ShearVector are populated whenever computable, EVEN ON FAILURE, because they are
	/// the useful diagnostics when a profile is rejected.</remarks>
	public sealed record StormMotionResult(
		MotionVector? RightMover,
		MotionVector? LeftMover,
		MotionVector? MeanWind,
		MotionVector? ShearVector,
		string? ProfileSource,
		DateTime ValidTimeUtc,
		StormMotionFailure Failure)
	{
		/// <summary>How many profile levels backed this result. Doc 01 §6 requires it to reach the UI: a
		/// motion from 25 levels and one from 4 are not equally trustworthy.</summary>
		public int LevelCount { get; init; }

		/// <summary>Height of the highest profile level, metres AGL.</summary>
		public double ProfileTopM { get; init; }

		/// <summary>Which estimate this is, for the readout. ⚠️ This path has NO mean-wind fallback tier — a
		/// solution here is always the full Bunkers right-mover — but naming it explicitly keeps the readout
		/// honest if a weaker tier is ever added, rather than a call site asserting "Bunkers R" by hand.</summary>
		public string Tier => HasSolution ? "Bunkers R" : string.Empty;

		public bool HasSolution => Failure == StormMotionFailure.None;

		public static StormMotionResult NoSolution(
			StormMotionFailure reason,
			DateTime validTimeUtc,
			string? source,
			MotionVector? meanWind = null,
			MotionVector? shear = null)
			=> new(null, null, meanWind, shear, source, validTimeUtc, reason);
	}
}
