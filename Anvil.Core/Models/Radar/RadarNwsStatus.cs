using System;

namespace Anvil.Models
{
	/// <summary>
	/// One radar's SELF-REPORTED state from the NWS radar-stations feed (<c>api.weather.gov/radar/stations</c>):
	/// what the RDA says about itself, raw strings as NWS sends them (<c>null</c> = not reported — TDWRs send
	/// no generator state). Worded for display by <c>RadarNwsStatusViewModel</c>, never here.
	/// </summary>
	/// <remarks>
	/// ⚠️ NOT an availability signal. This answers "is the radar reporting a problem", which is a different
	/// question from the Online/Offline dot ("is data reaching the bucket we load from"): ~25% of healthy,
	/// streaming sites say "Maintenance Action Mandatory" on any given day. Never feed this into
	/// <c>RadarSiteRow.Availability</c>.
	/// </remarks>
	public sealed record RadarNwsStation(
		string Id,
		string? Status,          // "Operate" / "Start-Up"
		string? Operability,     // "RDA - On-line" / "RDA - Maintenance Action Required|Mandatory" / "RDA - Inoperable"
		string? AlarmSummary,    // "No Alarms" or subsystems joined by '|': "Tower/Utilities|Transmitter"
		string? GeneratorState,  // tokens joined by '|': "Utility PWR Available", "Generator On", "Switched to Auxiliary Power"
		DateTimeOffset? LevelTwoLastReceivedUtc);

	/// <summary>
	/// One Free Text Message (FTM) — the written outage notice a WFO issues for its radar ("…WILL BE DOWN FOR
	/// MAINTENANCE FROM 1330Z…"). <see cref="Code"/> is the 3-letter id from the <c>FTMxxx</c> line, which does
	/// NOT say which network: <c>FTMCVG</c> may be KCVG or TCVG — see <c>RadarNwsStatusService.ResolveSiteId</c>.
	/// </summary>
	public sealed record RadarNwsMessage(
		string Code,
		string Office,           // issuing office, e.g. "KRAH" (from the WMO header)
		DateTimeOffset IssuedUtc, // from the WMO header's ddhhmm, NOT the free-form "Message Date:" line
		string Text);
}
