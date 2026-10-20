using System;
using System.Collections.Generic;
using System.IO;

namespace Anvil.Services
{
	/// <summary>
	/// One directory entry. <see cref="RunLength"/> ≥ 1 = that many CONSECUTIVE tile ids sharing one piece of tile data at
	/// <see cref="Offset"/> (from the tile data section); 0 = a pointer to a LEAF directory at <see cref="Offset"/> (from the
	/// leaf directories section) holding the entries from <see cref="TileId"/> up to the next entry's id.
	/// </summary>
	public readonly record struct PmtilesEntry(ulong TileId, ulong Offset, uint Length, uint RunLength)
	{
		public bool IsLeaf => RunLength == 0;
	}

	/// <summary>Reads a PMTiles v3 directory (decompressed): varint count, then the ids (delta), run lengths, lengths and
	/// offsets (0 = right after the previous entry's data, else offset + 1), each as its own column.</summary>
	public static class PmtilesDirectory
	{
		public static IReadOnlyList<PmtilesEntry> Decode(ReadOnlySpan<byte> data)
		{
			var at = 0;
			var count = checked((int)ReadVarint(data, ref at));
			var ids = new ulong[count];
			var runs = new uint[count];
			var lengths = new uint[count];

			ulong id = 0;
			for (var i = 0; i < count; i++) { id += ReadVarint(data, ref at); ids[i] = id; }
			for (var i = 0; i < count; i++) runs[i] = checked((uint)ReadVarint(data, ref at));
			for (var i = 0; i < count; i++) lengths[i] = checked((uint)ReadVarint(data, ref at));

			var entries = new PmtilesEntry[count];
			for (var i = 0; i < count; i++)
			{
				var v = ReadVarint(data, ref at);
				var offset = v == 0 && i > 0 ? entries[i - 1].Offset + entries[i - 1].Length : v - 1;
				entries[i] = new PmtilesEntry(ids[i], offset, lengths[i], runs[i]);
			}
			return entries;
		}

		private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int at)
		{
			ulong value = 0;
			for (var shift = 0; shift < 64; shift += 7)
			{
				if (at >= data.Length) throw new InvalidDataException("A PMTiles directory ended mid-number.");
				var b = data[at++];
				value |= (ulong)(b & 0x7F) << shift;
				if ((b & 0x80) == 0) return value;
			}
			throw new InvalidDataException("A PMTiles directory number is too long.");
		}
	}
}
