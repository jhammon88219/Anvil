namespace Anvil.Models
{
	/// <summary>
	/// How a level is DRAWN, which is a property of the product and not a rendering choice: SPC's
	/// probability and category areas are solid translucent fills, while its Conditional Intensity
	/// Groups are black hatching laid OVER whichever fill is underneath.
	/// </summary>
	public enum SpcLevelKind
	{
		/// <summary>A category or probability area — a solid fill.</summary>
		Solid,

		/// <summary>A Conditional Intensity Group (CIG1/2/3) — hatching over the fill below it.</summary>
		ConditionalIntensity,
	}

	/// <summary>
	/// The hatch pattern a <see cref="SpcLevelKind.ConditionalIntensity"/> level is drawn with. These
	/// mirror SPC's own ArcGIS fill styles one-for-one (<c>esriSFSBackwardDiagonal</c> and friends), and
	/// the PATTERN — not the colour — is what tells the groups apart: every CIG level is black.
	/// ⚠️ The direction matters. Drawing CIG1 forward and CIG2 backward (which Anvil did until this
	/// catalog landed) makes our map disagree with SPC's about which intensity group an area is in.
	/// </summary>
	public enum SpcHatchPattern
	{
		/// <summary>Not hatched — a solid level.</summary>
		None,

		/// <summary>CIG1 — <c>\</c> lines.</summary>
		BackwardDiagonal,

		/// <summary>CIG2 — <c>/</c> lines.</summary>
		ForwardDiagonal,

		/// <summary>CIG3 — both directions crossed.</summary>
		DiagonalCross,
	}

	/// <summary>
	/// ONE official SPC risk level — a categorical risk, a probability step, or a Conditional Intensity
	/// Group — with the colours and name SPC itself publishes.
	/// <para>
	/// ⚠️ Every field here is HARVESTED, never authored: <c>tools/make_spc_catalog.py</c> reads NOAA's
	/// published layer symbology into <c>Assets/spc-risk-catalog.json</c>, which
	/// <see cref="Services.SpcRiskCatalog"/> loads. Do not hand-edit a colour into the JSON — the app
	/// previously drew a 15% wind risk red where SPC draws it yellow precisely because these values
	/// were once transcribed by hand.
	/// </para>
	/// </summary>
	/// <param name="Code">
	/// The short code the GeoJSON feeds and the IEM archive key on — <c>"MRGL"</c>, <c>"0.15"</c>,
	/// <c>"CIG1"</c>, <c>"ELEV"</c>. This is the join key for colouring a feature, and it matches the
	/// feed's <c>LABEL</c> property exactly.
	/// </param>
	/// <param name="Dn">
	/// SPC's own severity ordinal, and the ONLY thing a scale is ordered by — so the order is SPC's,
	/// not the order someone happened to type the rows in.
	/// ⚠️ It is unique only WITHIN a <see cref="Kind"/>, not within a product: the tornado outlook
	/// carries both a 2% probability level and CIG2 at <c>Dn == 2</c>. Never look a level up by
	/// <see cref="Dn"/> alone across kinds.
	/// ⚠️ A <c>double</c> because the extended fire-weather outlook's ordinals are fractions (0.4, 0.7),
	/// unlike every other product's integers.
	/// </param>
	/// <param name="OfficialName">
	/// SPC's own label for the level — <c>"Marginal"</c>, <c>"15%"</c>, <c>"Extreme"</c>, <c>"CIG1"</c>.
	/// ⚠️ SPC publishes no short adjective for the intensity groups, so a CIG's name really is just
	/// "CIG1". Don't dress it up as "significant"/"considerable" in the catalog; that would be exactly
	/// the invented wording this whole file exists to remove.
	/// </param>
	/// <param name="Fill">The area fill, as <c>#RRGGBB</c>.</param>
	/// <param name="Stroke">The outline, as <c>#RRGGBB</c>. Occasionally equal to <paramref name="Fill"/>
	/// (SPC draws the 60% tornado area that way).</param>
	/// <param name="Kind">Solid area vs. conditional-intensity hatching.</param>
	/// <param name="Hatch">The hatch direction, for a <see cref="SpcLevelKind.ConditionalIntensity"/>
	/// level; <see cref="SpcHatchPattern.None"/> otherwise.</param>
	public record SpcRiskLevel(
		string Code,
		double Dn,
		string OfficialName,
		string Fill,
		string Stroke,
		SpcLevelKind Kind,
		SpcHatchPattern Hatch)
	{
		/// <summary>True for a Conditional Intensity Group — the levels drawn as hatching.</summary>
		public bool IsConditionalIntensity => Kind == SpcLevelKind.ConditionalIntensity;
	}
}
