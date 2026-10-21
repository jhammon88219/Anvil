using System;

namespace Anvil.Models
{
	/// <summary>
	/// How a model field's value becomes the ONE BYTE per grid cell the page draws from (<c>model-field.js</c>):
	/// <c>code = round(value × Scale + Offset)</c>, clamped to 1…255; code 0 = nothing drawn (below
	/// <see cref="MinShown"/>, or missing). The page turns a code back into a value with <see cref="ValueOf"/>'s rule.
	/// </summary>
	/// <remarks>
	/// ⚠️ MIRRORED in <c>model-field.js</c> (it builds each field's colour table by walking codes 1…255 through
	/// value = (code − Offset) / Scale) — the numbers ride in every frame's descriptor, so only the RULE is mirrored.
	/// </remarks>
	public sealed record ModelFieldEncoding(double Scale, double Offset, double MinShown)
	{
		/// <summary>Reflectivity in Level III's steps: 0.5 dBZ, −32 dBZ = code 2; under 10 dBZ isn't drawn (radar's MIN_DBZ).</summary>
		public static readonly ModelFieldEncoding Reflectivity = new(2, 66, 10);

		/// <summary>Updraft helicity in 2 m²/s² steps to 508; under 25 m²/s² isn't drawn (no rotating updraft worth a mark).</summary>
		public static readonly ModelFieldEncoding UpdraftHelicity = new(0.5, 1, 25);

		public byte Code(float value)
		{
			if (float.IsNaN(value) || value < MinShown) return 0;
			var c = Math.Round(value * Scale + Offset, MidpointRounding.AwayFromZero);
			return (byte)Math.Clamp(c, 1, 255);
		}

		public double ValueOf(byte code) => (code - Offset) / Scale;
	}
}
