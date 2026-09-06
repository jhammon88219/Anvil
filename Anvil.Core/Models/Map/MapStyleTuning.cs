namespace Anvil.Models
{
	/// <summary>
	/// A LEVELS transform over a basemap style's colours — the editable state of the dev style tuner.
	/// </summary>
	/// <remarks>
	/// ⚠️ PARAMETERS, NOT A PALETTE, and that is the whole design. The Data Viz styles are monochrome ramps
	/// (<c>#ffffff</c> → <c>#000000</c>), so the useful edit is the SHAPE of the ramp, not 86 individual
	/// colours: the whole "too bright" pass that preceded this tool was one number. Five doubles persist,
	/// diff and reason about in a way a colour table never would.
	///
	/// Per channel, on the style's PRISTINE value:
	/// <code>
	///   out = Black + pow(v / 255, 1 / Gamma) * (White - Black)
	/// </code>
	/// then, if <see cref="TintStrength"/> is non-zero, blended toward a fully-saturated colour of
	/// <see cref="TintHue"/> at the same lightness.
	///
	/// ⚠️ THE MATH LIVES ONLY IN JS (<c>style-tune.js</c>) — this type just carries the numbers, and the
	/// export asks the page for the resulting colour MAP rather than recomputing it. That is deliberate:
	/// the Bunkers storm-motion maths exists three times and says so in its own remarks; this one is not
	/// going to repeat that.
	///
	/// ⚠️ Identity (<see cref="IsIdentity"/>) is the DEFAULT and must stay cheap to detect — the page skips
	/// the whole tuning path, including a style fetch it would otherwise not do, when nothing is tuned.
	/// </remarks>
	public record MapStyleTuning(
		double White = 255,
		double Black = 0,
		double Gamma = 1.0,
		double TintHue = 210,
		double TintStrength = 0)
	{
		/// <summary>Whether this transform would leave every colour untouched.</summary>
		public bool IsIdentity =>
			White >= 254.5 && Black <= 0.5 && Gamma is > 0.995 and < 1.005 && TintStrength <= 0.001;

		public static MapStyleTuning Default { get; } = new();
	}
}
