using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// What <c>model-field.js</c> draws a model field FROM, built from decoded GRIB. Pure (no I/O): the service writes
	/// the bytes these return.
	/// <list type="bullet">
	/// <item>A FRAME: one byte per grid cell in the model's OWN grid (row 0 = south), gzip'd —
	/// <see cref="ModelFieldEncoding"/> says what a byte means.</item>
	/// <item>The MESH: a lat/lon lattice over the grid; the page places each point on the map and stretches the frame
	/// between them, so it never does map projection maths per pixel.</item>
	/// <item>The DESCRIPTOR: one small JSON naming the frame, the mesh, the grid size, the encoding and the colours.</item>
	/// </list>
	/// </summary>
	/// <remarks>
	/// ⚠️ MESH SIZE IS MEASURED, not guessed: the page interpolates LINEARLY in Web Mercator between mesh points, and the
	/// HRRR grid's worst-case error (far north, where Mercator stretches most) was 3.9 km at 16×10, 990 m at 32×20, 244 m at
	/// 64×40 and 59 m at 128×80 (2026-10-21, a scratch script against this projection). 59 m is under a screen pixel until
	/// town zoom; 10 k points cost the GPU nothing.
	/// ⚠️ Frames are gzip'd but served as plain bytes (<c>.bin</c>): the page checks the gzip magic and inflates itself
	/// (DecompressionStream), so it never depends on how the virtual host labels the file.
	/// </remarks>
	public static class ModelFieldFrames
	{
		public const int MeshColumns = 128;
		public const int MeshRows = 80;

		/// <summary>The frame bytes for <paramref name="values"/> (gzip'd codes, row-major, south row first).</summary>
		public static byte[] EncodeFrame(float[] values, ModelFieldEncoding encoding)
		{
			var codes = new byte[values.Length];
			for (var k = 0; k < values.Length; k++) codes[k] = encoding.Code(values[k]);
			using var ms = new MemoryStream();
			using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(codes);
			return ms.ToArray();
		}

		/// <summary>Cell-by-cell max of same-grid fields — "Run so far" tracks (the max of every hour up to now).
		/// NaN loses to any value.</summary>
		public static float[] MaxOf(IReadOnlyList<float[]> fields)
		{
			if (fields.Count == 0) throw new ArgumentException("No fields to combine.", nameof(fields));
			var max = (float[])fields[0].Clone();
			for (var f = 1; f < fields.Count; f++)
			{
				var v = fields[f];
				if (v.Length != max.Length) throw new ArgumentException("Fields are on different grids.", nameof(fields));
				for (var k = 0; k < max.Length; k++)
					if (float.IsNaN(max[k]) || v[k] > max[k]) max[k] = v[k];
			}
			return max;
		}

		/// <summary>
		/// The mesh: (<see cref="MeshColumns"/>+1) × (<see cref="MeshRows"/>+1) points on an even (i, j) lattice from grid
		/// point (0, 0) to (Nx−1, Ny−1), row by row from the south, as <c>[lon, lat, lon, lat…]</c>.
		/// </summary>
		public static string MeshJson(LambertConformalGrid grid)
		{
			using var ms = new MemoryStream();
			using (var w = new Utf8JsonWriter(ms))
			{
				w.WriteStartObject();
				w.WriteNumber("cols", MeshColumns);
				w.WriteNumber("rows", MeshRows);
				w.WriteNumber("nx", grid.Nx);
				w.WriteNumber("ny", grid.Ny);
				w.WriteStartArray("lonlat");
				for (var r = 0; r <= MeshRows; r++)
				{
					var j = r * (grid.Ny - 1) / (double)MeshRows;
					for (var c = 0; c <= MeshColumns; c++)
					{
						var (lat, lon) = grid.ToLatLon(c * (grid.Nx - 1) / (double)MeshColumns, j);
						w.WriteRawValue(Math.Round(lon, 5).ToString(CultureInfo.InvariantCulture));
						w.WriteRawValue(Math.Round(lat, 5).ToString(CultureInfo.InvariantCulture));
					}
				}
				w.WriteEndArray();
				w.WriteEndObject();
			}
			return System.Text.Encoding.UTF8.GetString(ms.ToArray());
		}

		/// <summary>The page's descriptor for one frame (<c>window.setModelField</c>).</summary>
		public static string DescriptorJson(string frameUrl, string meshUrl, LambertConformalGrid grid, HrrrField field)
		{
			using var ms = new MemoryStream();
			using (var w = new Utf8JsonWriter(ms))
			{
				w.WriteStartObject();
				w.WriteString("url", frameUrl);
				w.WriteString("mesh", meshUrl);
				w.WriteNumber("nx", grid.Nx);
				w.WriteNumber("ny", grid.Ny);
				w.WriteNumber("scale", field.Encoding.Scale);
				w.WriteNumber("offset", field.Encoding.Offset);
				w.WriteString("ramp", field.RampId);
				if (ModelRamps.ById(field.RampId) is { } ramp)
				{
					w.WriteStartArray("stops");
					foreach (var s in ramp.Stops)
					{
						w.WriteStartObject();
						w.WriteNumber("v", s.V);
						w.WriteStartArray("color");
						foreach (var c in s.Color) w.WriteNumberValue(c);
						w.WriteEndArray();
						w.WriteEndObject();
					}
					w.WriteEndArray();
				}
				w.WriteEndObject();
			}
			return System.Text.Encoding.UTF8.GetString(ms.ToArray());
		}
	}
}
