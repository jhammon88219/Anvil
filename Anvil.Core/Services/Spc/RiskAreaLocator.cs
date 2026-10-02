using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Anvil.Services
{
	/// <summary>Where a point sits against an outlook's severe categories.</summary>
	/// <param name="Code">The HIGHEST category whose area contains the point (MRGL…HIGH), or null when outside all.</param>
	/// <param name="NearestEdgeMeters">Outside only: distance to the nearest severe area's edge.</param>
	/// <param name="Bearing">Outside only: the compass direction FROM the point TO that edge ("southeast").</param>
	public sealed record RiskAreaFix(string? Code, double? NearestEdgeMeters, string? Bearing)
	{
		public bool IsInside => Code is not null;
	}

	/// <summary>
	/// Tests a point against a CATEGORICAL outlook GeoJSON (our cache files: features carry <c>LABEL</c> = TSTM / MRGL /
	/// SLGT / ENH / MDT / HIGH; Polygon or MultiPolygon, first ring outer, the rest holes). Pure — text + point in.
	/// </summary>
	/// <remarks>
	/// ⚠️ General thunder (TSTM) is NOT a risk area here, same as the cards. Distances use a local equirectangular
	/// projection around the point — plenty for "42 mi southeast" at outlook scales, wrong only across the globe.
	/// </remarks>
	public static class RiskAreaLocator
	{
		private static readonly string[] Rank = { "MRGL", "SLGT", "ENH", "MDT", "HIGH" };
		private const double EarthRadiusM = 6_371_000.0;

		/// <summary>Null when the GeoJSON can't be read or holds no severe area.</summary>
		public static RiskAreaFix? Locate(string? geojson, double lat, double lon)
		{
			var areas = Read(geojson);
			if (areas.Count == 0) return null;

			string? best = null;
			foreach (var (code, polygons) in areas)
			{
				if (polygons.Any(p => Contains(p, lat, lon)) && (best is null || Array.IndexOf(Rank, code) > Array.IndexOf(Rank, best)))
				{
					best = code;
				}
			}
			if (best is not null) return new RiskAreaFix(best, null, null);

			// Outside: the nearest edge of ANY severe area, in metres east/north of the point.
			var cosLat = Math.Cos(lat * Math.PI / 180);
			(double X, double Y) Local(double[] c) =>
				((c[0] - lon) * Math.PI / 180 * EarthRadiusM * cosLat, (c[1] - lat) * Math.PI / 180 * EarthRadiusM);

			var bestD = double.MaxValue;
			(double X, double Y) bestP = default;
			foreach (var ring in areas.SelectMany(a => a.Polygons).SelectMany(p => p))
			{
				for (var i = 0; i + 1 < ring.Count; i++)
				{
					var a = Local(ring[i]);
					var b = Local(ring[i + 1]);
					var p = Closest(a, b);
					var d = Math.Sqrt(p.X * p.X + p.Y * p.Y);
					if (d < bestD) { bestD = d; bestP = p; }
				}
			}
			return new RiskAreaFix(null, bestD, Compass(Math.Atan2(bestP.X, bestP.Y) * 180 / Math.PI));
		}

		// The point on segment a→b nearest the origin (the user, in local metres).
		private static (double X, double Y) Closest((double X, double Y) a, (double X, double Y) b)
		{
			var dx = b.X - a.X;
			var dy = b.Y - a.Y;
			var len2 = dx * dx + dy * dy;
			var t = len2 == 0 ? 0 : Math.Clamp(-(a.X * dx + a.Y * dy) / len2, 0, 1);
			return (a.X + t * dx, a.Y + t * dy);
		}

		private static readonly string[] Points =
			{ "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };

		internal static string Compass(double degrees) => Points[(int)Math.Round(((degrees % 360) + 360) % 360 / 45.0) % 8];

		// Ray casting, holes subtract. A polygon = rings; ring = [lon, lat] points.
		private static bool Contains(List<List<double[]>> polygon, double lat, double lon)
		{
			if (polygon.Count == 0 || !InRing(polygon[0], lat, lon)) return false;
			return !polygon.Skip(1).Any(hole => InRing(hole, lat, lon));
		}

		private static bool InRing(List<double[]> ring, double lat, double lon)
		{
			var inside = false;
			for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
			{
				double xi = ring[i][0], yi = ring[i][1], xj = ring[j][0], yj = ring[j][1];
				if ((yi > lat) != (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi) inside = !inside;
			}
			return inside;
		}

		private static List<(string Code, List<List<List<double[]>>> Polygons)> Read(string? geojson)
		{
			var areas = new List<(string, List<List<List<double[]>>>)>();
			if (string.IsNullOrWhiteSpace(geojson)) return areas;
			try
			{
				using var doc = JsonDocument.Parse(geojson);
				if (!doc.RootElement.TryGetProperty("features", out var features)) return areas;
				foreach (var f in features.EnumerateArray())
				{
					if (!f.TryGetProperty("properties", out var props) || !props.TryGetProperty("LABEL", out var label)) continue;
					var code = label.GetString()?.ToUpperInvariant();
					if (code is null || Array.IndexOf(Rank, code) < 0) continue;
					if (!f.TryGetProperty("geometry", out var g) || g.ValueKind != JsonValueKind.Object) continue;
					var type = g.GetProperty("type").GetString();
					var coords = g.GetProperty("coordinates");
					var polygons = new List<List<List<double[]>>>();
					if (type == "Polygon") polygons.Add(Rings(coords));
					else if (type == "MultiPolygon") polygons.AddRange(coords.EnumerateArray().Select(Rings));
					if (polygons.Count > 0) areas.Add((code, polygons));
				}
			}
			catch (JsonException) { }
			catch (InvalidOperationException) { }
			catch (KeyNotFoundException) { }
			return areas;
		}

		private static List<List<double[]>> Rings(JsonElement polygon) =>
			polygon.EnumerateArray()
				.Select(r => r.EnumerateArray().Select(p => new[] { p[0].GetDouble(), p[1].GetDouble() }).ToList())
				.ToList();
	}
}
