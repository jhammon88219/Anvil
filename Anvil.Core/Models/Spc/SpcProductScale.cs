using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>
	/// The FULL official scale for one SPC product family — every level SPC defines for it, whether or
	/// not today's issuance contains one. That completeness is the point: the legend shows the whole
	/// escalating scale so a reader can see where today's risk sits on it.
	/// <para>
	/// Ordering is least-severe FIRST, with the solid category/probability levels ahead of the
	/// Conditional Intensity Groups — the two are separate ordinal namespaces (see
	/// <see cref="SpcRiskLevel.Dn"/>), so they are ordered independently and concatenated, never
	/// interleaved by <c>Dn</c>.
	/// </para>
	/// <para>
	/// ⚠️ There is deliberately no "no risk" wording here. SPC states it per DAY, not per product — days
	/// 5-6 say "Potential Too Low" where days 7-8 say "Predictability Too Low", and the convective
	/// products say "Less Than 2%/5% All Areas" or "No Thunderstorms Forecast" — and it always arrives
	/// in the feed's own <c>LABEL</c> on the <c>DN == 0</c> feature. Storing one string per product
	/// would mean inventing wording for the days it does not fit.
	/// </para>
	/// </summary>
	public record SpcProductScale(SpcOutlookType Type, IReadOnlyList<SpcRiskLevel> Levels)
	{
		/// <summary>The solid category/probability levels, least severe first.</summary>
		public IEnumerable<SpcRiskLevel> SolidLevels
		{
			get
			{
				foreach (var level in Levels)
				{
					if (level.Kind == SpcLevelKind.Solid)
					{
						yield return level;
					}
				}
			}
		}

		/// <summary>
		/// The Conditional Intensity Groups, lowest first. Empty for a product SPC defines none for
		/// (the categorical outlook and the days 4-8 / fire-weather products); two for hail and the
		/// Day 3 combined product; three for tornado and wind.
		/// </summary>
		public IEnumerable<SpcRiskLevel> IntensityGroups
		{
			get
			{
				foreach (var level in Levels)
				{
					if (level.Kind == SpcLevelKind.ConditionalIntensity)
					{
						yield return level;
					}
				}
			}
		}
	}
}
