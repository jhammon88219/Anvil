using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Anvil.Services
{
	/// <summary>How a PMTiles section is compressed (spec values).</summary>
	public enum PmtilesCompression : byte { Unknown = 0, None = 1, Gzip = 2, Brotli = 3, Zstd = 4 }

	/// <summary>
	/// The fixed 127-byte PMTiles v3 header: where the root directory, metadata, leaf directories and tile data sit, and
	/// the archive's counts and extent. Spec: github.com/protomaps/PMTiles/blob/main/spec/v3/spec.md.
	/// </summary>
	public sealed record PmtilesHeader(
		ulong RootDirectoryOffset, ulong RootDirectoryLength,
		ulong MetadataOffset, ulong MetadataLength,
		ulong LeafDirectoriesOffset, ulong LeafDirectoriesLength,
		ulong TileDataOffset, ulong TileDataLength,
		ulong AddressedTilesCount, ulong TileEntriesCount, ulong TileContentsCount,
		bool Clustered, PmtilesCompression InternalCompression, PmtilesCompression TileCompression, byte TileType,
		byte MinZoom, byte MaxZoom, int MinLonE7, int MinLatE7, int MaxLonE7, int MaxLatE7,
		byte CenterZoom, int CenterLonE7, int CenterLatE7)
	{
		/// <summary>The header's size; the root directory always fits in the first 16 KiB with it.</summary>
		public const int Length = 127;

		/// <summary>One read of this many bytes from offset 0 gets the header AND the root directory.</summary>
		public const int FirstReadLength = 16384;

		public static PmtilesHeader Parse(ReadOnlySpan<byte> b)
		{
			if (b.Length < Length) throw new InvalidDataException("Too short to be a PMTiles header.");
			if (!b[..7].SequenceEqual("PMTiles"u8)) throw new InvalidDataException("Not a PMTiles file.");
			if (b[7] != 3) throw new InvalidDataException($"PMTiles version {b[7]} — only version 3 is read.");

			return new PmtilesHeader(
				U64(b, 8), U64(b, 16), U64(b, 24), U64(b, 32), U64(b, 40), U64(b, 48), U64(b, 56), U64(b, 64),
				U64(b, 72), U64(b, 80), U64(b, 88),
				b[96] == 1, (PmtilesCompression)b[97], (PmtilesCompression)b[98], b[99],
				b[100], b[101], I32(b, 102), I32(b, 106), I32(b, 110), I32(b, 114),
				b[118], I32(b, 119), I32(b, 123));
		}

		private static ulong U64(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b[at..]);
		private static int I32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b[at..]);

		/// <summary>Undo <paramref name="compression"/> on a directory or metadata section.</summary>
		public static byte[] Decompress(byte[] data, PmtilesCompression compression)
		{
			switch (compression)
			{
				case PmtilesCompression.None:
					return data;
				case PmtilesCompression.Gzip:
				{
					using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
					using var output = new MemoryStream();
					input.CopyTo(output);
					return output.ToArray();
				}
				case PmtilesCompression.Brotli:
				{
					using var input = new BrotliStream(new MemoryStream(data), CompressionMode.Decompress);
					using var output = new MemoryStream();
					input.CopyTo(output);
					return output.ToArray();
				}
				default:
					throw new NotSupportedException($"PMTiles {compression} compression isn't supported.");
			}
		}
	}
}
