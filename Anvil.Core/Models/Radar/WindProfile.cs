using System;
using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>One level of a vertical wind profile (a hodograph point).</summary>
	/// <param name="HeightAglM">Height above RADAR level, metres. ⚠️ NOT MSL — Bunkers layers are AGL/ARL,
	/// and the NVW product reports MSL, so the radar's own height has already been subtracted.</param>
	/// <param name="U">Eastward wind component, m/s.</param>
	/// <param name="V">Northward wind component, m/s.</param>
	/// <param name="SpeedKt">Wind speed as reported by the source, knots (diagnostics + cross-check).</param>
	/// <param name="DirectionFromDeg">Meteorological direction the wind blows FROM, degrees.</param>
	/// <param name="RmsKt">Per-level RMS fit residual, knots. The quality number to threshold on.</param>
	public sealed record WindProfileLevel(
		double HeightAglM,
		double U,
		double V,
		double SpeedKt,
		double DirectionFromDeg,
		double RmsKt);

	/// <summary>A vertical wind profile from some provider, ascending by height.</summary>
	/// <remarks>
	/// Units are m/s and metres AGL internally — convert to kt/ft only at the view-model boundary.
	/// <para><paramref name="Source"/> must reach the UI: a Bunkers vector derived from the NWS's own VAD and
	/// one derived from our Level II retrieval are not the same claim.</para>
	/// </remarks>
	public sealed record WindProfile(
		IReadOnlyList<WindProfileLevel> Levels,
		DateTime ValidTimeUtc,
		string Source,
		string SiteId,
		double RadarHeightFtMsl);
}
