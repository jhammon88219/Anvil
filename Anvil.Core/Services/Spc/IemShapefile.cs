using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Anvil.Services
{
	/// <summary>
	/// The smallest shapefile reader that serves IEM's <c>watchwarn.py?accept=shapefile</c> export: a zip
	/// holding <c>.shp</c> (polygons) + <c>.dbf</c> (attributes), read record-by-record in step. Polygon
	/// (type 5) and null shapes only — that is all the export writes. Pure; no I/O beyond the byte array.
	/// </summary>
	/// <remarks>
	/// ⚠️ WHY A SHAPEFILE AND NOT THE EASIER FORMATS: the export's CSV has every attribute but no geometry,
	/// its KML has geometry but only the warning's issue→expire (not each polygon VERSION's span) and lists
	/// placemarks in a different order from the CSV, and the <c>api/1/vtec/sbw_interval</c> GeoJSON returns
	/// only a warning's FIRST polygon (Barnsdall's TO.W.47 vanished at 02:39 of a warning that ran to 03:15
	/// and was upgraded to CATASTROPHIC in its follow-ups). Only the shapefile has both in one row.
	/// </remarks>
	internal static class IemShapefile
	{
		/// <summary>One record: its attributes (trimmed strings; "" for blank or ***** numerics) and its
		/// polygons, each an outer ring followed by its holes, coordinates [lon, lat].</summary>
		internal sealed record Record(IReadOnlyDictionary<string, string> Fields, List<List<List<double[]>>> Polygons);

		internal static List<Record> Read(byte[] zipBytes)
		{
			using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
			byte[] Entry(string ext)
			{
				var e = zip.Entries.FirstOrDefault(x => x.FullName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
					?? throw new InvalidDataException($"shapefile zip has no {ext}");
				using var s = e.Open();
				using var m = new MemoryStream();
				s.CopyTo(m);
				return m.ToArray();
			}

			var fields = ReadDbf(Entry(".dbf"));
			var shapes = ReadShp(Entry(".shp"));
			var n = Math.Min(fields.Count, shapes.Count);
			var records = new List<Record>(n);
			for (var i = 0; i < n; i++)
			{
				if (fields[i] is { } f) { records.Add(new Record(f, shapes[i])); }
			}
			return records;
		}

		// ── .dbf (dBASE III): 32-byte header, 32-byte field descriptors to 0x0D, fixed-width records ──

		private static List<Dictionary<string, string>?> ReadDbf(byte[] b)
		{
			var count = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4));
			var headerLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8));
			var recordLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(10));
			var defs = new List<(string Name, int Len)>();
			for (var off = 32; off < headerLen && b[off] != 0x0D; off += 32)
			{
				var name = Encoding.ASCII.GetString(b, off, 11).Split('\0')[0];
				defs.Add((name, b[off + 16]));
			}

			var rows = new List<Dictionary<string, string>?>(count);
			for (var r = 0; r < count; r++)
			{
				var p = headerLen + r * recordLen;
				if (p + recordLen > b.Length) { break; }
				if (b[p] == (byte)'*') { rows.Add(null); continue; } // deleted record
				p++;
				var row = new Dictionary<string, string>(defs.Count, StringComparer.Ordinal);
				foreach (var (name, len) in defs)
				{
					var v = Encoding.Latin1.GetString(b, p, len).Trim();
					row[name] = v.Length > 0 && v.All(c => c == '*') ? string.Empty : v; // ***** = a null numeric
					p += len;
				}
				rows.Add(row);
			}
			return rows;
		}

		// ── .shp: 100-byte header, then (big-endian record header, little-endian content) per record ──

		private static List<List<List<List<double[]>>>> ReadShp(byte[] b)
		{
			var shapes = new List<List<List<List<double[]>>>>();
			var p = 100;
			while (p + 8 <= b.Length)
			{
				var contentLen = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p + 4)) * 2;
				var c = p + 8;
				p = c + contentLen;
				if (p > b.Length) { break; }

				var type = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(c));
				if (type != 5) { shapes.Add(new List<List<List<double[]>>>()); continue; } // null (or unsupported)

				var numParts = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(c + 36));
				var numPoints = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(c + 40));
				var partsAt = c + 44;
				var pointsAt = partsAt + 4 * numParts;
				var rings = new List<List<double[]>>(numParts);
				for (var i = 0; i < numParts; i++)
				{
					var start = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(partsAt + 4 * i));
					var end = i + 1 < numParts ? BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(partsAt + 4 * (i + 1))) : numPoints;
					var ring = new List<double[]>(end - start);
					for (var k = start; k < end; k++)
					{
						var at = pointsAt + 16 * k;
						ring.Add(new[]
						{
							Math.Round(BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(at)), 4),
							Math.Round(BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(at + 8)), 4),
						});
					}
					rings.Add(ring);
				}
				shapes.Add(GroupRings(rings));
			}
			return shapes;
		}

		/// <summary>
		/// Shapefile rings are flat: an OUTER ring runs clockwise, a HOLE counter-clockwise, and a hole
		/// belongs to the outer ring before it. GeoJSON needs them grouped. ⚠️ Emitting each ring as its own
		/// polygon fills holes; emitting all as ONE polygon turns a county's islands into holes in nothing.
		/// </summary>
		internal static List<List<List<double[]>>> GroupRings(List<List<double[]>> rings)
		{
			var polygons = new List<List<List<double[]>>>();
			foreach (var ring in rings)
			{
				if (ring.Count < 4) { continue; }
				if (SignedArea(ring) <= 0 || polygons.Count == 0) { polygons.Add(new List<List<double[]>> { ring }); } // clockwise = outer
				else { polygons[^1].Add(ring); }
			}
			return polygons;
		}

		// Shoelace in lon/lat: positive = counter-clockwise.
		private static double SignedArea(List<double[]> ring)
		{
			double sum = 0;
			for (var i = 0; i < ring.Count - 1; i++) { sum += ring[i][0] * ring[i + 1][1] - ring[i + 1][0] * ring[i][1]; }
			return sum / 2;
		}
	}
}
