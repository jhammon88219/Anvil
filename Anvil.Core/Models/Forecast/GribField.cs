using System;

namespace Anvil.Models
{
	/// <summary>
	/// ONE decoded GRIB2 message: a single field (composite reflectivity, updraft helicity…) at one time, on its
	/// grid. <see cref="Values"/> is row-major, SOUTH row first (see <see cref="LambertConformalGrid"/>), in the
	/// field's own units; a missing point is <see cref="float.NaN"/>.
	/// </summary>
	/// <param name="Discipline">GRIB2 discipline (0 = meteorological).</param>
	/// <param name="Category">Parameter category (16 = forecast radar imagery, 7 = thermodynamic stability).</param>
	/// <param name="Number">Parameter number within the category (16/196 = REFC).</param>
	/// <param name="RunUtc">The model run (reference time).</param>
	/// <param name="ValidUtc">The moment the field is FOR — for a max-over-interval field, the interval's END.</param>
	/// <param name="IntervalStartUtc">For a max-over-interval field (template 4.8), the interval's start; else null.</param>
	public sealed record GribField(
		int Discipline, int Category, int Number,
		DateTimeOffset RunUtc, DateTimeOffset ValidUtc, DateTimeOffset? IntervalStartUtc,
		LambertConformalGrid Grid, float[] Values)
	{
		public TimeSpan Lead => ValidUtc - RunUtc;

		public float At(int i, int j) => Values[j * Grid.Nx + i];
	}
}
