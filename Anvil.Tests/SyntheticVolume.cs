using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace Anvil.Tests
{
	/// <summary>
	/// Builds structurally-valid Level II volumes in memory, so the tilt-extraction tests need no
	/// checked-in ~20 MB .V06 fixture (and no network). We synthesize only the fields the extractor
	/// actually reads — the Message-31 radial header and the moment-block markers — which is enough to
	/// drive every decision it makes.
	/// </summary>
	/// <remarks>
	/// Volume wire format, per <c>Level2Format.TryExtractTiltByAngle</c>: a 24-byte file header, then a
	/// series of LDM records, each a 4-byte big-endian control word (the record's byte length; the sign
	/// is ignored) followed by that many bytes of bzip2 data.
	/// <para>
	/// Radial block layout — only these offsets are load-bearing, all relative to the ICAO:
	/// <c>+0</c> ICAO (4 ASCII), <c>+4</c> ms-of-day (u32, must be &lt;= 86_400_000), <c>+12</c> azimuth
	/// (f32 BE, must be in [0,360)), <c>+22</c> elevation NUMBER (1..32), <c>+24</c> elevation ANGLE
	/// (f32 BE, must be in [-2,75]). Then the moment blocks: a 4-char name (<c>DREF</c>/<c>DVEL</c>)
	/// with a u16 gate count 8 bytes later, which must be 1..2000 or HasMoment rejects it.
	/// </para>
	/// Everything else is zero-filled on purpose: 0x00 is neither alphanumeric (so it can't produce a
	/// stray ICAO match in TryDetectIcao) nor part of a moment name.
	/// </remarks>
	internal static class SyntheticVolume
	{
		public const string DefaultIcao = "KTLX";

		private const int MarkerOffset = 48;
		private const int BlockLength = 56;

		/// <summary>
		/// One Message-31-shaped radial block. <paramref name="marker"/> is a 4-char LOWERCASE tag the
		/// tests use to identify which blocks survived into the extracted output — lowercase because
		/// TryDetectIcao only considers A-Z/0-9, so a marker can never be mistaken for a callsign.
		/// </summary>
		public static byte[] Radial(
			int elevationNumber,
			float elevationAngle,
			string marker,
			bool reflectivity = true,
			bool velocity = false,
			string icao = DefaultIcao,
			float azimuth = 12.5f)
		{
			if (marker.Length != 4) throw new ArgumentException("marker must be 4 chars", nameof(marker));

			var block = new byte[BlockLength];
			Write(block, 0, icao);
			BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(4, 4), 43_200_000);   // noon, a valid ms-of-day
			BinaryPrimitives.WriteSingleBigEndian(block.AsSpan(12, 4), azimuth);
			block[22] = (byte)elevationNumber;
			BinaryPrimitives.WriteSingleBigEndian(block.AsSpan(24, 4), elevationAngle);

			// Moment blocks. The gate count sits 8 bytes after the name and must look plausible.
			if (reflectivity) WriteMoment(block, 28, "DREF", gates: 1832);
			if (velocity) WriteMoment(block, 38, "DVEL", gates: 1192);

			Write(block, MarkerOffset, marker);
			return block;
		}

		/// <summary>
		/// A non-radial block (no moment markers, no valid elevation number), which the extractor treats
		/// as leading metadata and copies into every extracted tilt.
		/// </summary>
		public static byte[] Metadata(string marker)
		{
			var block = new byte[BlockLength];
			Write(block, MarkerOffset, marker);
			return block;
		}

		/// <summary>Wraps blocks into a volume: 24-byte header + one bzip2 LDM record per block.</summary>
		public static byte[] Volume(params byte[][] blocks)
		{
			using var output = new MemoryStream();
			output.Write(new byte[24]);                     // file header — content is never parsed, only copied

			var controlWord = new byte[4];
			foreach (var block in blocks)
			{
				var compressed = Bzip2(block);
				BinaryPrimitives.WriteInt32BigEndian(controlWord, compressed.Length);
				output.Write(controlWord);
				output.Write(compressed);
			}

			return output.ToArray();
		}

		/// <summary>How many times <paramref name="marker"/> appears in an extracted tilt — i.e. whether
		/// (and how often) the block carrying it was kept.</summary>
		public static int CountMarker(byte[] haystack, string marker)
		{
			var needle = System.Text.Encoding.ASCII.GetBytes(marker);
			var count = 0;
			for (var i = 0; i + needle.Length <= haystack.Length; i++)
			{
				var k = 0;
				while (k < needle.Length && haystack[i + k] == needle[k]) k++;
				if (k == needle.Length) count++;
			}
			return count;
		}

		private static void WriteMoment(byte[] block, int offset, string name, ushort gates)
		{
			Write(block, offset, name);
			BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(offset + 8, 2), gates);
		}

		private static void Write(byte[] block, int offset, string ascii)
		{
			for (var i = 0; i < ascii.Length; i++) block[offset + i] = (byte)ascii[i];
		}

		private static byte[] Bzip2(byte[] data)
		{
			using var output = new MemoryStream();
			using (var bz = new BZip2Stream(output, CompressionMode.Compress, false))
			{
				bz.Write(data, 0, data.Length);
			}
			return output.ToArray();
		}
	}
}
