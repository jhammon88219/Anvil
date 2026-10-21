using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>
	/// The colour tables of the model fields that radar has none for. Reflectivity is NOT here: a forecast dBZ draws
	/// with radar's own table (<c>radar-ramps.js</c>, the legend from the page's <c>radarRamps</c> push), so the HRRR and
	/// live radar look identical. Colours are DATA, never themed.
	/// </summary>
	/// <remarks>
	/// ⚠️ The page receives these stops in each frame's descriptor (<see cref="Services.ModelFieldFrames"/>), so this is
	/// the ONE copy — the legend and the map draw from it.
	/// </remarks>
	public static class ModelRamps
	{
		/// <summary>Radar's reflectivity table (the page's; the legend is radar's own <c>RadarRampInfo</c>).</summary>
		public const string Reflectivity = "reflectivity";

		public const string UpdraftHelicity = "uh";

		/// <summary>
		/// 2–5 km updraft helicity, m²/s², banded. 25 is the floor of a mark, 75 the usual "rotating storm" line (SPC's
		/// HREF uses it), 150+ strong, 250+ violent.
		/// </summary>
		public static RadarRampInfo UpdraftHelicityRamp { get; } = new()
		{
			Id = UpdraftHelicity,
			Label = "Updraft helicity",
			Unit = "m²/s²",
			Min = 25,
			Max = 300,
			Interpolate = false,
			Stops = new List<RadarRampStop>
			{
				new() { V = 25, Color = new[] { 60, 179, 113 } },
				new() { V = 50, Color = new[] { 242, 214, 0 } },
				new() { V = 75, Color = new[] { 242, 138, 0 } },
				new() { V = 100, Color = new[] { 224, 32, 32 } },
				new() { V = 150, Color = new[] { 192, 0, 192 } },
				new() { V = 250, Color = new[] { 122, 0, 200 } },
			},
		};

		/// <summary>A non-radar ramp by id, or null (reflectivity is radar's).</summary>
		public static RadarRampInfo? ById(string id) => id == UpdraftHelicity ? UpdraftHelicityRamp : null;
	}
}
