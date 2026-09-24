using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="IDamageSurveyService"/>. Queries the NWS Damage Assessment Toolkit's public
	/// ArcGIS FeatureServer (the service behind the DAT "Damage Viewer"), caches each UTC day's raw layers,
	/// and writes one normalized GeoJSON per replay window. No WebView2 here — MainWindow maps the cache
	/// folder to the "damagesurveys" virtual host so the page can fetch https://damagesurveys/dat-….geojson.
	/// </summary>
	/// <remarks>
	/// ⚠️ THE THREE DAT LAYERS ARE NOT LINKED. Polygons carry a <c>path_guid</c> field, but it is null in
	/// the published data (checked 2026-09-24), so a damage polygon cannot be joined to its track by id.
	/// <see cref="BuildWindow"/> links them by TIME + PLACE instead (the polygon's time inside the track's
	/// start→end, bounding boxes overlapping) — and a polygon's own <c>stormdate</c> is the time of THAT
	/// contour, not the tornado's start, which is why the link is needed at all: filtering polygons by their
	/// own time would drop a tornado's outer EF0 contour from a window that caught only its later half.
	/// ⚠️ ONLY SOME OFFICES DRAW POLYGONS. On 2024-05-06 there were 58 surveyed tracks and 21 polygons, all
	/// from a handful of WFOs. The polygon layer is the default view by choice, so the VM's card says so
	/// when a window has tracks but no polygons.
	/// ⚠️ <c>qc</c> is "Y" on every published feature (the public service serves QC'd surveys only), so
	/// there is no preliminary/final distinction to show.
	/// </remarks>
	public sealed class DamageSurveyService : CachingHttpService, IDamageSurveyService
	{
		/// <summary>WebView virtual host the cached files are served under. MainWindow owns the actual
		/// mapping; this constant is the shared contract.</summary>
		public const string CacheHostName = "damagesurveys";

		private const string ServiceBase =
			"https://services.dat.noaa.gov/arcgis/rest/services/nws_damageassessmenttoolkit/DamageViewer/FeatureServer";

		// The service's maxRecordCount. 2011-04-27 alone has ~1,400 damage points, so paging is real.
		private const int PageSize = 2000;
		private const int MaxPages = 50; // runaway guard — 100k features in one day is not a real answer

		// Cache schema version — bump when the window file's shape changes, so stale files are ignored.
		private const string CacheVersion = "v1";

		/// <summary>
		/// How far BEFORE the window the day fetch reaches. A track is filed under its START time, so one that
		/// began just before midnight UTC and is still on the ground inside the window lives in the previous
		/// day's file. Three hours is longer than any surveyed track (Tri-State ran ~3.5 h but predates the
		/// DAT's coverage; modern long-trackers run 1-2.5 h).
		/// </summary>
		internal static readonly TimeSpan TrackLookback = TimeSpan.FromHours(3);

		// Surveys land days after an event and are revised for weeks. A day older than this is treated as
		// settled and fetched once; a newer one is re-fetched once its cache is over an hour old.
		private static readonly TimeSpan RevisionHorizon = TimeSpan.FromDays(45);
		private static readonly TimeSpan RecentRefetchAge = TimeSpan.FromHours(1);

		private readonly ILogger<DamageSurveyService> _logger;

		public DamageSurveyService(ILogger<DamageSurveyService> logger) : base("DamageSurveys") => _logger = logger;

		/// <summary>Stable cache filename for one window's normalized features.</summary>
		public static string WindowCacheName(DateTimeOffset startUtc, DateTimeOffset endUtc) =>
			$"dat-{CacheVersion}-{startUtc.UtcDateTime:yyyyMMddHHmm}-{endUtc.UtcDateTime:yyyyMMddHHmm}.geojson";

		private static string DayCacheName(DateOnly day, DatLayer layer) =>
			$"dat-{CacheVersion}-day-{day:yyyyMMdd}-{LayerSlug(layer)}.geojson";

		public string LocalUrl(DateTimeOffset startUtc, DateTimeOffset endUtc) =>
			$"https://{CacheHostName}/{WindowCacheName(startUtc, endUtc)}";

		public async Task<DamageSurveyResult> EnsureWindowAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
		{
			var byLayer = new Dictionary<DatLayer, List<DatFeature>>
			{
				[DatLayer.Points] = new(),
				[DatLayer.Tracks] = new(),
				[DatLayer.Areas] = new(),
			};

			foreach (var day in DaysFor(startUtc, endUtc))
			{
				foreach (var layer in byLayer.Keys)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var file = await EnsureDayAsync(day, layer, cancellationToken);
					if (file is null)
					{
						// ⚠️ ALL OR NOTHING. A window missing one day's tracks would quietly draw half an
						// outbreak and call it the whole thing.
						return new DamageSurveyResult(false, 0, 0, 0, "NWS damage surveys unavailable.");
					}
					ParseDayFile(file, layer, byLayer[layer]);
				}
			}

			var built = BuildWindow(byLayer[DatLayer.Areas], byLayer[DatLayer.Tracks], byLayer[DatLayer.Points],
				startUtc.ToUnixTimeMilliseconds(), endUtc.ToUnixTimeMilliseconds());

			var windowFile = Path.Combine(CacheDirectory, WindowCacheName(startUtc, endUtc));
			await WriteWindowAsync(windowFile, built, cancellationToken);

			return new DamageSurveyResult(true,
				built.Count(f => f.Layer == DatLayer.Areas),
				built.Count(f => f.Layer == DatLayer.Tracks),
				built.Count(f => f.Layer == DatLayer.Points),
				null);
		}

		// The UTC days the window needs: from TrackLookback before its start to its (exclusive) end.
		internal static IEnumerable<DateOnly> DaysFor(DateTimeOffset startUtc, DateTimeOffset endUtc)
		{
			var first = DateOnly.FromDateTime((startUtc - TrackLookback).UtcDateTime);
			var last = DateOnly.FromDateTime(endUtc.UtcDateTime.AddTicks(-1));
			for (var d = first; d <= last; d = d.AddDays(1)) { yield return d; }
		}

		// ── Day cache ────────────────────────────────────────────────────────────────────────────────

		// Returns the path of a usable day file (fresh, settled, or last-known-good), or null if there is none.
		private async Task<string?> EnsureDayAsync(DateOnly day, DatLayer layer, CancellationToken ct)
		{
			var path = Path.Combine(CacheDirectory, DayCacheName(day, layer));
			var now = DateTime.UtcNow;
			if (File.Exists(path))
			{
				var dayEnd = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1);
				var settled = now - dayEnd > RevisionHorizon;
				if (settled || now - File.GetLastWriteTimeUtc(path) < RecentRefetchAge) { return path; }
			}

			try
			{
				var features = await FetchDayAsync(day, layer, ct);
				await AtomicWriteAsync(path, async stream =>
				{
					await using var writer = new Utf8JsonWriter(stream);
					writer.WriteStartObject();
					writer.WriteString("type", "FeatureCollection");
					writer.WriteStartArray("features");
					foreach (var raw in features) { writer.WriteRawValue(raw, skipInputValidation: true); }
					writer.WriteEndArray();
					writer.WriteEndObject();
					await writer.FlushAsync(ct);
				}, ct);
				return path;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "DAT {Layer} fetch failed for {Day}", LayerSlug(layer), day);
				return File.Exists(path) ? path : null; // last-known-good
			}
		}

		// One UTC day of one layer, paged. Returns each feature's raw GeoJSON text.
		// ⚠️ A WHERE on stormdate, not the layer's `time=` extent: the extent is inclusive at both ends, so
		// a feature stamped exactly 00:00Z would land in two day files. TIMESTAMP literals are UTC here
		// (verified: the where and time= counts agree for 2024-05-06).
		private async Task<List<string>> FetchDayAsync(DateOnly day, DatLayer layer, CancellationToken ct)
		{
			var where = string.Create(CultureInfo.InvariantCulture,
				$"stormdate >= TIMESTAMP '{day:yyyy-MM-dd} 00:00:00' AND stormdate < TIMESTAMP '{day.AddDays(1):yyyy-MM-dd} 00:00:00'");
			var list = new List<string>();
			for (var page = 0; page < MaxPages; page++)
			{
				var url = $"{ServiceBase}/{(int)layer}/query?where={Uri.EscapeDataString(where)}" +
					$"&outFields=*&orderByFields=objectid&resultOffset={list.Count}&resultRecordCount={PageSize}" +
					"&geometryPrecision=5&outSR=4326&f=geojson";

				using var response = await Http.GetAsync(url, ct);
				response.EnsureSuccessStatusCode();
				await using var stream = await response.Content.ReadAsStreamAsync(ct);
				using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
				var root = doc.RootElement;

				// ArcGIS reports a failed query as HTTP 200 with an {"error": …} body.
				if (root.TryGetProperty("error", out var err))
				{
					throw new InvalidOperationException("DAT query error: " + err.GetRawText());
				}
				if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
				{
					throw new InvalidOperationException("DAT query returned no feature array.");
				}

				var n = 0;
				foreach (var f in features.EnumerateArray()) { list.Add(f.GetRawText()); n++; }

				// More pages when the server says it truncated, or a page came back full.
				var truncated = (root.TryGetProperty("exceededTransferLimit", out var etl) && etl.ValueKind == JsonValueKind.True)
					|| (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
						&& props.TryGetProperty("exceededTransferLimit", out var etl2) && etl2.ValueKind == JsonValueKind.True);
				if (n == 0 || (!truncated && n < PageSize)) { break; }
			}
			return list;
		}

		// ── Parse ────────────────────────────────────────────────────────────────────────────────────

		private void ParseDayFile(string path, DatLayer layer, List<DatFeature> into)
		{
			try
			{
				using var doc = JsonDocument.Parse(File.ReadAllText(path));
				if (!doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array) { return; }
				foreach (var f in features.EnumerateArray())
				{
					if (TryParseFeature(f, layer) is { } parsed) { into.Add(parsed); }
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "DAT day file unreadable: {File}", path);
			}
		}

		// One raw DAT feature → the normalized record, or null when it is not a tornado feature or has no
		// usable geometry/time. Field names are the service's (lowercase); tracks carry start/end times,
		// polygons and points only stormdate.
		internal static DatFeature? TryParseFeature(JsonElement f, DatLayer layer)
		{
			if (!f.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object) { return null; }
			if (!f.TryGetProperty("geometry", out var geom) || geom.ValueKind != JsonValueKind.Object) { return null; }
			if (!TryBounds(geom, out var box)) { return null; }

			var ef = NormalizeEf(Str(p, "efscale"));
			if (ef is null) { return null; } // wind / tropical — not a tornado survey

			var stormdate = Long(p, "stormdate");
			long t0, t1;
			if (layer == DatLayer.Tracks)
			{
				t0 = Long(p, "starttime") ?? stormdate ?? 0;
				t1 = Math.Max(t0, Long(p, "endtime") ?? t0);
			}
			else
			{
				t0 = t1 = stormdate ?? 0;
			}
			if (t0 <= 0) { return null; }

			return new DatFeature(
				Layer: layer,
				Geometry: geom.GetRawText(),
				Box: box,
				T0: t0,
				T1: t1,
				Ef: ef,
				Name: Str(p, "event_id")?.Trim() ?? string.Empty,
				Wfo: (layer == DatLayer.Points ? Str(p, "office") : Str(p, "wfo"))?.Trim() ?? string.Empty,
				Wind: PositiveInt(layer == DatLayer.Points ? Loose(p, "windspeed") : Loose(p, "maxwind")),
				LengthMi: PositiveDouble(Loose(p, "length")),
				WidthYd: PositiveDouble(Loose(p, "width")),
				Injuries: PositiveInt(Loose(p, "injuries")) ?? 0,
				Fatalities: PositiveInt(Loose(p, layer == DatLayer.Points ? "deaths" : "fatalities")) ?? 0,
				Comments: Str(p, "comments")?.Trim() ?? string.Empty,
				DamageText: Str(p, "damage_txt")?.Trim() ?? string.Empty,
				DegreeText: Str(p, "dod_txt")?.Trim() ?? string.Empty);
		}

		/// <summary>
		/// The DAT's <c>efscale</c> → the rating the overlay colours by, or null for a non-tornado entry
		/// (dropped). The DAT's own renderer groups the same way: "EF3+" is its own class, lowercase "ef0" is
		/// EF0, and anything unrated or junk ("UNKNOWN", "N/A", "Correction", …) is "N/A".
		/// </summary>
		internal static string? NormalizeEf(string? raw)
		{
			var s = (raw ?? string.Empty).Trim().ToUpperInvariant();
			return s switch
			{
				"EF0" or "EF1" or "EF2" or "EF3" or "EF3+" or "EF4" or "EF5" or "EFU" => s,
				"TSTM/WIND" or "WIND" or "TSTM" or "TROPICAL" => null,
				_ => "N/A",
			};
		}

		// Sort rank: higher EF draws on top. EF3+ sits with EF3; EFU below EF0; N/A lowest.
		internal static int EfRank(string ef) => ef switch
		{
			"EF0" => 0, "EF1" => 1, "EF2" => 2, "EF3" or "EF3+" => 3, "EF4" => 4, "EF5" => 5,
			"EFU" => -1,
			_ => -2,
		};

		// ── Link + window ────────────────────────────────────────────────────────────────────────────

		// A polygon's time may sit a minute outside its track's start/end (both are hand-entered).
		private const long LinkSlackMs = 60_000;
		// Bounding-box slack for the link, in degrees (~1 km): a thin track and the swath around it can
		// miss each other's boxes by a few hundred metres at the ends.
		private const double LinkSlackDeg = 0.01;

		/// <summary>
		/// The span an UNLINKED polygon is assumed to cover, from its own time. ⚠️ Measured, not guessed:
		/// on 2011-04-27 only 4 of 186 polygons link (most of that day's polygons have no DAT track at all),
		/// and every contour of one tornado carries the SAME stormdate — so with no span, a window starting a
		/// few minutes into that tornado dropped all of its damage. 30 min errs toward showing a tornado that
		/// ended shortly before the window over hiding one still on the ground. (Linked: 24/28 on
		/// 2024-05-06, 16/29 on 2013-05-20.)
		/// </summary>
		internal const long UnlinkedAreaSpanMs = 30 * 60_000;

		/// <summary>
		/// Links each damage polygon to the track it lies on, then keeps every feature whose time span
		/// overlaps [startMs, endMs]. A linked polygon takes its track's span (and name, office, rating and
		/// peak wind for the popup); an unlinked one spans <see cref="UnlinkedAreaSpanMs"/> from its own time. Output is areas (lowest EF first,
		/// so higher contours draw on top), then tracks, then points.
		/// </summary>
		internal static List<DatFeature> BuildWindow(IReadOnlyList<DatFeature> areas, IReadOnlyList<DatFeature> tracks,
			IReadOnlyList<DatFeature> points, long startMs, long endMs)
		{
			bool Overlaps(DatFeature x) => x.T0 <= endMs && x.T1 >= startMs;

			var linkedAreas = new List<DatFeature>(areas.Count);
			foreach (var a in areas)
			{
				DatFeature? best = null;
				var bestDist = double.MaxValue;
				foreach (var t in tracks)
				{
					if (a.T0 < t.T0 - LinkSlackMs || a.T0 > t.T1 + LinkSlackMs) { continue; }
					if (!a.Box.Intersects(t.Box, LinkSlackDeg)) { continue; }
					var d = a.Box.CenterDistance(t.Box);
					if (d < bestDist) { bestDist = d; best = t; }
				}
				linkedAreas.Add(best is null ? a with { T1 = a.T0 + UnlinkedAreaSpanMs } : a with
				{
					T0 = best.T0,
					T1 = best.T1,
					Name = best.Name,
					Wfo = best.Wfo,
					TrackEf = best.Ef,
					Wind = best.Wind,
					LengthMi = best.LengthMi,
					WidthYd = best.WidthYd,
					Injuries = best.Injuries,
					Fatalities = best.Fatalities,
					Comments = a.Comments.Length > 0 ? a.Comments : best.Comments,
				});
			}

			var result = new List<DatFeature>();
			result.AddRange(linkedAreas.Where(Overlaps).OrderBy(a => EfRank(a.Ef)));
			result.AddRange(tracks.Where(Overlaps));
			result.AddRange(points.Where(Overlaps));
			return result;
		}

		// ── Write ────────────────────────────────────────────────────────────────────────────────────

		// The window file the page draws. Property names are short and fixed — damage-surveys.js reads them.
		private static Task WriteWindowAsync(string path, List<DatFeature> features, CancellationToken ct) =>
			AtomicWriteAsync(path, async stream =>
			{
				await using var w = new Utf8JsonWriter(stream);
				w.WriteStartObject();
				w.WriteString("type", "FeatureCollection");
				w.WriteStartArray("features");
				foreach (var f in features)
				{
					w.WriteStartObject();
					w.WriteString("type", "Feature");
					w.WritePropertyName("geometry");
					w.WriteRawValue(f.Geometry, skipInputValidation: true);
					w.WriteStartObject("properties");
					w.WriteString("layer", LayerSlug(f.Layer));
					w.WriteString("ef", f.Ef);
					w.WriteNumber("efn", EfRank(f.Ef));
					w.WriteNumber("t0", f.T0);
					w.WriteNumber("t1", f.T1);
					w.WriteString("name", f.Name);
					w.WriteString("wfo", f.Wfo);
					if (f.TrackEf is { } tef) { w.WriteString("tef", tef); }
					if (f.Wind is { } wind) { w.WriteNumber("wind", wind); }
					if (f.LengthMi is { } len) { w.WriteNumber("len", Math.Round(len, 2)); }
					if (f.WidthYd is { } wid) { w.WriteNumber("wid", Math.Round(wid)); }
					w.WriteNumber("inj", f.Injuries);
					w.WriteNumber("fat", f.Fatalities);
					w.WriteString("com", f.Comments);
					if (f.DamageText.Length > 0) { w.WriteString("dmg", f.DamageText); }
					if (f.DegreeText.Length > 0) { w.WriteString("dod", f.DegreeText); }
					w.WriteEndObject();
					w.WriteEndObject();
				}
				w.WriteEndArray();
				w.WriteEndObject();
				await w.FlushAsync(ct);
			}, ct);

		// ── Helpers ──────────────────────────────────────────────────────────────────────────────────

		internal static string LayerSlug(DatLayer layer) => layer switch
		{
			DatLayer.Areas => "area",
			DatLayer.Tracks => "track",
			_ => "point",
		};

		private static string? Str(JsonElement p, string name) =>
			p.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

		private static long? Long(JsonElement p, string name) =>
			p.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v) ? v : null;

		// A number that may arrive as a JSON number OR a string (points' windspeed is "60"). DAT uses -99 as
		// "unknown", hence the Positive* wrappers below.
		private static double? Loose(JsonElement p, string name)
		{
			if (!p.TryGetProperty(name, out var el)) { return null; }
			if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)) { return d; }
			if (el.ValueKind == JsonValueKind.String &&
				double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) { return s; }
			return null;
		}

		private static int? PositiveInt(double? v) => v is > 0 ? (int)Math.Round(v.Value) : null;
		private static double? PositiveDouble(double? v) => v is > 0 ? v : null;

		// Bounding box of any GeoJSON geometry, walking its coordinate arrays to the [lon, lat] pairs.
		private static bool TryBounds(JsonElement geom, out GeoBox box)
		{
			var b = GeoBox.Empty;
			if (geom.TryGetProperty("coordinates", out var coords)) { Walk(coords, ref b); }
			box = b;
			return b.IsValid;

			static void Walk(JsonElement el, ref GeoBox acc)
			{
				if (el.ValueKind != JsonValueKind.Array) { return; }
				if (el.GetArrayLength() >= 2 && el[0].ValueKind == JsonValueKind.Number)
				{
					if (el[0].TryGetDouble(out var lon) && el[1].TryGetDouble(out var lat)) { acc = acc.Include(lon, lat); }
					return;
				}
				foreach (var child in el.EnumerateArray()) { Walk(child, ref acc); }
			}
		}
	}

	/// <summary>The DAT FeatureServer layer ids.</summary>
	internal enum DatLayer
	{
		Points = 0,
		Tracks = 1,
		Areas = 2,
	}

	/// <summary>A lon/lat bounding box.</summary>
	internal readonly record struct GeoBox(double MinLon, double MinLat, double MaxLon, double MaxLat)
	{
		public static GeoBox Empty => new(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
		public bool IsValid => MinLon <= MaxLon && MinLat <= MaxLat;

		public GeoBox Include(double lon, double lat) =>
			new(Math.Min(MinLon, lon), Math.Min(MinLat, lat), Math.Max(MaxLon, lon), Math.Max(MaxLat, lat));

		public bool Intersects(GeoBox o, double slack) =>
			MinLon - slack <= o.MaxLon && o.MinLon - slack <= MaxLon &&
			MinLat - slack <= o.MaxLat && o.MinLat - slack <= MaxLat;

		public double CenterDistance(GeoBox o)
		{
			var dx = (MinLon + MaxLon - o.MinLon - o.MaxLon) / 2;
			var dy = (MinLat + MaxLat - o.MinLat - o.MaxLat) / 2;
			return Math.Sqrt(dx * dx + dy * dy);
		}
	}

	/// <summary>One normalized DAT feature. Times are Unix ms UTC; T0 == T1 for polygons and points until a
	/// polygon is linked to a track.</summary>
	internal sealed record DatFeature(
		DatLayer Layer,
		string Geometry,
		GeoBox Box,
		long T0,
		long T1,
		string Ef,
		string Name,
		string Wfo,
		int? Wind,
		double? LengthMi,
		double? WidthYd,
		int Injuries,
		int Fatalities,
		string Comments,
		string DamageText,
		string DegreeText)
	{
		/// <summary>The linked track's rating, on a polygon only.</summary>
		public string? TrackEf { get; init; }
	}
}
