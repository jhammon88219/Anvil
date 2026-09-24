using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="IRadarNwsStatusService"/>. No disk cache: the whole thing is two small requests, run
	/// once at launch and on demand (the Atlas's Re-check, 5-min cooldown), and a stale outage notice is worse
	/// than none.
	/// </summary>
	/// <remarks>
	/// SOURCES (both public, keyless):
	/// • <c>api.weather.gov/radar/stations</c> — every WSR-88D, TDWR and profiler with its RDA block
	///   (status, operability, alarm summary, generator). Needs a real User-Agent (403s a blank one).
	/// • <c>radar3pub.ncep.noaa.gov/ftm.txt</c> — the NCEP Level III status page's rolling 24-h file of FTMs,
	///   blocks separated by a line of '='. The same messages exist as <c>api.weather.gov/products/types/FTM</c>,
	///   but that is one request PER message; this is one request for all of them.
	/// ⚠️ FTM IDS ARE 3 LETTERS — <c>FTMCVG</c> covers KCVG and TCVG alike. <see cref="ResolveSiteId"/> decides.
	/// ⚠️ A message's time is its WMO header (ddhhmm UTC), never the "Message Date:" line: that line is free
	/// text in a dozen formats, and a RE-SENT notice keeps its original date there (KPBZ re-sent an Aug 12 notice
	/// on Sep 24 — the header said Sep 24, and Sep 24 is when it was current).
	/// </remarks>
	public sealed class RadarNwsStatusService : IRadarNwsStatusService
	{
		private const string StationsUrl = "https://api.weather.gov/radar/stations";
		private const string MessagesUrl = "https://radar3pub.ncep.noaa.gov/ftm.txt";

		private readonly HttpClient _http;

		public RadarNwsStatusService()
		{
			_http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			_http.DefaultRequestHeaders.UserAgent.ParseAdd("Anvil/1.0 (severe-weather app)");
		}

		public async Task<IReadOnlyDictionary<string, RadarNwsStation>> GetStationsAsync(CancellationToken ct = default)
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, StationsUrl);
			request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/geo+json"));
			using var response = await _http.SendAsync(request, ct);
			response.EnsureSuccessStatusCode();
			var json = await response.Content.ReadAsStringAsync(ct);
			var stations = ParseStations(json);
			// An empty list is a broken answer, not "no radars": throw so the caller keeps its last good set.
			if (stations.Count == 0)
			{
				throw new InvalidOperationException("NWS radar stations feed returned no stations.");
			}
			return stations;
		}

		public async Task<IReadOnlyList<RadarNwsMessage>> GetMessagesAsync(CancellationToken ct = default)
		{
			var text = await _http.GetStringAsync(MessagesUrl, ct);
			// Unlike stations, an empty FTM file IS a real answer (a quiet day) — but an HTML error page isn't.
			if (text.TrimStart().StartsWith('<'))
			{
				throw new InvalidOperationException("FTM feed returned HTML, not the message file.");
			}
			return ParseMessages(text, DateTimeOffset.UtcNow);
		}

		// ── Parsers (pure; internal for tests) ──────────────────────────────────────────────────────

		/// <summary>Reads the stations GeoJSON. A station with no <c>rda</c> block still gets a row (all nulls),
		/// so "NWS knows this site but it reports nothing" differs from "NWS doesn't list this site".</summary>
		internal static Dictionary<string, RadarNwsStation> ParseStations(string json)
		{
			var result = new Dictionary<string, RadarNwsStation>(StringComparer.OrdinalIgnoreCase);
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
			{
				return result;
			}

			foreach (var feature in features.EnumerateArray())
			{
				if (!feature.TryGetProperty("properties", out var p) || Str(p, "id") is not { Length: > 0 } id)
				{
					continue;
				}

				JsonElement rda = default;
				var hasRda = p.TryGetProperty("rda", out var rdaBlock) && rdaBlock.ValueKind == JsonValueKind.Object
					&& rdaBlock.TryGetProperty("properties", out rda) && rda.ValueKind == JsonValueKind.Object;

				DateTimeOffset? received = null;
				if (p.TryGetProperty("latency", out var latency) && latency.ValueKind == JsonValueKind.Object
					&& Str(latency, "levelTwoLastReceivedTime") is { } stamp
					&& DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
				{
					received = t.ToUniversalTime();
				}

				result[id] = new RadarNwsStation(
					id,
					hasRda ? Str(rda, "status") : null,
					hasRda ? Str(rda, "operabilityStatus") : null,
					hasRda ? Str(rda, "alarmSummary") : null,
					hasRda ? Str(rda, "generatorState") : null,
					received);
			}
			return result;
		}

		private static string? Str(JsonElement obj, string name) =>
			obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

		// "NOUS62 KRAH 231129" with an optional correction suffix ("RRA", "CCA").
		private static readonly Regex WmoHeader = new(@"^[A-Z]{4}\d{2}\s+([A-Z]{4})\s+(\d{2})(\d{2})(\d{2})(\s+[A-Z]{3})?\s*$",
			RegexOptions.Compiled);
		private static readonly Regex FtmLine = new(@"^FTM([A-Z0-9]{3})\s*$", RegexOptions.Compiled);

		/// <summary>Splits the FTM file into messages, newest first. A block without a readable WMO header and
		/// <c>FTMxxx</c> line is skipped rather than guessed at.</summary>
		internal static List<RadarNwsMessage> ParseMessages(string text, DateTimeOffset nowUtc)
		{
			var messages = new List<RadarNwsMessage>();
			var blocks = Regex.Split(text.Replace("\r\n", "\n"), @"^=+\s*$", RegexOptions.Multiline);
			foreach (var block in blocks)
			{
				var lines = block.Split('\n').Select(l => l.TrimEnd()).ToList();
				var h = lines.FindIndex(l => WmoHeader.IsMatch(l));
				if (h < 0 || h + 1 >= lines.Count || FtmLine.Match(lines[h + 1]) is not { Success: true } ftm)
				{
					continue;
				}
				var header = WmoHeader.Match(lines[h]);
				if (IssuedFrom(header, nowUtc) is not { } issued)
				{
					continue;
				}

				var body = BodyText(lines.Skip(h + 2));
				messages.Add(new RadarNwsMessage(ftm.Groups[1].Value, header.Groups[1].Value, issued, body));
			}
			return messages.OrderByDescending(m => m.IssuedUtc).ToList();
		}

		// ddhhmm carries no month or year: take the latest date on or before (now + a day of clock slack) whose
		// day-of-month matches. The file only spans 24 h, so this is exact except across a month boundary, which
		// it handles by stepping back a month.
		private static DateTimeOffset? IssuedFrom(Match header, DateTimeOffset nowUtc)
		{
			var day = int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture);
			var hour = int.Parse(header.Groups[3].Value, CultureInfo.InvariantCulture);
			var minute = int.Parse(header.Groups[4].Value, CultureInfo.InvariantCulture);
			if (day is < 1 or > 31 || hour > 23 || minute > 59)
			{
				return null;
			}

			var month = new DateTime(nowUtc.Year, nowUtc.Month, 1);
			for (var back = 0; back < 3; back++, month = month.AddMonths(-1))
			{
				if (day > DateTime.DaysInMonth(month.Year, month.Month))
				{
					continue;
				}
				var candidate = new DateTimeOffset(month.Year, month.Month, day, hour, minute, 0, TimeSpan.Zero);
				if (candidate <= nowUtc.AddDays(1))
				{
					return candidate;
				}
			}
			return null;
		}

		// Drops the free-form "Message Date:" line, then re-flows NWS's hard 70-column wrap into paragraphs (a
		// blank line is a paragraph break) so the text wraps to the card, not to a teletype.
		private static string BodyText(IEnumerable<string> lines)
		{
			var paragraphs = new List<string>();
			var current = new StringBuilder();
			foreach (var raw in lines)
			{
				var line = raw.Trim();
				if (line.StartsWith("MESSAGE DATE", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				if (line.Length == 0)
				{
					if (current.Length > 0)
					{
						paragraphs.Add(current.ToString());
						current.Clear();
					}
					continue;
				}
				if (current.Length > 0)
				{
					current.Append(' ');
				}
				current.Append(line);
			}
			if (current.Length > 0)
			{
				paragraphs.Add(current.ToString());
			}
			return string.Join("\n", paragraphs);
		}

		/// <summary>
		/// Which of OUR sites an FTM is about. Its code is 3 letters, so KCVG and TCVG both match "CVG":
		/// 1. one site ends in the code → that one;
		/// 2. the body names exactly one of the candidates ("THE CINCINNATI TDWR (TCVG)") → that one;
		/// 3. the body says TDWR / "terminal doppler" → the TDWR candidate, else the NEXRAD one.
		/// Decided by the site's NETWORK, never its first letter — TJUA (San Juan) is a WSR-88D.
		/// Null when no site of ours has the code (a profiler, or a radar we don't list).
		/// </summary>
		internal static string? ResolveSiteId(RadarNwsMessage message, IEnumerable<(string Id, bool IsTdwr)> sites)
		{
			var candidates = sites
				.Where(s => s.Id.Length == 4 && s.Id.EndsWith(message.Code, StringComparison.OrdinalIgnoreCase))
				.ToList();
			if (candidates.Count <= 1)
			{
				return candidates.Count == 1 ? candidates[0].Id : null;
			}

			var named = candidates
				.Where(s => Regex.IsMatch(message.Text, $@"\b{Regex.Escape(s.Id)}\b", RegexOptions.IgnoreCase))
				.ToList();
			if (named.Count == 1)
			{
				return named[0].Id;
			}

			var saysTdwr = message.Text.Contains("TDWR", StringComparison.OrdinalIgnoreCase)
				|| message.Text.Contains("TERMINAL DOPPLER", StringComparison.OrdinalIgnoreCase);
			var pick = candidates.FirstOrDefault(s => s.IsTdwr == saysTdwr);
			return (pick.Id ?? candidates[0].Id);
		}
	}
}
