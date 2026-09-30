using System;

namespace Anvil.Models
{
	/// <summary>
	/// One active warning as a place to FLY TO — what the NowCast tiles' arrows step through
	/// (<c>WarningStepper</c>). Built by <c>WarningService</c> from the same merged set the map draws.
	/// </summary>
	/// <param name="Id">The CAP URN — the merge key, stable across refreshes.</param>
	/// <param name="Phenom">TO / SV / FF.</param>
	/// <param name="Tier">0 base · 1 considerable (PDS) · 2 catastrophic/destructive (an Emergency).</param>
	/// <param name="Sent">When NWS issued this version (CAP <c>sent</c>); <see cref="DateTimeOffset.MinValue"/> if unknown.</param>
	/// <param name="Place">The first county/zone of CAP's <c>areaDesc</c> ("Cleveland, OK"); may be empty.</param>
	/// <param name="GeometryJson">The polygon as GeoJSON — the page flashes it with this, so the flash works
	/// even while the warning layer isn't drawn.</param>
	public sealed record WarningTarget(string Id, string Phenom, int Tier, DateTimeOffset Sent, string Place,
		string GeometryJson, double West, double South, double East, double North);
}
