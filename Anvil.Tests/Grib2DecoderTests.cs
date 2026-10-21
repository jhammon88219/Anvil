using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Pins <see cref="Grib2Decoder"/> + <see cref="LambertConformalGrid"/> against a REAL HRRR message (composite
	/// reflectivity, 15Z run 2026-10-21, f01 — 5.3 complex packing + 2nd-order spatial differencing), and the paths
	/// the real file doesn't take (simple packing, a bitmap) against a hand-built message.
	/// </summary>
	/// <remarks>
	/// The fixture was checked by eye when it was taken (TiltCheck <c>--hrrr --png</c>: coherent storms where the
	/// HRRR had them), so the pinned max + its cell are a REGRESSION pin on a decode already judged right. The
	/// corners are not ours: they are the HRRR grid's published SW/NE points.
	/// </remarks>
	public class Grib2DecoderTests
	{
		private static GribField Fixture() =>
			Grib2Decoder.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hrrr-refc-2026102115-f01.grib2")));

		[Fact]
		public void Hrrr_refc_identifies_itself()
		{
			var f = Fixture();
			Assert.Equal((0, 16, 196), (f.Discipline, f.Category, f.Number));
			Assert.Equal(new DateTimeOffset(2026, 10, 21, 15, 0, 0, TimeSpan.Zero), f.RunUtc);
			Assert.Equal(new DateTimeOffset(2026, 10, 21, 16, 0, 0, TimeSpan.Zero), f.ValidUtc);
			Assert.Null(f.IntervalStartUtc);
			Assert.Equal((1799, 1059), (f.Grid.Nx, f.Grid.Ny));
			Assert.Equal(3000, f.Grid.Dx);
			Assert.Equal(1799 * 1059, f.Values.Length);
		}

		[Fact]
		public void Hrrr_grid_corners_match_the_published_ones()
		{
			var g = Fixture().Grid;
			var (swLat, swLon) = g.ToLatLon(0, 0);
			var (neLat, neLon) = g.ToLatLon(g.Nx - 1, g.Ny - 1);
			Assert.Equal(21.138, swLat, 3);
			Assert.Equal(-122.720, swLon, 3);
			Assert.Equal(47.842, neLat, 3);
			Assert.Equal(-60.917, neLon, 3);
		}

		[Theory]
		[InlineData(35.4, -97.6)]   // Oklahoma City
		[InlineData(44.9, -68.7)]   // Bangor, near the NE edge
		[InlineData(25.8, -80.2)]   // Miami
		public void Projection_round_trips(double lat, double lon)
		{
			var g = Fixture().Grid;
			var (i, j) = g.ToGrid(lat, lon);
			var (lat2, lon2) = g.ToLatLon(i, j);
			Assert.Equal(lat, lat2, 6);
			Assert.Equal(lon, lon2, 6);
		}

		[Fact]
		public void Hrrr_refc_values_are_pinned()
		{
			var f = Fixture();
			Assert.DoesNotContain(f.Values, float.IsNaN);
			Assert.Equal(-10f, f.Values.Min()); // REFC's floor ("no echo")
			var max = f.Values.Max();
			var at = Array.IndexOf(f.Values, max);
			Assert.Equal(PinnedMax, max, 2);
			Assert.Equal(PinnedMaxCell, (at % f.Grid.Nx, at / f.Grid.Nx));
			Assert.Equal(PinnedAtLeast40, f.Values.Count(v => v >= 40));
		}

		private const float PinnedMax = 62.625f;
		private static readonly (int, int) PinnedMaxCell = (941, 132);
		private const int PinnedAtLeast40 = 2483;

		// ── simple packing + bitmap, hand-built ─────────────────────────────────────────────────────────────

		[Fact]
		public void Simple_packing_with_a_bitmap_scatters_and_scales()
		{
			// 3 × 2 grid, point 4 missing; five packed 8-bit values 0,10,20,30,40 with R = 5, E = 1 (×2), D = 1 (÷10).
			var f = Grib2Decoder.Decode(BuildSimple(new byte[] { 0, 10, 20, 30, 40 }, bitmap: 0b1110_1100));
			Assert.Equal(new[] { 0.5f, 2.5f, 4.5f, float.NaN, 6.5f, 8.5f }, f.Values);
			Assert.Equal(new DateTimeOffset(2026, 5, 20, 18, 0, 0, TimeSpan.Zero) + TimeSpan.FromMinutes(45), f.ValidUtc);
		}

		[Fact]
		public void Not_grib_is_refused()
		{
			Assert.Throws<FormatException>(() => Grib2Decoder.Decode(new byte[32]));
		}

		private static byte[] BuildSimple(byte[] packed, byte bitmap)
		{
			var s1 = new byte[21];
			Head(s1, 1);
			BinaryPrimitives.WriteUInt16BigEndian(s1.AsSpan(12), 2026);
			s1[14] = 5; s1[15] = 20; s1[16] = 18;

			var s3 = new byte[81];
			Head(s3, 3);
			BinaryPrimitives.WriteUInt32BigEndian(s3.AsSpan(6), 6);
			BinaryPrimitives.WriteUInt16BigEndian(s3.AsSpan(12), 30);
			s3[14] = 6;
			BinaryPrimitives.WriteUInt32BigEndian(s3.AsSpan(30), 3);
			BinaryPrimitives.WriteUInt32BigEndian(s3.AsSpan(34), 2);
			BinaryPrimitives.WriteUInt32BigEndian(s3.AsSpan(38), 35_000_000);
			BinaryPrimitives.WriteUInt32BigEndian(s3.AsSpan(42), 260_000_000);
			BinaryPrimitives.WriteUInt32BigEndian(s3.AsSpan(51), 262_500_000);
			BinaryPrimitives.WriteUInt32BigEndian(s3.AsSpan(55), 3_000_000);
			BinaryPrimitives.WriteUInt32BigEndian(s3.AsSpan(59), 3_000_000);
			s3[64] = 0x40;
			BinaryPrimitives.WriteUInt32BigEndian(s3.AsSpan(65), 38_500_000);
			BinaryPrimitives.WriteUInt32BigEndian(s3.AsSpan(69), 38_500_000);

			var s4 = new byte[34];
			Head(s4, 4);
			s4[9] = 16; s4[10] = 196; s4[17] = 0;
			BinaryPrimitives.WriteUInt32BigEndian(s4.AsSpan(18), 45);

			var s5 = new byte[21];
			Head(s5, 5);
			BinaryPrimitives.WriteUInt32BigEndian(s5.AsSpan(5), (uint)packed.Length);
			BinaryPrimitives.WriteSingleBigEndian(s5.AsSpan(11), 5f);
			BinaryPrimitives.WriteUInt16BigEndian(s5.AsSpan(15), 1);
			BinaryPrimitives.WriteUInt16BigEndian(s5.AsSpan(17), 1);
			s5[19] = 8;

			var s6 = new byte[7];
			Head(s6, 6);
			s6[5] = 0; s6[6] = bitmap;

			var s7 = new byte[5 + packed.Length];
			Head(s7, 7);
			packed.CopyTo(s7, 5);

			var body = new[] { s1, s3, s4, s5, s6, s7 }.SelectMany(b => b).ToArray();
			var msg = new byte[16 + body.Length + 4];
			"GRIB"u8.CopyTo(msg);
			msg[7] = 2;
			BinaryPrimitives.WriteUInt64BigEndian(msg.AsSpan(8), (ulong)msg.Length);
			body.CopyTo(msg, 16);
			"7777"u8.CopyTo(msg.AsSpan(msg.Length - 4));
			return msg;
		}

		private static void Head(byte[] section, byte number)
		{
			BinaryPrimitives.WriteUInt32BigEndian(section, (uint)section.Length);
			section[4] = number;
		}
	}
}
