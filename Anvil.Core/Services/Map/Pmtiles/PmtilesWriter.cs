using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Anvil.Services
{
	/// <summary>
	/// Writes the parts of a PMTiles v3 file that aren't tile data: the header, and the directories (one root, plus leaves
	/// when the entries don't fit in it). Directories are gzip'd, the archive's internal compression. The read side's
	/// mirror (<see cref="PmtilesHeader"/>, <see cref="PmtilesDirectory"/>); <c>BasemapDownloadTests</c> round-trips them.
	/// </summary>
	public static class PmtilesWriter
	{
		/// <summary>The root directory must fit beside the header in the first 16 KiB (the spec's one-read rule).</summary>
		public const int MaxRootLength = PmtilesHeader.FirstReadLength - PmtilesHeader.Length;

		/// <summary>One directory, uncompressed: count, delta ids, run lengths, lengths, offsets (0 = right after the previous).</summary>
		public static byte[] SerializeDirectory(IReadOnlyList<PmtilesEntry> entries)
		{
			var o = new MemoryStream();
			Varint(o, (ulong)entries.Count);
			ulong last = 0;
			foreach (var e in entries) { Varint(o, e.TileId - last); last = e.TileId; }
			foreach (var e in entries) Varint(o, e.RunLength);
			foreach (var e in entries) Varint(o, e.Length);
			for (var i = 0; i < entries.Count; i++)
			{
				var follows = i > 0 && entries[i].Offset == entries[i - 1].Offset + entries[i - 1].Length;
				Varint(o, follows ? 0 : entries[i].Offset + 1);
			}
			return o.ToArray();
		}

		/// <summary>
		/// The root + leaf directories for <paramref name="entries"/> (sorted by tile id), gzip'd. All in the root if it fits;
		/// else leaves of 4096 entries, doubling until the root of leaf pointers fits — go-pmtiles' own layout rule.
		/// </summary>
		public static (byte[] Root, byte[] Leaves) BuildDirectories(IReadOnlyList<PmtilesEntry> entries)
		{
			var all = Gzip(SerializeDirectory(entries));
			if (all.Length <= MaxRootLength) return (all, Array.Empty<byte>());

			// Slices of one array, not Skip/Take — a Full-detail map has millions of entries.
			var array = entries as PmtilesEntry[] ?? entries.ToArray();
			for (var leafSize = 4096; ; leafSize *= 2)
			{
				var leaves = new MemoryStream();
				var pointers = new List<PmtilesEntry>();
				for (var at = 0; at < array.Length; at += leafSize)
				{
					var chunk = new ArraySegment<PmtilesEntry>(array, at, Math.Min(leafSize, array.Length - at));
					var leaf = Gzip(SerializeDirectory(chunk));
					pointers.Add(new PmtilesEntry(chunk[0].TileId, (ulong)leaves.Length, (uint)leaf.Length, 0));
					leaves.Write(leaf);
				}
				var root = Gzip(SerializeDirectory(pointers));
				if (root.Length <= MaxRootLength) return (root, leaves.ToArray());
			}
		}

		/// <summary>The 127-byte header.</summary>
		public static byte[] HeaderBytes(PmtilesHeader h)
		{
			var b = new byte[PmtilesHeader.Length];
			"PMTiles"u8.CopyTo(b);
			b[7] = 3;
			void U64(int at, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), v);
			void I32(int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), v);
			U64(8, h.RootDirectoryOffset); U64(16, h.RootDirectoryLength);
			U64(24, h.MetadataOffset); U64(32, h.MetadataLength);
			U64(40, h.LeafDirectoriesOffset); U64(48, h.LeafDirectoriesLength);
			U64(56, h.TileDataOffset); U64(64, h.TileDataLength);
			U64(72, h.AddressedTilesCount); U64(80, h.TileEntriesCount); U64(88, h.TileContentsCount);
			b[96] = h.Clustered ? (byte)1 : (byte)0;
			b[97] = (byte)h.InternalCompression;
			b[98] = (byte)h.TileCompression;
			b[99] = h.TileType;
			b[100] = h.MinZoom;
			b[101] = h.MaxZoom;
			I32(102, h.MinLonE7); I32(106, h.MinLatE7); I32(110, h.MaxLonE7); I32(114, h.MaxLatE7);
			b[118] = h.CenterZoom;
			I32(119, h.CenterLonE7); I32(123, h.CenterLatE7);
			return b;
		}

		public static byte[] Gzip(byte[] data)
		{
			var o = new MemoryStream();
			using (var z = new GZipStream(o, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
			return o.ToArray();
		}

		private static void Varint(Stream o, ulong v)
		{
			while (v >= 0x80) { o.WriteByte((byte)(v | 0x80)); v >>= 7; }
			o.WriteByte((byte)v);
		}
	}
}
