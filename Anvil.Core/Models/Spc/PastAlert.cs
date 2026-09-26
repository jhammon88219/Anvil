using System;

namespace Anvil.Models
{
	/// <summary>
	/// One historical alert AREA for one span of time — a warning's polygon VERSION (a follow-up statement
	/// can reshape it) or one county of a watch. The map draws it while the displayed radar frame is inside
	/// [<see cref="Start"/>, <see cref="End"/>).
	/// </summary>
	/// <param name="Key">The ALERT it belongs to, so N rows of one watch (its counties) or one warning (its
	/// versions) count once: "OUN.TO.W.24.2013" for a warning (ETNs are per office), "TO.A.191.2013" for a
	/// watch (watch numbers are national).</param>
	/// <param name="Phenom">TO / SV / FF — the code the map colours and filters by.</param>
	/// <param name="Tier">The damage-threat tier (0/1/2, see <c>WarningService.ThreatTier</c>); 0 for watches.</param>
	public sealed record PastAlert(string Key, string Phenom, int Tier, DateTimeOffset Start, DateTimeOffset End)
	{
		public bool IsInEffectAt(DateTimeOffset t) => Start <= t && t < End;
	}
}
