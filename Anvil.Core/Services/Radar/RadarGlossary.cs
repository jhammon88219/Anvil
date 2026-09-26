using System;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// THE plain-language glossary for radar readouts — the words behind the Radar Atlas's "?" hints. One
	/// entry per thing the UI shows, each returning a <see cref="RadarGlossaryCard"/> whose "now" line reads
	/// the site's actual value back in words.
	/// </summary>
	/// <remarks>
	/// ⚠️ ONE PLACE FOR THE WORDS. Anything that explains a radar concept to the user reads from here rather
	/// than hard-coding a sentence at the call site, so a correction lands everywhere at once (and so a second
	/// surface — an explain mode, a first-run tour — costs no new prose).
	/// ⚠️ The freshness sentences are pinned to the REAL thresholds: <see cref="RadarSiteStatus.Staleness"/>
	/// decides online/offline, and <see cref="RecentKnee"/> mirrors the bar's amber knee. Retune a threshold
	/// and these sentences must move with it, or the app will explain a rule it no longer follows.
	/// ⚠️ No jargon in Definition; the acronym belongs on the Technical line. Every sentence is written for
	/// someone who has never heard of a VCP.
	/// </remarks>
	public static class RadarGlossary
	{
		/// <summary>Where the age readout leaves green — the bar's amber knee (RadarControls.AgeBrush).</summary>
		public static readonly TimeSpan RecentKnee = TimeSpan.FromMinutes(12);

		// ── Data age ─────────────────────────────────────────────────────────────────────────────
		public static RadarGlossaryCard DataAge(TimeSpan? age) => new(
			"Data age",
			"Time since this site finished a volume",
			"How old the newest sweep is. A full volume takes 4 to 10 minutes depending on the scan " +
			"pattern, then a minute or two more to reach us.",
			DataAgeNow(age),
			$"Under {RecentKnee.TotalMinutes:0} minutes is current. At {RadarSiteStatus.Staleness.TotalMinutes:0} " +
			"minutes a site is treated as offline until a fresh volume lands.");

		private static string DataAgeNow(TimeSpan? age)
		{
			if (age is not { } span) return "No scan time for this site yet.";
			var said = SpokenAge(span);
			if (span <= RecentKnee) return $"{said} old — arriving normally.";
			if (span <= RadarSiteStatus.Staleness) return $"{said} old — later than usual, but still counted as online.";
			return $"{said} old — past the {RadarSiteStatus.Staleness.TotalMinutes:0} minute mark, so this site reads offline until a new volume lands.";
		}

		// ── Scan pattern ─────────────────────────────────────────────────────────────────────────
		/// <summary>Takes the formatted mode line ("VCP 35 · clear-air"), since that's all any caller holds.</summary>
		public static RadarGlossaryCard ScanPattern(string? modeText)
		{
			var vcp = VcpNumber(modeText);
			return new RadarGlossaryCard(
				"Scan pattern",
				vcp is null ? "Volume coverage pattern" : $"Volume coverage pattern {vcp}",
				"The routine the radar repeats: which tilts it sweeps, in what order, and how quickly it " +
				"works through them.",
				ScanPatternNow(modeText, vcp),
				ScanPatternScale(vcp));
		}

		/// <summary>
		/// The bottom bar's Scan-readout tooltip: the same card as the Atlas "?", flattened to plain text
		/// (tooltips take a string), plus the SAILS sentence when the readout shows SAILS/MRLE.
		/// </summary>
		public static string ScanPatternTooltip(string? modeText)
		{
			var card = ScanPattern(modeText);
			var sails = modeText?.Contains("SAILS", StringComparison.Ordinal) == true
				? "\n\nSAILS/MRLE ×N: the radar squeezes N extra sweeps of its lowest tilt into each volume, so " +
				  "the view nearest the ground refreshes more often while weather is changing fast."
				: string.Empty;
			return $"{card.Technical}\n\n{card.Definition}\n\n{card.Now}{sails}\n\n{card.Context}";
		}

		// The scale sentence is BUILT from VcpCatalog so it can never list a pattern the app doesn't know
		// (it once said "31, 32, 35" — missing 34, and 32 long retired).
		private static string ScanPatternScale(int? vcp)
		{
			if (vcp is int n && VcpCatalog.Find(n) is { Network: VcpNetwork.Tdwr })
			{
				return "Airport radars have just two: 90 (monitor) and 80 (hazardous), switching on their own " +
					"when weather nears the airport.";
			}
			return $"Precipitation: {string.Join(", ", VcpCatalog.Operational(VcpRegime.Precip))}. " +
				$"Clear-air: {string.Join(", ", VcpCatalog.Operational(VcpRegime.ClearAir))}. " +
				"Sites switch between them as weather moves in.";
		}

		private static string ScanPatternNow(string? modeText, int? vcp)
		{
			if (string.IsNullOrEmpty(modeText) || modeText == "—") return "This site hasn't reported a scan pattern.";
			if (vcp is int n && VcpCatalog.Find(n) is { } info)
			{
				var tempo = info.Tilts > 0
					? $" {info.Tilts} tilts, about {info.VolumeMinutes} minutes per volume."
					: $" About {info.VolumeMinutes} minutes per volume.";
				var retired = info.Retired ? " Retired — seen only in older archive volumes." : string.Empty;
				return $"{info.Name}. {info.Summary}{tempo}{retired}";
			}
			if (vcp is not null && modeText.Contains(" · unlisted", StringComparison.Ordinal))
			{
				return $"Pattern {vcp} isn't in Anvil's list of standard patterns. It's shown exactly as the radar " +
					"reported it — usually a test pattern on a research radar, or a new one being rolled out. " +
					"Anvil keeps a record of each one it sees.";
			}
			if (modeText.Contains("clear-air", StringComparison.OrdinalIgnoreCase))
			{
				return "Clear-air mode: the dish turns slowly and listens hard, which picks up dust, insects " +
					"and light snow, but takes about 10 minutes per volume.";
			}
			if (modeText.Contains("precip", StringComparison.OrdinalIgnoreCase))
			{
				return "Precipitation mode: faster sweeps, roughly 4 to 6 minutes per volume, so storms stay current.";
			}
			if (modeText.Contains("TDWR", StringComparison.OrdinalIgnoreCase))
			{
				return "A terminal radar's own pattern — it watches a single airport's approaches rather than a wide area.";
			}
			return "An unrecognized pattern, so the app is showing the number the volume reported.";
		}

		// ── Distance ─────────────────────────────────────────────────────────────────────────────
		public static RadarGlossaryCard Distance(double? miles) => new(
			"Distance",
			"Straight-line distance from your location marker",
			"How far the antenna is from you, measured over the ground.",
			DistanceNow(miles),
			"A NEXRAD reaches roughly 250 miles, but the beam climbs as it travels, so distant echoes are " +
			"read high inside a storm rather than near the ground.");

		private static string DistanceNow(double? miles)
		{
			if (miles is not double mi) return "Drop a location marker to measure from where you are.";
			if (mi <= 60) return $"{mi:0} miles out — close enough that the beam is still low over your area.";
			if (mi <= 140) return $"{mi:0} miles out — the beam is a few thousand feet up by the time it reaches you.";
			return $"{mi:0} miles out — near the edge of useful range; a nearer site would see your area lower.";
		}

		// ── Antenna position ─────────────────────────────────────────────────────────────────────
		public static RadarGlossaryCard Coordinates() => new(
			"Antenna position",
			"Latitude and longitude of the tower",
			"Where the dish physically stands. Every echo the radar reports is measured outward from this point.",
			string.Empty,
			"Positive latitude is north of the equator; negative longitude is west of Greenwich.");

		// ── Site status ──────────────────────────────────────────────────────────────────────────
		public static RadarGlossaryCard Status(SiteAvailability availability, bool replayDay) => new(
			"Site status",
			"Availability from the last check",
			"Whether this site's volumes are reaching the public archive the app reads. A radar can be " +
			"running perfectly and still show offline if its data isn't being published.",
			StatusNow(availability, replayDay),
			$"A site counts as online while its newest volume is under {RadarSiteStatus.Staleness.TotalMinutes:0} minutes old.");

		private static string StatusNow(SiteAvailability availability, bool replayDay) => availability switch
		{
			SiteAvailability.Online => replayDay
				? "This site has data for the replay day you're viewing."
				: "Fresh data is arriving from this site now.",
			SiteAvailability.Offline => replayDay
				? "Nothing was archived from this site on the replay day you're viewing."
				: "Nothing recent has arrived from this site — it may be down for maintenance.",
			_ => "Not checked yet. The app checks every site shortly after it starts.",
		};

		// ── Network ──────────────────────────────────────────────────────────────────────────────
		public static RadarGlossaryCard Network(RadarSiteClass siteClass) => siteClass switch
		{
			RadarSiteClass.Tdwr => new RadarGlossaryCard(
				"Network",
				"Terminal Doppler Weather Radar",
				"An FAA radar guarding one airport. It sits closer to the ground and updates faster than a " +
				"NEXRAD, but only covers its own terminal area.",
				string.Empty,
				"Turn these on or off under Settings, Radar."),
			RadarSiteClass.Research => new RadarGlossaryCard(
				"Network",
				"Research and test radar",
				"A test bed rather than an operational site. It can be switched off, re-aimed or run in " +
				"unusual modes without notice.",
				string.Empty,
				"Turn these on or off under Settings, Radar."),
			_ => new RadarGlossaryCard(
				"Network",
				"WSR-88D, the NEXRAD network",
				"One of the 160-odd National Weather Service radars covering the country. This is the " +
				"network most weather apps mean by \"radar\".",
				string.Empty,
				"Operated by the NWS, the Air Force and the FAA together."),
		};

		// ── Site load time (the Atlas's "Your use" strip) ────────────────────────────────────────
		// ⚠️ The Context sentence states SiteUsageTracker's clock rule — move them together.
		public static RadarGlossaryCard SiteLoadTime(string siteId, double nowCastSeconds, double pastCastSeconds, int loads) => new(
			"Site load time",
			"Total time this site has been your loaded radar",
			$"How long {siteId} has been the radar on your map, added up across every session and split by " +
				"mode: NowCast for live loops, PastCast for replays.",
			SiteLoadTimeNow(nowCastSeconds, pastCastSeconds, loads),
			"The clock pauses while Anvil is minimized and stops when you switch sites or clear the radar.");

		private static string SiteLoadTimeNow(double nowCast, double pastCast, int loads)
		{
			var seconds = nowCast + pastCast;
			if (seconds < 60) return loads == 0 ? string.Empty : "Under a minute so far.";

			// Which modes it came from — "all in NowCast" beats "0 minutes in PastCast".
			var split = pastCast < 60 ? "all in NowCast"
				: nowCast < 60 ? "all in PastCast"
				: $"{LoadTimeWords(nowCast)} in NowCast, {LoadTimeWords(pastCast)} in PastCast";
			var perLoad = loads > 1
				? $", about {SpokenAge(TimeSpan.FromSeconds(seconds / loads)).ToLowerInvariant()} per load"
				: string.Empty;
			return $"{LoadTimeWords(seconds)} so far — {split}{perLoad}.";
		}

		// Same precision as the tile (6.4 hours), so the sentence and the number agree.
		private static string LoadTimeWords(double seconds) => seconds < 3600
			? SpokenAge(TimeSpan.FromSeconds(seconds)).ToLowerInvariant()
			: $"{seconds / 3600:0.#} hour{(Math.Round(seconds / 3600, 1) == 1 ? string.Empty : "s")}";

		// Age in words for a sentence ("4 minutes", "2 hours") — the tile shows the compact form instead.
		private static string SpokenAge(TimeSpan span)
		{
			if (span.TotalMinutes < 1) return "Less than a minute";
			if (span.TotalMinutes < 60) return $"{span.TotalMinutes:0} minute{Plural(span.TotalMinutes)}";
			if (span.TotalHours < 24) return $"{span.TotalHours:0} hour{Plural(span.TotalHours)}";
			return $"{span.TotalDays:0} day{Plural(span.TotalDays)}";
		}

		private static string Plural(double value) => Math.Round(value) == 1 ? string.Empty : "s";

		// "VCP 212 · precip" → 212. Null when the line has no number (archive placeholder, "—", "loading…").
		private static int? VcpNumber(string? modeText)
		{
			if (string.IsNullOrEmpty(modeText)) return null;
			var idx = modeText.IndexOf("VCP ", StringComparison.Ordinal);
			if (idx < 0) return null;
			var rest = modeText[(idx + 4)..];
			var end = 0;
			while (end < rest.Length && char.IsDigit(rest[end])) end++;
			return end > 0 && int.TryParse(rest[..end], out var vcp) ? vcp : null;
		}
	}
}
