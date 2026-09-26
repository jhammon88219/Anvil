using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// The pure half of the storm-cell overlay: parse IEM's storm-attribute CSV into scans, pick the scan a
	/// radar frame should show, and build each cell's past track and forecast. No I/O, no clock — the
	/// service fetches and caches, the view model decides WHEN, this decides WHAT.
	/// </summary>
	public static class StormCellTracks
	{
		// Forecast marks, as the NST product draws them.
		private static readonly int[] ForecastMinutes = { 15, 30, 45, 60 };

		// A past position must be reachable from the next one at this speed (plus slack), or the id was
		// recycled onto a different storm. 150 km/h is well above any real cell motion.
		private const double MaxCellSpeedKmPerMin = 2.5;
		private const double JumpSlackKm = 5.0;

		// Two sightings further apart than this are not one continuous track (a volume is ~4–10 min; one
		// missed detection is tolerated).
		private static readonly TimeSpan MaxTrackGap = TimeSpan.FromMinutes(12);

		private const double EarthRadiusKm = 6371.0;
		private const double KmPerNm = 1.852;

		/// <summary>
		/// Parses IEM's <c>nexrad_storm_attrs.py?fmt=csv</c> body into scans, oldest first. Columns are
		/// found BY NAME (IEM's header carries a typo, <c>MAZ_DBZ_H</c>, which a fix could rename), rows that
		/// don't parse are skipped, and a repeated (scan, id) keeps its first row. Returns an empty list for a
		/// body that isn't the CSV at all.
		/// </summary>
		public static List<StormCellScan> ParseCsv(string csv)
		{
			var scans = new SortedDictionary<DateTimeOffset, List<StormCell>>();
			if (string.IsNullOrWhiteSpace(csv)) { return new List<StormCellScan>(); }

			var lines = csv.Split('\n');
			var header = lines[0].Trim().Split(',');
			int Col(params string[] names)
			{
				foreach (var n in names)
				{
					var i = Array.FindIndex(header, h => h.Trim().Equals(n, StringComparison.OrdinalIgnoreCase));
					if (i >= 0) { return i; }
				}
				return -1;
			}

			int cValid = Col("VALID"), cId = Col("STORM_ID"), cTvs = Col("TVS"), cMeso = Col("MESO"),
				cPosh = Col("POSH"), cPoh = Col("POH"), cSize = Col("MAX_SIZE"), cVil = Col("VIL"),
				cDbz = Col("MAX_DBZ"), cDbzH = Col("MAX_DBZ_H", "MAZ_DBZ_H"), cTop = Col("TOP"),
				cDir = Col("DRCT"), cKt = Col("SKNT"), cLat = Col("LAT"), cLon = Col("LON");
			if (cValid < 0 || cId < 0 || cLat < 0 || cLon < 0) { return new List<StormCellScan>(); }

			var seen = new HashSet<(DateTimeOffset, string)>();
			for (var li = 1; li < lines.Length; li++)
			{
				var f = lines[li].Trim().Split(',');
				if (f.Length < header.Length) { continue; }
				if (!DateTimeOffset.TryParseExact(f[cValid], "yyyyMMddHHmm", CultureInfo.InvariantCulture,
						DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var valid)) { continue; }
				if (!TryD(f[cLat], out var lat) || !TryD(f[cLon], out var lon)) { continue; }
				var id = f[cId].Trim();
				if (id.Length == 0 || !seen.Add((valid, id))) { continue; }

				var tvs = At(f, cTvs).ToUpperInvariant();
				var cell = new StormCell(
					id, lat, lon,
					tvs is "TVS" or "ETVS" ? tvs : string.Empty,
					I(At(f, cMeso)), I(At(f, cPosh)), I(At(f, cPoh)), D(At(f, cSize)),
					I(At(f, cVil)), I(At(f, cDbz)), D(At(f, cDbzH)), D(At(f, cTop)),
					I(At(f, cDir)), I(At(f, cKt)));

				if (!scans.TryGetValue(valid, out var list)) { scans[valid] = list = new List<StormCell>(); }
				list.Add(cell);
			}

			return scans.Select(kv => new StormCellScan(kv.Key, kv.Value)).ToList();
		}

		/// <summary>
		/// The scan a frame at <paramref name="frameUtc"/> should show: the newest scan at or before the frame
		/// (a minute of slack for a scan stamped a hair after its frame), and only if it is within
		/// <see cref="StormCellThresholds.MaxFrameLag"/>. -1 = show nothing. Scans must be oldest first.
		/// </summary>
		public static int SelectScan(IReadOnlyList<StormCellScan> scans, DateTimeOffset frameUtc)
		{
			var limit = frameUtc + TimeSpan.FromMinutes(1);
			for (var i = scans.Count - 1; i >= 0; i--)
			{
				if (scans[i].Valid > limit) { continue; }
				return frameUtc - scans[i].Valid <= StormCellThresholds.MaxFrameLag ? i : -1;
			}
			return -1;
		}

		/// <summary>
		/// The cell's earlier positions (oldest first, NOT including this scan), walking back through the
		/// scans while the same id keeps turning up close enough in time and space to be the same storm.
		/// Stops at <see cref="StormCellThresholds.PastTrack"/>.
		/// </summary>
		public static List<(double Lat, double Lon)> PastTrack(IReadOnlyList<StormCellScan> scans, int scanIndex, string id)
		{
			var track = new List<(double, double)>();
			var start = scans[scanIndex].Valid;
			var cur = scans[scanIndex].Cells.FirstOrDefault(c => c.Id == id);
			if (cur is null) { return track; }
			var curTime = start;

			for (var i = scanIndex - 1; i >= 0; i--)
			{
				var t = scans[i].Valid;
				if (start - t > StormCellThresholds.PastTrack || curTime - t > MaxTrackGap) { break; }
				var prev = scans[i].Cells.FirstOrDefault(c => c.Id == id);
				if (prev is null) { continue; } // one missed detection; the gap check ends a real break
				var minutes = (curTime - t).TotalMinutes;
				if (DistanceKm(prev.Lat, prev.Lon, cur.Lat, cur.Lon) > minutes * MaxCellSpeedKmPerMin + JumpSlackKm)
				{
					break; // the id now belongs to a different storm
				}
				track.Add((prev.Lat, prev.Lon));
				cur = prev;
				curTime = t;
			}

			track.Reverse();
			return track;
		}

		/// <summary>Where the cell is forecast at +15/30/45/60 min, moving TOWARD its from-direction + 180°.
		/// Empty for a cell with no motion (a new cell: speed 0).</summary>
		public static List<(double Lat, double Lon)> Forecast(StormCell cell)
		{
			var points = new List<(double, double)>();
			if (cell.SpeedKt <= 0) { return points; }
			var toward = (cell.DirFromDeg + 180) % 360;
			foreach (var m in ForecastMinutes)
			{
				points.Add(Destination(cell.Lat, cell.Lon, toward, cell.SpeedKt * KmPerNm * m / 60.0));
			}
			return points;
		}

		/// <summary>
		/// The file the page reads: every scan with its cells, each carrying its past track and forecast
		/// precomputed (the page only picks a scan by time and draws it). Coordinates are [lon, lat], 4 dp.
		/// </summary>
		public static string ToPageJson(string site, IReadOnlyList<StormCellScan> scans)
		{
			using var buffer = new System.IO.MemoryStream();
			using (var w = new Utf8JsonWriter(buffer))
			{
				w.WriteStartObject();
				w.WriteString("site", site);
				w.WriteStartArray("scans");
				for (var i = 0; i < scans.Count; i++)
				{
					var scan = scans[i];
					w.WriteStartObject();
					w.WriteNumber("t", scan.Valid.ToUnixTimeMilliseconds());
					w.WriteStartArray("cells");
					foreach (var c in scan.Cells)
					{
						w.WriteStartObject();
						w.WriteString("id", c.Id);
						w.WriteNumber("lon", Math.Round(c.Lon, 4));
						w.WriteNumber("lat", Math.Round(c.Lat, 4));
						w.WriteString("tvs", c.Tvs);
						w.WriteNumber("meso", c.MesoRank);
						w.WriteNumber("posh", c.Posh);
						w.WriteNumber("poh", c.Poh);
						w.WriteNumber("size", c.MaxHailIn);
						w.WriteNumber("vil", c.Vil);
						w.WriteNumber("dbz", c.MaxDbz);
						w.WriteNumber("dbzh", c.MaxDbzHeightKft);
						w.WriteNumber("top", c.TopKft);
						w.WriteNumber("dir", c.DirFromDeg);
						w.WriteNumber("kt", c.SpeedKt);
						WritePoints(w, "past", PastTrack(scans, i, c.Id));
						WritePoints(w, "fcst", Forecast(c));
						w.WriteEndObject();
					}
					w.WriteEndArray();
					w.WriteEndObject();
				}
				w.WriteEndArray();
				w.WriteEndObject();
			}
			return Encoding.UTF8.GetString(buffer.ToArray());
		}

		private static void WritePoints(Utf8JsonWriter w, string name, List<(double Lat, double Lon)> points)
		{
			w.WriteStartArray(name);
			foreach (var (lat, lon) in points)
			{
				w.WriteStartArray();
				w.WriteNumberValue(Math.Round(lon, 4));
				w.WriteNumberValue(Math.Round(lat, 4));
				w.WriteEndArray();
			}
			w.WriteEndArray();
		}

		// ── Geometry (spherical earth; plenty for tens of km) ──

		internal static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
		{
			var p1 = Rad(lat1);
			var p2 = Rad(lat2);
			var dp = p2 - p1;
			var dl = Rad(lon2 - lon1);
			var a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
			return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
		}

		internal static (double Lat, double Lon) Destination(double lat, double lon, double bearingDeg, double km)
		{
			var d = km / EarthRadiusKm;
			var b = Rad(bearingDeg);
			var p1 = Rad(lat);
			var l1 = Rad(lon);
			var p2 = Math.Asin(Math.Sin(p1) * Math.Cos(d) + Math.Cos(p1) * Math.Sin(d) * Math.Cos(b));
			var l2 = l1 + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(p1), Math.Cos(d) - Math.Sin(p1) * Math.Sin(p2));
			return (p2 * 180 / Math.PI, l2 * 180 / Math.PI);
		}

		private static double Rad(double deg) => deg * Math.PI / 180;

		// ── Field parsing: IEM writes "NONE" (and occasionally blanks) for an absent value; that is 0. ──

		private static string At(string[] f, int i) => i >= 0 && i < f.Length ? f[i].Trim() : string.Empty;

		private static bool TryD(string s, out double v) =>
			double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

		private static double D(string s) => TryD(s, out var v) ? v : 0;

		private static int I(string s) => TryD(s, out var v) ? (int)Math.Round(v) : 0;
	}
}
