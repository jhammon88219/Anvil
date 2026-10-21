using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Decodes ONE GRIB2 message (the byte range a model's <c>.idx</c> names) into a <see cref="GribField"/>. Pure:
	/// no I/O, no clock, no logging. Only what NCEP's convection-allowing models actually send is supported — a
	/// message outside that throws <see cref="FormatException"/> naming what it met, rather than guessing.
	/// </summary>
	/// <remarks>
	/// Supported (WMO GRIB2 manual, FM 92; the unpacking follows NCEP's g2clib <c>comunpack.c</c>):
	/// <list type="bullet">
	/// <item>Grid 3.30 Lambert conformal on a sphere (shape 6) — HRRR, NAM 3 km, RRFS.</item>
	/// <item>Product 4.0 (an instant) and 4.8 (a statistic over an interval — the hourly-max fields: UH, hail).</item>
	/// <item>Data 5.0 simple packing, 5.2 complex packing, 5.3 complex packing + spatial differencing (HRRR's REFC).</item>
	/// <item>Bitmap 6: none (255) or a bitmap in the message (0).</item>
	/// </list>
	/// ⚠️ GRIB2's signed numbers are SIGN-MAGNITUDE (top bit = sign), not two's complement: the binary and decimal
	/// scale factors, the latitude of the first point, and the spatial-differencing minimum. Reading E as a plain
	/// short turns HRRR's −4 (0x8004) into −32764 and every value into the reference.
	/// ⚠️ 5.3's FIRST one or two values are placeholders in the packed stream; the true ones ride in front of the
	/// group references, and the differencing is undone over NON-MISSING points only.
	/// Pinned against a real HRRR message by <c>Anvil.Tests/Grib2DecoderTests.cs</c>.
	/// </remarks>
	public static class Grib2Decoder
	{
		public static GribField Decode(ReadOnlySpan<byte> message)
		{
			if (message.Length < 16 || message[0] != 'G' || message[1] != 'R' || message[2] != 'I' || message[3] != 'B')
				throw new FormatException("Not a GRIB message (no 'GRIB' at the start).");
			if (message[7] != 2) throw new FormatException($"GRIB edition {message[7]}; only GRIB2 is supported.");
			var discipline = message[6];
			var total = (long)BinaryPrimitives.ReadUInt64BigEndian(message[8..]);
			if (total > message.Length) throw new FormatException($"Message is {total} bytes but only {message.Length} were given.");

			DateTimeOffset? run = null;
			LambertConformalGrid? grid = null;
			Product? product = null;
			Packing? packing = null;
			bool[]? bitmap = null;
			float[]? values = null;

			var p = 16;
			while (p + 4 <= total)
			{
				if (message[p] == '7' && message[p + 1] == '7' && message[p + 2] == '7' && message[p + 3] == '7') break;
				var len = (int)BinaryPrimitives.ReadUInt32BigEndian(message[p..]);
				var s = message.Slice(p, len);
				switch (s[4])
				{
					case 1: run = ReadTime(s[12..]); break;
					case 2: break; // local use
					case 3: grid = ReadGrid(s); break;
					case 4: product = ReadProduct(s); break;
					case 5: packing = ReadPacking(s); break;
					case 6:
						if (s[5] == 0) bitmap = ReadBitmap(s[6..], grid?.Count ?? throw new FormatException("Bitmap before the grid."));
						else if (s[5] != 255) throw new FormatException($"Bitmap indicator {s[5]} (a pre-defined or earlier bitmap) is not supported.");
						break;
					case 7:
						if (grid is null || packing is null) throw new FormatException("Data section before the grid or the packing.");
						values = Unpack(s[5..], packing, grid.Count, bitmap);
						break;
					default: throw new FormatException($"Unexpected GRIB2 section {s[4]}.");
				}
				p += len;
			}

			if (run is null || grid is null || product is null || values is null)
				throw new FormatException("Message is missing its identification, grid, product or data section.");
			var valid = product.IntervalEnd ?? run.Value + product.Forecast;
			return new GribField(discipline, product.Category, product.Number, run.Value, valid,
				product.IntervalEnd is null ? null : run.Value + product.Forecast, grid, values);
		}

		// ── sections ────────────────────────────────────────────────────────────────────────────────────────

		private static DateTimeOffset ReadTime(ReadOnlySpan<byte> t) => new(
			BinaryPrimitives.ReadUInt16BigEndian(t), t[2], t[3], t[4], t[5], t[6], TimeSpan.Zero);

		private static LambertConformalGrid ReadGrid(ReadOnlySpan<byte> s)
		{
			var template = BinaryPrimitives.ReadUInt16BigEndian(s[12..]);
			if (template != 30) throw new FormatException($"Grid template 3.{template}; only 3.30 (Lambert conformal) is supported.");
			if (s[14] != 6) throw new FormatException($"Earth shape {s[14]}; only 6 (sphere, r = 6 371 229 m) is supported.");
			var scan = s[64];
			if (scan != 0x40) throw new FormatException($"Scanning mode 0x{scan:X2}; only 0x40 (rows south → north) is supported.");
			return new LambertConformalGrid(
				Nx: (int)BinaryPrimitives.ReadUInt32BigEndian(s[30..]),
				Ny: (int)BinaryPrimitives.ReadUInt32BigEndian(s[34..]),
				FirstLat: SignMag32(s[38..]) * 1e-6,
				FirstLon: BinaryPrimitives.ReadUInt32BigEndian(s[42..]) * 1e-6,
				LoV: BinaryPrimitives.ReadUInt32BigEndian(s[51..]) * 1e-6,
				Latin1: SignMag32(s[65..]) * 1e-6,
				Latin2: SignMag32(s[69..]) * 1e-6,
				Dx: BinaryPrimitives.ReadUInt32BigEndian(s[55..]) * 1e-3,
				Dy: BinaryPrimitives.ReadUInt32BigEndian(s[59..]) * 1e-3,
				EarthRadius: 6_371_229);
		}

		private sealed record Product(int Category, int Number, TimeSpan Forecast, DateTimeOffset? IntervalEnd);

		private static Product ReadProduct(ReadOnlySpan<byte> s)
		{
			var template = BinaryPrimitives.ReadUInt16BigEndian(s[7..]);
			if (template is not (0 or 8)) throw new FormatException($"Product template 4.{template}; only 4.0 and 4.8 are supported.");
			var forecast = Duration(s[17], BinaryPrimitives.ReadUInt32BigEndian(s[18..]));
			return new Product(s[9], s[10], forecast, template == 8 ? ReadTime(s[34..]) : null);
		}

		private static TimeSpan Duration(byte unit, uint n) => unit switch
		{
			0 => TimeSpan.FromMinutes(n),
			1 => TimeSpan.FromHours(n),
			2 => TimeSpan.FromDays(n),
			10 => TimeSpan.FromHours(3 * n),
			11 => TimeSpan.FromHours(6 * n),
			12 => TimeSpan.FromHours(12 * n),
			13 => TimeSpan.FromSeconds(n),
			_ => throw new FormatException($"Time range unit {unit} is not supported."),
		};

		private sealed record Packing(
			int Template, float Reference, int BinaryScale, int DecimalScale, int Bits,
			int MissingMode, uint PrimaryMissing, uint SecondaryMissing,
			int Groups, int WidthRef, int WidthBits, uint LengthRef, int LengthIncrement, uint LastLength, int LengthBits,
			int DiffOrder, int DiffOctets);

		private static Packing ReadPacking(ReadOnlySpan<byte> s)
		{
			var template = BinaryPrimitives.ReadUInt16BigEndian(s[9..]);
			if (template is not (0 or 2 or 3)) throw new FormatException($"Data template 5.{template}; only 5.0, 5.2 and 5.3 are supported.");
			var reference = BinaryPrimitives.ReadSingleBigEndian(s[11..]);
			var e = SignMag16(s[15..]);
			var d = SignMag16(s[17..]);
			var bits = s[19];
			if (template == 0) return new Packing(0, reference, e, d, bits, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
			return new Packing(template, reference, e, d, bits,
				MissingMode: s[22],
				PrimaryMissing: BinaryPrimitives.ReadUInt32BigEndian(s[23..]),
				SecondaryMissing: BinaryPrimitives.ReadUInt32BigEndian(s[27..]),
				Groups: (int)BinaryPrimitives.ReadUInt32BigEndian(s[31..]),
				WidthRef: s[35], WidthBits: s[36],
				LengthRef: BinaryPrimitives.ReadUInt32BigEndian(s[37..]), LengthIncrement: s[41],
				LastLength: BinaryPrimitives.ReadUInt32BigEndian(s[42..]), LengthBits: s[46],
				DiffOrder: template == 3 ? s[47] : 0, DiffOctets: template == 3 ? s[48] : 0);
		}

		private static bool[] ReadBitmap(ReadOnlySpan<byte> b, int count)
		{
			var map = new bool[count];
			for (var k = 0; k < count; k++) map[k] = (b[k >> 3] & (0x80 >> (k & 7))) != 0;
			return map;
		}

		// ── unpacking ───────────────────────────────────────────────────────────────────────────────────────

		private static float[] Unpack(ReadOnlySpan<byte> data, Packing pk, int gridCount, bool[]? bitmap)
		{
			var present = bitmap is null ? gridCount : Count(bitmap);
			var ints = new long[present];
			var missing = pk.Template == 0 ? null : new bool[present];
			if (pk.Template == 0) UnpackSimple(data, pk, ints);
			else UnpackComplex(data, pk, ints, missing!);

			var bScale = Math.Pow(2, pk.BinaryScale);
			var dScale = Math.Pow(10, -pk.DecimalScale);
			var packed = new float[present];
			for (var k = 0; k < present; k++)
				packed[k] = missing is not null && missing[k] ? float.NaN : (float)((pk.Reference + ints[k] * bScale) * dScale);

			if (bitmap is null) return packed;
			var values = new float[gridCount];
			for (int k = 0, q = 0; k < gridCount; k++) values[k] = bitmap[k] ? packed[q++] : float.NaN;
			return values;
		}

		private static void UnpackSimple(ReadOnlySpan<byte> data, Packing pk, long[] ints)
		{
			if (pk.Bits == 0) return; // every value is the reference
			var r = new BitReader(data);
			for (var k = 0; k < ints.Length; k++) ints[k] = (long)r.Read(pk.Bits);
		}

		private static void UnpackComplex(ReadOnlySpan<byte> data, Packing pk, long[] ints, bool[] missing)
		{
			var r = new BitReader(data);
			long first = 0, second = 0, minSd = 0;
			if (pk.DiffOrder > 0)
			{
				var bits = pk.DiffOctets * 8;
				first = (long)r.Read(bits);
				if (pk.DiffOrder == 2) second = (long)r.Read(bits);
				var negative = r.Read(1) == 1;
				minSd = (long)r.Read(bits - 1);
				if (negative) minSd = -minSd;
			}

			var ng = pk.Groups;
			var refs = new ulong[ng];
			for (var g = 0; g < ng; g++) refs[g] = r.Read(pk.Bits);
			r.AlignToByte();
			var widths = new int[ng];
			for (var g = 0; g < ng; g++) widths[g] = pk.WidthRef + (int)r.Read(pk.WidthBits);
			r.AlignToByte();
			var lengths = new long[ng];
			for (var g = 0; g < ng; g++) lengths[g] = pk.LengthRef + (long)r.Read(pk.LengthBits) * pk.LengthIncrement;
			if (ng > 0) lengths[ng - 1] = pk.LastLength;
			r.AlignToByte();

			var refAllOnes = pk.Bits == 0 ? 0UL : (1UL << pk.Bits) - 1;
			var n = 0;
			for (var g = 0; g < ng; g++)
			{
				var w = widths[g];
				var allOnes = w == 0 ? 0UL : (1UL << w) - 1;
				for (var k = 0L; k < lengths[g]; k++, n++)
				{
					if (n >= ints.Length) throw new FormatException("Groups hold more values than the message has points.");
					if (w == 0)
					{
						// A constant group; with missing values on, an all-ones reference means the whole group is missing.
						missing[n] = pk.MissingMode >= 1 && pk.Bits > 0 && refs[g] == refAllOnes
							|| pk.MissingMode == 2 && pk.Bits > 0 && refs[g] == refAllOnes - 1;
						ints[n] = (long)refs[g];
						continue;
					}
					var v = r.Read(w);
					missing[n] = pk.MissingMode >= 1 && v == allOnes || pk.MissingMode == 2 && v == allOnes - 1;
					ints[n] = (long)(refs[g] + v);
				}
			}
			if (n != ints.Length) throw new FormatException($"Groups hold {n} values; the message has {ints.Length} points.");

			if (pk.DiffOrder == 0) return;
			// Undo the differencing over the NON-missing points only (they are what was differenced).
			var seen = 0;
			long prev = 0, prev2 = 0;
			for (var k = 0; k < ints.Length; k++)
			{
				if (missing[k]) continue;
				long value;
				if (seen == 0) value = first;
				else if (seen == 1 && pk.DiffOrder == 2) value = second;
				else value = pk.DiffOrder == 1 ? ints[k] + minSd + prev : ints[k] + minSd + 2 * prev - prev2;
				ints[k] = value;
				prev2 = prev;
				prev = value;
				seen++;
			}
		}

		// ── helpers ─────────────────────────────────────────────────────────────────────────────────────────

		private static int Count(bool[] map)
		{
			var c = 0;
			foreach (var b in map) if (b) c++;
			return c;
		}

		private static int SignMag16(ReadOnlySpan<byte> b)
		{
			var raw = BinaryPrimitives.ReadUInt16BigEndian(b);
			var magnitude = raw & 0x7FFF;
			return (raw & 0x8000) != 0 ? -magnitude : magnitude;
		}

		private static long SignMag32(ReadOnlySpan<byte> b)
		{
			var raw = BinaryPrimitives.ReadUInt32BigEndian(b);
			long magnitude = raw & 0x7FFF_FFFF;
			return (raw & 0x8000_0000) != 0 ? -magnitude : magnitude;
		}

		/// <summary>Big-endian, most-significant-bit-first bit reader over the data section.</summary>
		private ref struct BitReader
		{
			private readonly ReadOnlySpan<byte> _data;
			private long _bit;

			public BitReader(ReadOnlySpan<byte> data) { _data = data; _bit = 0; }

			public ulong Read(int bits)
			{
				if (bits == 0) return 0;
				if (bits > 57) throw new FormatException($"A {bits}-bit field is wider than the reader supports.");
				var byteIndex = (int)(_bit >> 3);
				var shift = (int)(_bit & 7);
				if ((_bit + bits + 7) >> 3 > _data.Length) throw new FormatException("Data section ended early.");
				ulong acc = 0;
				var need = (shift + bits + 7) >> 3;
				for (var k = 0; k < need; k++) acc = (acc << 8) | _data[byteIndex + k];
				_bit += bits;
				var spare = need * 8 - shift - bits;
				return (acc >> spare) & ((1UL << bits) - 1);
			}

			public void AlignToByte() => _bit = (_bit + 7) & ~7L;
		}
	}
}
