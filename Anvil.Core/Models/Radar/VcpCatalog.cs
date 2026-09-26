using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>Which radar network a scan pattern belongs to.</summary>
	public enum VcpNetwork { Wsr88d, Tdwr }

	/// <summary>
	/// The scan regime a pattern runs in — what the readout's regime word ("clear-air", "precip",
	/// "TDWR monitor", "TDWR hazardous") says.
	/// </summary>
	public enum VcpRegime { ClearAir, Precip, TdwrMonitor, TdwrHazardous }

	/// <summary>
	/// One volume coverage pattern: the number a volume reports, plus everything the app says about it.
	/// </summary>
	/// <param name="Number">The pattern number as Message 5 / the VOL block carries it.</param>
	/// <param name="Retired">No longer run by any site — kept because PastCast replays the archive back to
	/// 1991, where a retired pattern is a legitimate read.</param>
	/// <param name="Name">Short technical name ("SZ-2 severe convective").</param>
	/// <param name="Tilts">Distinct elevation angles designed into the pattern (the tilt-picker count before
	/// AVSET trims it). 0 = site-specific (TDWR).</param>
	/// <param name="VolumeMinutes">Nominal full-volume time, in words ("~4.5"). A sanity band, never a
	/// schedule — AVSET shortens precip volumes and SAILS/MRLE lengthen them.</param>
	/// <param name="SailsMax">Most supplemental low-tilt scans an operator can add (0 = none).</param>
	/// <param name="Summary">Plain-language explanation — no jargon, for the glossary's Now line.</param>
	public sealed record VcpInfo(
		int Number,
		VcpNetwork Network,
		VcpRegime Regime,
		bool Retired,
		string Name,
		int Tilts,
		string VolumeMinutes,
		int SailsMax,
		string Summary)
	{
		/// <summary>The readout's regime word. ⚠️ The Atlas tile and the glossary parse these words back out
		/// of the mode line — rename one and grep for it.</summary>
		public string RegimeLabel => Regime switch
		{
			VcpRegime.ClearAir => "clear-air",
			VcpRegime.Precip => "precip",
			VcpRegime.TdwrMonitor => "TDWR monitor",
			_ => "TDWR hazardous",
		};
	}

	/// <summary>
	/// THE list of volume coverage patterns — the one source of truth for which VCP numbers are real and what
	/// each one means. <c>Level2Format.IsKnownVcp</c>, the mode readout's regime word, and the glossary's
	/// scan-pattern card all read from here.
	/// </summary>
	/// <remarks>
	/// ⚠️ THIS IS AN ALLOW-LIST WITH TEETH: a number missing here fails <c>IsKnownVcp</c>, which EMPTIES the
	/// tilt picker and reads "VCP ?" on every site running it. That shipped twice (TDWR 80, then clear-air 34)
	/// while the list was three scattered HashSets. <c>VcpCatalogTests.EveryDeployedVcpIsRecognised</c> guards
	/// it. Add a new pattern HERE and nowhere else.
	/// Sources: ROC "New VCP Paradigm" (2015), RAC 2025 VCP sheet, NCEI TDWR notes — distilled in
	/// <c>docs/radar/NEXRAD WSR-88D VCP &amp; Scan Strategy technical reference.md</c>.
	/// TDWR 80/90 are NOT WSR-88D patterns: the FAA's terminal radar is translated by the Supplemental
	/// Product Generator into these two pseudo-VCPs, and only T-prefixed sites (TOKC, TDAL…) ever carry them.
	/// Their tilt angles are site-specific, hence <c>Tilts = 0</c>.
	/// </remarks>
	public static class VcpCatalog
	{
		public static IReadOnlyList<VcpInfo> All { get; } = new VcpInfo[]
		{
			// ── WSR-88D precipitation (operational) ─────────────────────────────────────────────────
			new(12, VcpNetwork.Wsr88d, VcpRegime.Precip, false, "Severe convective", 14, "~4.5", 3,
				"Built for fast-changing severe storms: tilts packed low to the ground and the quickest full " +
				"volume the radar can do."),
			new(212, VcpNetwork.Wsr88d, VcpRegime.Precip, false, "SZ-2 severe convective", 14, "~4.5", 3,
				"The usual choice for severe storms. Same tilts as 12, plus a pulse trick that stops distant " +
				"echoes from folding back and hiding nearby velocity."),
			new(112, VcpNetwork.Wsr88d, VcpRegime.Precip, false, "Tropical / strong-wind", 14, "~5.5", 1,
				"For widespread strong winds such as hurricanes: extra Doppler passes at different pulse rates so " +
				"very fast winds can be measured without folding."),
			new(215, VcpNetwork.Wsr88d, VcpRegime.Precip, false, "General surveillance", 15, "~6", 1,
				"The everyday rain pattern: more tilts spread high into the sky, trading a little speed for a " +
				"fuller picture of the whole storm."),

			// ── WSR-88D clear air (operational) ─────────────────────────────────────────────────────
			new(35, VcpNetwork.Wsr88d, VcpRegime.ClearAir, false, "SZ-2 clear air", 9, "~7", 1,
				"The modern quiet-weather pattern: nine low tilts, sensitive enough for light rain and snow, " +
				"fast enough to catch showers forming."),
			new(31, VcpNetwork.Wsr88d, VcpRegime.ClearAir, false, "Clear air, long pulse", 5, "~10", 0,
				"The most sensitive pattern: a long pulse and a slow turn that picks up dust, insects, fronts " +
				"and very light snow."),
			new(34, VcpNetwork.Wsr88d, VcpRegime.ClearAir, false, "SZ-2 clear air, long pulse", 7, "~10", 1,
				"A slow, sensitive quiet-weather pattern with better handling of distant echoes than 31."),

			// ── TDWR (Terminal Doppler Weather Radar) ───────────────────────────────────────────────
			new(90, VcpNetwork.Tdwr, VcpRegime.TdwrMonitor, false, "TDWR monitor", 0, "~6", 0,
				"An airport radar's quiet mode: one long-range sweep, then short-range tilts over the terminal " +
				"area. It runs when no hazardous weather is near the airport."),
			new(80, VcpNetwork.Tdwr, VcpRegime.TdwrHazardous, false, "TDWR hazardous", 0, "~6", 0,
				"An airport radar's storm mode, switched on when rain or wind shear is near the airport. It " +
				"re-scans its lowest tilt about once a minute to watch for microbursts."),

			// ── WSR-88D retired (archive only) ──────────────────────────────────────────────────────
			new(11, VcpNetwork.Wsr88d, VcpRegime.Precip, true, "Severe convective (original)", 14, "~5", 0,
				"The original severe-storm pattern, retired in the 2015 overhaul in favour of 12 and 212."),
			new(21, VcpNetwork.Wsr88d, VcpRegime.Precip, true, "Stratiform (original)", 9, "~6", 0,
				"The original steady-rain pattern: nine tilts, slower than the storm patterns. Retired in 2015."),
			new(121, VcpNetwork.Wsr88d, VcpRegime.Precip, true, "MPDA", 9, "~6", 0,
				"A steady-rain pattern with several Doppler passes per tilt to untangle strong winds, used for " +
				"tropical systems before 112 replaced it."),
			new(211, VcpNetwork.Wsr88d, VcpRegime.Precip, true, "SZ-2 severe (original)", 14, "~5", 0,
				"Pattern 11 with the SZ-2 pulse trick added. Retired in 2015."),
			new(221, VcpNetwork.Wsr88d, VcpRegime.Precip, true, "SZ-2 stratiform (original)", 9, "~6", 0,
				"Pattern 21 with the SZ-2 pulse trick added. Retired in 2015."),
			new(32, VcpNetwork.Wsr88d, VcpRegime.ClearAir, true, "Clear air, short pulse", 5, "~10", 0,
				"The short-pulse twin of 31, replaced by 35."),
		};

		private static readonly Dictionary<int, VcpInfo> ByNumber = All.ToDictionary(v => v.Number);

		/// <summary>The pattern for <paramref name="number"/>, or null when it isn't a real VCP (a bad parse).</summary>
		public static VcpInfo? Find(int number) => ByNumber.TryGetValue(number, out var info) ? info : null;

		public static bool IsKnown(int number) => ByNumber.ContainsKey(number);

		/// <summary>Operational patterns of a regime, in catalog order — for "which ones exist" sentences.</summary>
		public static IEnumerable<int> Operational(VcpRegime regime) =>
			All.Where(v => !v.Retired && v.Regime == regime).Select(v => v.Number);
	}
}
