using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Anvil.Services
{
	// ── NCEI Storm Events: the official tornado record, 1950 → ~3 months ago ──────────────────────────
	// ⚠️ WHY A SECOND SOURCE. The DAT has no tracks before an office adopted it (OUN: April 2012), and its
	// retro-entered 2011-era features carry local STANDARD time stored as if it were UTC — May 24 2011's
	// El Reno–Piedmont polygon is stamped 14:50Z; Storm Events has that tornado 14:50–15:55 CST, i.e.
	// 20:50Z. Storm Events gives every tornado a start/end point and a start/end time with its zone, one
	// row per COUNTY crossed, so the rows chain into one path per tornado (straight within each county).
	// It does three jobs in BuildWindow: supplies a track where the DAT has none, corrects the TIMES of a
	// DAT track it matches, and — through those tracks — times the DAT polygons and points.
	// ⚠️ It lags ~3 months (NCEI publishes per year and republishes old years), so the last few months are
	// DAT-only; that is fine, because that is exactly when DAT times are right.
	public sealed partial class DamageSurveyService
	{
		private const string StormEventsBase = "https://www.ncei.noaa.gov/pub/data/swdi/stormevents/csvfiles/";
		private const string StormEventsCacheVersion = "v1";

		// How often a cached year is re-checked against NCEI's listing for a republished file.
		private static readonly TimeSpan StormEventsRecheck = TimeSpan.FromDays(7);

		// Each year is its own file; a window reads the years its ±1-day margin touches.
		private async Task<List<SeSegment>> LoadStormEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
		{
			var all = new List<SeSegment>();
			string? listing = null;
			for (var year = fromUtc.UtcDateTime.Year; year <= toUtc.UtcDateTime.Year; year++)
			{
				try
				{
					var (segments, fetchedListing) = await EnsureStormEventsYearAsync(year, listing, ct);
					listing = fetchedListing;
					all.AddRange(segments);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					// ⚠️ NEVER FATAL: the DAT still draws on its own; only older/untimed features suffer.
					_logger.LogWarning(ex, "Storm Events {Year} unavailable", year);
				}
			}
			return all;
		}

		// A year's tornado rows, from the cache if it was checked recently, else re-checked against the
		// listing and re-downloaded only when NCEI has republished it. Returns the listing it fetched (if any)
		// so a two-year window lists once.
		private async Task<(List<SeSegment> Segments, string? Listing)> EnsureStormEventsYearAsync(int year, string? listing, CancellationToken ct)
		{
			var prefix = $"se-{StormEventsCacheVersion}-{year}-c";
			var cached = Directory.EnumerateFiles(CacheDirectory, prefix + "*.json").OrderByDescending(f => f).FirstOrDefault();
			if (cached is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(cached) < StormEventsRecheck)
			{
				return (ReadSegments(cached), listing);
			}

			try
			{
				listing ??= await Http.GetStringAsync(StormEventsBase, ct);
			}
			catch (Exception ex) when (ex is not OperationCanceledException && cached is not null)
			{
				_logger.LogWarning(ex, "Storm Events listing failed; using cached {Year}", year);
				return (ReadSegments(cached), listing);
			}

			var match = Regex.Matches(listing, $@"StormEvents_details-ftp_v1\.0_d{year}_c(\d{{8}})\.csv\.gz")
				.Select(m => m.Groups[1].Value).OrderByDescending(s => s, StringComparer.Ordinal).FirstOrDefault();
			if (match is null)
			{
				// Not published yet (early in a year) — nothing, not an error.
				return (cached is null ? new List<SeSegment>() : ReadSegments(cached), listing);
			}

			var path = Path.Combine(CacheDirectory, $"{prefix}{match}.json");
			if (File.Exists(path))
			{
				File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // re-checked: good for another week
				return (ReadSegments(path), listing);
			}

			var segments = await DownloadStormEventsYearAsync($"{StormEventsBase}StormEvents_details-ftp_v1.0_d{year}_c{match}.csv.gz", ct);
			await AtomicWriteAsync(path, JsonSerializer.Serialize(segments), ct);
			foreach (var old in Directory.EnumerateFiles(CacheDirectory, prefix + "*.json"))
			{
				if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) { try { File.Delete(old); } catch { } }
			}
			_logger.LogInformation("Storm Events {Year} cached: {Count} tornado segments", year, segments.Count);
			return (segments, listing);
		}

		// Streams the year's gzipped CSV (10–16 MB gz for modern years) and keeps only the tornado rows.
		// ⚠️ ResponseHeadersRead + streaming: the file is never buffered whole, and the client's 30 s timeout
		// covers only the headers, not the download.
		private async Task<List<SeSegment>> DownloadStormEventsYearAsync(string url, CancellationToken ct)
		{
			using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
			response.EnsureSuccessStatusCode();
			await using var body = await response.Content.ReadAsStreamAsync(ct);
			await using var gz = new GZipStream(body, CompressionMode.Decompress);
			using var reader = new StreamReader(gz, Encoding.UTF8);
			return await Task.Run(() => StormEvents.ParseTornadoRows(reader, ct), ct);
		}

		private List<SeSegment> ReadSegments(string path)
		{
			try
			{
				return JsonSerializer.Deserialize<List<SeSegment>>(File.ReadAllText(path)) ?? new List<SeSegment>();
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Storm Events cache unreadable: {File}", path);
				return new List<SeSegment>();
			}
		}
	}

	/// <summary>One Storm Events tornado row: the part of one tornado inside one county. Times Unix ms UTC.</summary>
	internal sealed record SeSegment(
		long EventId,
		long EpisodeId,
		long T0,
		long T1,
		double Lat0,
		double Lon0,
		double Lat1,
		double Lon1,
		bool HasEnd,
		string Scale,
		double? LengthMi,
		double? WidthYd,
		int Injuries,
		int Fatalities,
		string Wfo,
		string County,
		string State,
		string Narrative);

	/// <summary>Pure Storm Events parsing + chaining — no I/O, so the tests can drive it.</summary>
	internal static class StormEvents
	{
		// Consecutive county rows of one tornado: the next begins where (and when) the last ended.
		// ⚠️ 0.1° (~10 km), measured: 1999's Bridge Creek–Moore F5 hands McClain→Cleveland from -97.55 to
		// -97.60 (older rows carry 2-decimal, hand-entered county-line points), and 0.03° split that tornado
		// into three. The TIME handoff is what really identifies the next row (same episode, same minute),
		// and the closest candidate wins.
		private const double ChainSlackDeg = 0.1;
		private const long ChainSlackMs = 10 * 60_000;

		private const int MaxNarrative = 3000;

		/// <summary>Reads a details CSV and returns its tornado rows that have a location and a time.</summary>
		internal static List<SeSegment> ParseTornadoRows(TextReader reader, CancellationToken ct = default)
		{
			var list = new List<SeSegment>();
			var header = ReadRecord(reader);
			if (header is null) { return list; }
			var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			for (var i = 0; i < header.Count; i++) { col[header[i]] = i; }
			string F(List<string> r, string name) => col.TryGetValue(name, out var i) && i < r.Count ? r[i].Trim() : string.Empty;

			List<string>? rec;
			var n = 0;
			while ((rec = ReadRecord(reader)) is not null)
			{
				if ((++n & 0x3FF) == 0) { ct.ThrowIfCancellationRequested(); }
				if (!string.Equals(F(rec, "EVENT_TYPE"), "Tornado", StringComparison.OrdinalIgnoreCase)) { continue; }

				var offset = UtcOffsetHours(F(rec, "CZ_TIMEZONE"));
				var t0 = offset is { } o0 ? ToUtcMs(F(rec, "BEGIN_YEARMONTH"), F(rec, "BEGIN_DAY"), F(rec, "BEGIN_TIME"), o0) : null;
				var t1 = offset is { } o1 ? ToUtcMs(F(rec, "END_YEARMONTH"), F(rec, "END_DAY"), F(rec, "END_TIME"), o1) : null;
				if (t0 is null) { continue; }
				if (!TryD(F(rec, "BEGIN_LAT"), out var lat0) || !TryD(F(rec, "BEGIN_LON"), out var lon0)) { continue; }
				var hasEnd = TryD(F(rec, "END_LAT"), out var lat1) & TryD(F(rec, "END_LON"), out var lon1);
				if (!hasEnd) { lat1 = lat0; lon1 = lon0; }

				var narrative = F(rec, "EVENT_NARRATIVE");
				list.Add(new SeSegment(
					EventId: long.TryParse(F(rec, "EVENT_ID"), out var eid) ? eid : 0,
					EpisodeId: long.TryParse(F(rec, "EPISODE_ID"), out var ep) ? ep : 0,
					T0: t0.Value,
					T1: Math.Max(t0.Value, t1 ?? t0.Value),
					Lat0: lat0, Lon0: lon0, Lat1: lat1, Lon1: lon1,
					HasEnd: hasEnd && (lat1 != lat0 || lon1 != lon0),
					Scale: F(rec, "TOR_F_SCALE").ToUpperInvariant(),
					LengthMi: TryD(F(rec, "TOR_LENGTH"), out var len) && len > 0 ? len : null,
					WidthYd: TryD(F(rec, "TOR_WIDTH"), out var wid) && wid > 0 ? wid : null,
					Injuries: IntOr0(F(rec, "INJURIES_DIRECT")) + IntOr0(F(rec, "INJURIES_INDIRECT")),
					Fatalities: IntOr0(F(rec, "DEATHS_DIRECT")) + IntOr0(F(rec, "DEATHS_INDIRECT")),
					Wfo: F(rec, "WFO"),
					County: F(rec, "CZ_NAME"),
					State: F(rec, "STATE"),
					Narrative: narrative.Length > MaxNarrative ? narrative[..MaxNarrative] + "…" : narrative));
			}
			return list;
		}

		/// <summary>
		/// The zone's UTC offset in hours. Modern files write "CST-6"; older ones the bare abbreviation.
		/// ⚠️ NCEI records local STANDARD time year-round, so "CST" is always -6 — never apply DST.
		/// </summary>
		internal static int? UtcOffsetHours(string tz)
		{
			var s = (tz ?? string.Empty).Trim().ToUpperInvariant();
			var m = Regex.Match(s, @"^[A-Z]+([+-]?\d{1,2})$");
			if (m.Success) { return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); }
			return s switch
			{
				"EST" => -5, "EDT" => -4, "CST" => -6, "CDT" => -5, "MST" => -7, "MDT" => -6,
				"PST" => -8, "PDT" => -7, "AKST" => -9, "AKDT" => -8, "HST" => -10, "AST" => -4,
				"SST" => -11, "GST" or "CHST" => 10,
				_ => null,
			};
		}

		// yyyymm + day + HHmm (3-4 digits, "5" = 00:05) in a fixed offset → Unix ms UTC.
		internal static long? ToUtcMs(string yearMonth, string day, string hhmm, int offsetHours)
		{
			if (yearMonth.Length != 6 ||
				!int.TryParse(yearMonth.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ||
				!int.TryParse(yearMonth.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var mo) ||
				!int.TryParse(day, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ||
				!int.TryParse(hhmm, NumberStyles.None, CultureInfo.InvariantCulture, out var t))
			{
				return null;
			}
			var hh = t / 100;
			var mm = t % 100;
			if (mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || hh > 23 || mm > 59) { return null; }
			var local = new DateTimeOffset(y, mo, d, hh, mm, 0, TimeSpan.FromHours(offsetHours));
			return local.ToUnixTimeMilliseconds();
		}

		/// <summary>
		/// Joins county rows into one path per tornado (same episode; the next row starts where and when the
		/// last ended) and returns each as a track feature. A row with no end point becomes a Point track.
		/// </summary>
		internal static List<DatFeature> BuildTracks(IReadOnlyList<SeSegment> segments)
		{
			// ⚠️ BEST PAIR FIRST, across the whole episode — not first-come per row. Greedy-in-file-order let an
			// unrelated EF0 ending 7 min earlier claim the Cleveland Co. continuation of May 24 2011's
			// Chickasha EF4, whose own McClain row ends on the exact same point and minute.
			var next = new Dictionary<SeSegment, SeSegment>(ReferenceEqualityComparer.Instance);
			var hasPrev = new HashSet<SeSegment>(ReferenceEqualityComparer.Instance);
			foreach (var episode in segments.GroupBy(s => s.EpisodeId))
			{
				var rows = episode.ToList();
				var pairs = new List<(SeSegment A, SeSegment B, double Score)>();
				foreach (var a in rows)
				{
					if (!a.HasEnd) { continue; }
					foreach (var b in rows)
					{
						if (ReferenceEquals(a, b)) { continue; }
						// ⚠️ A continuation is ALWAYS in another county — NCEI splits a tornado only at county
						// lines. Same county = a new tornado from the same storm (cyclic supercells start a
						// few km and minutes from where the last one roped out), which the 0.1° slack would
						// otherwise weld on.
						if (string.Equals(a.County, b.County, StringComparison.OrdinalIgnoreCase) &&
							string.Equals(a.State, b.State, StringComparison.OrdinalIgnoreCase)) { continue; }
						var dt = Math.Abs(b.T0 - a.T1);
						var dd = Math.Max(Math.Abs(b.Lat0 - a.Lat1), Math.Abs(b.Lon0 - a.Lon1));
						if (dt > ChainSlackMs || dd > ChainSlackDeg) { continue; }
						pairs.Add((a, b, dd * 100 + dt / 60_000.0));
					}
				}
				foreach (var (a, b, _) in pairs.OrderBy(p => p.Score))
				{
					if (next.ContainsKey(a) || hasPrev.Contains(b)) { continue; }
					next[a] = b;
					hasPrev.Add(b);
				}
			}

			var tracks = new List<DatFeature>();
			foreach (var head in segments.Where(s => !hasPrev.Contains(s)))
			{
				var chain = new List<SeSegment> { head };
				var seen = new HashSet<SeSegment>(ReferenceEqualityComparer.Instance) { head };
				while (next.TryGetValue(chain[^1], out var n) && seen.Add(n)) { chain.Add(n); }
				tracks.Add(ToTrack(chain));
			}
			return tracks;
		}

		private static DatFeature ToTrack(List<SeSegment> chain)
		{
			var first = chain[0];
			// Each row's own begin AND end: where two rows disagree about the county-line point, the path shows
			// the short jog rather than bending a county's segment to meet the other's.
			var points = new List<(double Lon, double Lat)>();
			void Add(double lon, double lat)
			{
				if (points.Count == 0 || Math.Abs(points[^1].Lon - lon) > 1e-4 || Math.Abs(points[^1].Lat - lat) > 1e-4)
				{
					points.Add((lon, lat));
				}
			}
			foreach (var s in chain)
			{
				Add(s.Lon0, s.Lat0);
				if (s.HasEnd) { Add(s.Lon1, s.Lat1); }
			}

			var box = GeoBox.Empty;
			foreach (var (lon, lat) in points) { box = box.Include(lon, lat); }

			var inv = CultureInfo.InvariantCulture;
			string geometry = points.Count >= 2
				? "{\"type\":\"LineString\",\"coordinates\":[" +
					string.Join(",", points.Select(p => string.Create(inv, $"[{Math.Round(p.Lon, 5)},{Math.Round(p.Lat, 5)}]"))) + "]}"
				: string.Create(inv, $"{{\"type\":\"Point\",\"coordinates\":[{Math.Round(first.Lon0, 5)},{Math.Round(first.Lat0, 5)}]}}");

			// The strongest county rating is the tornado's rating.
			var top = chain.OrderByDescending(s => DamageSurveyService.EfRank(ColourClass(s.Scale))).First();
			var counties = chain.Select(s => TitleCase(s.County)).Distinct().ToList();
			var states = chain.Select(s => TitleCase(s.State)).Distinct();
			var name = string.Join(" → ", counties) + (counties.Count == 1 ? " Co., " : " Cos., ") + string.Join("/", states);
			var comments = string.Join("\n\n", chain.Where(s => s.Narrative.Length > 0)
				.Select(s => chain.Count > 1 ? $"{TitleCase(s.County)}: {s.Narrative}" : s.Narrative));

			double? length = chain.Any(s => s.LengthMi is not null) ? chain.Sum(s => s.LengthMi ?? 0) : null;
			return new DatFeature(
				Layer: DatLayer.Tracks,
				Geometry: geometry,
				Box: box,
				T0: chain.Min(s => s.T0),
				T1: chain.Max(s => s.T1),
				Ef: ColourClass(top.Scale),
				Name: name,
				Wfo: string.Join(",", chain.Select(s => s.Wfo).Where(w => w.Length > 0).Distinct()),
				Wind: null,
				LengthMi: length,
				WidthYd: chain.Max(s => s.WidthYd),
				Injuries: chain.Sum(s => s.Injuries),
				Fatalities: chain.Sum(s => s.Fatalities),
				Comments: comments,
				DamageText: string.Empty,
				DegreeText: string.Empty)
			{
				Source = "se",
				Line = points,
				// Pre-2007 ratings are the old F scale — shown as such, coloured by their EF bucket.
				Label = top.Scale.StartsWith("F", StringComparison.Ordinal) ? top.Scale : null,
			};
		}

		// "F3" / "EF3" / "EFU" / "" → the class the palette colours by.
		internal static string ColourClass(string scale)
		{
			var s = scale.Trim().ToUpperInvariant();
			if (s.Length == 2 && s[0] == 'F' && char.IsDigit(s[1])) { s = "E" + s; }
			return DamageSurveyService.NormalizeEf(s.Length == 0 ? "EFU" : s) ?? "EFU";
		}

		private static string TitleCase(string s) =>
			CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

		private static bool TryD(string s, out double v) =>
			double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

		private static int IntOr0(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 0;

		/// <summary>One RFC-4180 record: quoted fields may hold commas, "" and NEWLINES (narratives do).
		/// Null at end of input.</summary>
		internal static List<string>? ReadRecord(TextReader reader)
		{
			var c = reader.Read();
			if (c < 0) { return null; }
			var fields = new List<string>(52);
			var sb = new StringBuilder();
			var inQuotes = false;
			while (c >= 0)
			{
				var ch = (char)c;
				if (inQuotes)
				{
					if (ch == '"')
					{
						if (reader.Peek() == '"') { sb.Append('"'); reader.Read(); }
						else { inQuotes = false; }
					}
					else { sb.Append(ch); }
				}
				else if (ch == '"') { inQuotes = true; }
				else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
				else if (ch == '\n') { break; }
				else if (ch != '\r') { sb.Append(ch); }
				c = reader.Read();
			}
			fields.Add(sb.ToString());
			return fields;
		}
	}
}
