using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>
	/// One row of SPC's categorical "risk area" table — the box beside the map on an outlook page (Services/Spc/
	/// SpcRiskTableParser): a category, its area, the people in it (SPC's 2010-census estimate) and its largest places.
	/// ⚠️ Each row is that category's OWN ring (the High Risk's people are not also counted in the Moderate).
	/// </summary>
	/// <param name="Code">Categorical catalog code: HIGH / MDT / ENH / SLGT / MRGL.</param>
	public sealed record OutlookRiskArea(string Code, long AreaSqMi, long Population, IReadOnlyList<string> Places);
}
