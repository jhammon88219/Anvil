using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The offline-map planner's pieces: PMTiles tile ids, the header + directory readers, the area cover and the plan.
	/// LIVE CHECK (2026-10-20, build 20261020, against <c>pmtiles extract --dry-run</c>): Lower 48 at levels 6 / 10 / 13 =
	/// 128 / 23,179 / 1,446,253 region tiles, 125 / 19,028 / 1,025,159 entries, 1 / 4 / 39 leaf directories, 4.7 MB /
	/// 403 MB / 4.4 GB — all exact. Alaska at 10: ours 26,356 / 17,357 / 6; pmtiles 26,365 / 17,366 / 7 because its cover
	/// mishandles a box edge ON the date line — nudged to 179.99° it gives exactly ours.
	/// </summary>
	public class BasemapExtractPlannerTests
	{
		// ── tile ids ──

		[Theory]
		[InlineData(0, 0u, 0u, 0UL)]
		[InlineData(1, 0u, 0u, 1UL)]
		[InlineData(1, 0u, 1u, 2UL)]
		[InlineData(1, 1u, 1u, 3UL)]
		[InlineData(1, 1u, 0u, 4UL)]
		[InlineData(2, 0u, 0u, 5UL)]
		[InlineData(12, 3423u, 1763u, 19078479UL)] // go-pmtiles' own test value
		public void Tile_ids_follow_the_pmtiles_hilbert_curve(int z, uint x, uint y, ulong id)
		{
			Assert.Equal(id, PmtilesTileId.FromZxy(z, x, y));
		}

		[Fact]
		public void Each_zoom_uses_every_id_in_its_block_once()
		{
			for (var z = 0; z <= 5; z++)
			{
				var n = 1U << z;
				var ids = new HashSet<ulong>();
				for (uint x = 0; x < n; x++)
					for (uint y = 0; y < n; y++)
						Assert.True(ids.Add(PmtilesTileId.FromZxy(z, x, y)));
				Assert.Equal(PmtilesTileId.ZoomBase(z), ids.Min());
				Assert.Equal(PmtilesTileId.ZoomBase(z + 1) - 1, ids.Max());
			}
		}

		[Fact]
		public void A_tiles_descendants_are_one_contiguous_id_block()
		{
			// Every z3 tile's z6 descendants land exactly in its DescendantRange — what lets the cover be ranges.
			for (uint x = 0; x < 8; x++)
				for (uint y = 0; y < 8; y++)
				{
					var (first, count) = PmtilesTileId.DescendantRange(3, x, y, 6);
					for (uint dx = 0; dx < 8; dx++)
						for (uint dy = 0; dy < 8; dy++)
						{
							var id = PmtilesTileId.FromZxy(6, x * 8 + dx, y * 8 + dy);
							Assert.InRange(id, first, first + count - 1);
						}
				}
		}

		// ── header + directory ──

		[Fact]
		public void A_header_reads_back()
		{
			var h = PmtilesHeader.Parse(Archive.Header(rootOffset: 127, rootLength: 9, leafOffset: 200, leafLength: 50,
				dataOffset: 300, dataLength: 4000, maxZoom: 15));
			Assert.Equal(127UL, h.RootDirectoryOffset);
			Assert.Equal(9UL, h.RootDirectoryLength);
			Assert.Equal(200UL, h.LeafDirectoriesOffset);
			Assert.Equal(300UL, h.TileDataOffset);
			Assert.Equal(PmtilesCompression.None, h.InternalCompression);
			Assert.Equal(15, h.MaxZoom);
		}

		[Fact]
		public void Not_a_pmtiles_file_is_refused()
		{
			Assert.Throws<InvalidDataException>(() => PmtilesHeader.Parse(new byte[200]));
		}

		[Fact]
		public void A_directory_decodes_ids_runs_and_offsets()
		{
			var entries = new[]
			{
				new PmtilesEntry(5, 0, 100, 1),
				new PmtilesEntry(6, 100, 50, 3),   // right after the previous data → stored as offset 0
				new PmtilesEntry(20, 1000, 70, 1), // a jump → stored as offset + 1
				new PmtilesEntry(40, 0, 33, 0),    // a leaf pointer
			};
			Assert.Equal(entries, PmtilesDirectory.Decode(Archive.Directory(entries)));
		}

		// ── the cover ──

		[Fact]
		public void Lower_48_to_level_6_covers_pmtiles_own_count()
		{
			var cover = BasemapTileCover.Build(new[] { BasemapAreas.Lower48 }, 6);
			Assert.Equal(128UL, cover.Aggregate(0UL, (s, r) => s + r.Count)); // pmtiles extract: "Region tiles 128"
		}

		[Fact]
		public void The_cover_is_exactly_the_tiles_the_box_touches()
		{
			var area = BasemapAreas.Hawaii;
			var cover = BasemapTileCover.Build(new[] { area }, 9);
			ulong expected = 0;
			for (var z = 0; z <= 9; z++)
			{
				var (x0, y0, x1, y1) = BasemapTileCover.TileRect(area.Boxes[0], z);
				expected += (ulong)(x1 - x0 + 1) * (y1 - y0 + 1);
				for (var x = x0; x <= x1; x++)
					for (var y = y0; y <= y1; y++)
						Assert.True(BasemapTileCover.Touches(cover, PmtilesTileId.FromZxy(z, x, y), PmtilesTileId.FromZxy(z, x, y) + 1));
			}
			Assert.Equal(expected, cover.Aggregate(0UL, (s, r) => s + r.Count));
		}

		[Fact]
		public void Overlapping_areas_are_counted_once()
		{
			var both = BasemapTileCover.Build(new[] { BasemapAreas.Lower48, BasemapAreas.Canada }, 8);
			var sum = new[] { BasemapAreas.Lower48, BasemapAreas.Canada }
				.Sum(a => (double)BasemapTileCover.Build(new[] { a }, 8).Aggregate(0UL, (s, r) => s + r.Count));
			Assert.True(both.Aggregate(0UL, (s, r) => s + r.Count) < sum);
			for (var i = 1; i < both.Count; i++) Assert.True(both[i - 1].End < both[i].First); // sorted, apart
		}

		[Fact]
		public void Intersect_clips_a_run_to_the_cover()
		{
			var cover = new[] { new TileIdRange(10, 20), new TileIdRange(30, 40) };
			Assert.Equal(new[] { new TileIdRange(15, 20), new TileIdRange(30, 35) },
				BasemapTileCover.Intersect(cover, 15, 35).ToArray());
			Assert.Empty(BasemapTileCover.Intersect(cover, 20, 30));
		}

		// ── the plan ──

		[Fact]
		public async Task A_plan_opens_only_the_leaves_it_needs_and_clips_runs_at_the_area_edge()
		{
			// A z0-z1 "archive": the root points at two leaves; Guam's cover at z1 is ids 0 (z0) and 4 (z1 tile 1,0).
			var cover = BasemapTileCover.Build(new[] { BasemapAreas.Guam }, 1);
			Assert.Equal(new[] { new TileIdRange(0, 1), new TileIdRange(4, 5) }, cover);

			var leafA = Archive.Directory(new[] { new PmtilesEntry(0, 0, 10, 1), new PmtilesEntry(1, 10, 10, 2) });   // ids 0, 1-2
			var leafB = Archive.Directory(new[] { new PmtilesEntry(3, 20, 10, 2) });                                   // ids 3-4 (one run)
			var leaves = leafA.Concat(leafB).ToArray();
			var root = Archive.Directory(new[]
			{
				new PmtilesEntry(0, 0, (uint)leafA.Length, 0),
				new PmtilesEntry(3, (ulong)leafA.Length, (uint)leafB.Length, 0),
			});
			var source = Archive.Build(root, leaves, tileData: new byte[30], maxZoom: 1);

			var plan = await BasemapExtractPlanner.PlanAsync(source, new[] { BasemapAreas.Guam }, 1, CancellationToken.None);

			Assert.Equal(new[] { new PmtilesEntry(0, 0, 10, 1), new PmtilesEntry(4, 20, 10, 1) }, plan.Entries); // run 3-4 → just 4
			Assert.Equal(2, plan.LeafDirectoriesRead);
			Assert.Equal(2UL, plan.RegionTiles);
			Assert.Equal(2, plan.TileContents);
			Assert.Equal(20UL, plan.TileDataBytes);

			// Hawaii's z1 tile is (0,0) = id 1, inside leaf A's span (ids 0-2) only → leaf B is never fetched.
			var hawaii = await BasemapExtractPlanner.PlanAsync(source, new[] { BasemapAreas.Hawaii }, 1, CancellationToken.None);
			Assert.Equal(1, hawaii.LeafDirectoriesRead);
		}

		[Fact]
		public async Task The_world_overview_adds_every_tile_up_to_its_zoom_and_nothing_deeper()
		{
			// z0-z3 = ids 0-84, each its own tile, all in the root. Guam at z3 + an overview to z1 = ids 0-4 (the whole world
			// at z0-1) + Guam's own z2/z3 tiles — no other z2/z3 tile.
			var tiles = Enumerable.Range(0, (int)PmtilesTileId.ZoomBase(4)).Select(i => new PmtilesEntry((ulong)i, (ulong)i * 10, 10, 1)).ToArray();
			var source = Archive.Build(Archive.Directory(tiles), Array.Empty<byte>(), new byte[tiles.Length * 10], maxZoom: 3);
			var guam = new[] { BasemapAreas.Guam };

			var plan = await BasemapExtractPlanner.PlanAsync(source, guam, 3, CancellationToken.None, null, overviewZoom: 1);

			var guamCover = BasemapTileCover.Build(guam, 3);
			var expected = tiles.Where(t => t.TileId < PmtilesTileId.ZoomBase(2) || BasemapTileCover.Touches(guamCover, t.TileId, t.TileId + 1))
				.Select(t => t.TileId);
			Assert.Equal(expected, plan.Entries.Select(e => e.TileId));
			Assert.Equal(5, plan.Entries.Count(e => e.TileId < PmtilesTileId.ZoomBase(2)));
			Assert.True(plan.Entries.Count < tiles.Length);
		}

		[Fact]
		public async Task Leaves_read_at_once_still_give_entries_in_tile_id_order()
		{
			// z0-z2 = ids 0-20, each its own tile: id 0 sits in the root, ids 1-20 in four leaves of five. The source answers
			// LATER leaves FIRST, so a plan that kept results in finishing order would come back shuffled.
			var tiles = Enumerable.Range(0, 21).Select(i => new PmtilesEntry((ulong)i, (ulong)i * 10, 10, 1)).ToArray();
			var leafBytes = new List<byte>();
			var rootEntries = new List<PmtilesEntry> { tiles[0] };
			for (var l = 0; l < 4; l++)
			{
				var leaf = Archive.Directory(tiles.Skip(1 + l * 5).Take(5).ToArray());
				rootEntries.Add(new PmtilesEntry((ulong)(1 + l * 5), (ulong)leafBytes.Count, (uint)leaf.Length, 0));
				leafBytes.AddRange(leaf);
			}
			var areas = new[] { BasemapAreas.Lower48, BasemapAreas.Alaska, BasemapAreas.Guam };
			var source = new LaterFirstSource(Archive.Build(Archive.Directory(rootEntries), leafBytes.ToArray(), new byte[210], maxZoom: 2));

			var plan = await BasemapExtractPlanner.PlanAsync(source, areas, 2, CancellationToken.None);

			var cover = BasemapTileCover.Build(areas, 2);
			var expected = tiles.Where(t => BasemapTileCover.Touches(cover, t.TileId, t.TileId + 1)).ToArray();
			Assert.True(expected.Length > 3);
			Assert.Equal(expected, plan.Entries);
			Assert.True(source.MostAtOnce > 1, "the leaves should be read together");
		}

		[Fact]
		public void The_newest_build_is_picked_from_the_list()
		{
			const string json = "[{\"key\":\"20261019.pmtiles\"},{\"key\":\"20261020.pmtiles\"},{\"key\":\"20261018.pmtiles\"},{\"key\":\"notes.txt\"}]";
			Assert.Equal(new Uri("https://build.protomaps.com/20261020.pmtiles"), ProtomapsBuilds.LatestFromList(json));
		}

		/// <summary>Builds tiny uncompressed PMTiles archives in memory.</summary>
		private static class Archive
		{
			public static byte[] Header(ulong rootOffset, ulong rootLength, ulong leafOffset, ulong leafLength,
				ulong dataOffset, ulong dataLength, byte maxZoom)
			{
				var b = new byte[PmtilesHeader.Length];
				"PMTiles"u8.CopyTo(b);
				b[7] = 3;
				void U64(int at, ulong v) => BitConverter.GetBytes(v).CopyTo(b, at);
				U64(8, rootOffset); U64(16, rootLength);
				U64(24, rootOffset + rootLength); U64(32, 0);
				U64(40, leafOffset); U64(48, leafLength);
				U64(56, dataOffset); U64(64, dataLength);
				b[96] = 1;
				b[97] = (byte)PmtilesCompression.None;
				b[98] = (byte)PmtilesCompression.None;
				b[99] = 1;
				b[101] = maxZoom;
				return b;
			}

			public static byte[] Directory(IReadOnlyList<PmtilesEntry> entries)
			{
				var o = new List<byte>();
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

			public static IPmtilesSource Build(byte[] root, byte[] leaves, byte[] tileData, byte maxZoom)
			{
				var rootOffset = (ulong)PmtilesHeader.Length;
				var leafOffset = rootOffset + (ulong)root.Length;
				var dataOffset = leafOffset + (ulong)leaves.Length;
				var bytes = Header(rootOffset, (ulong)root.Length, leafOffset, (ulong)leaves.Length, dataOffset,
					(ulong)tileData.Length, maxZoom).Concat(root).Concat(leaves).Concat(tileData).ToArray();
				return new MemorySource(bytes);
			}

			private static void Varint(List<byte> o, ulong v)
			{
				while (v >= 0x80) { o.Add((byte)(v | 0x80)); v >>= 7; }
				o.Add((byte)v);
			}
		}

		private sealed class MemorySource : IPmtilesSource
		{
			private readonly byte[] _bytes;
			public MemorySource(byte[] bytes) => _bytes = bytes;

			public Task<byte[]> ReadAsync(ulong offset, int length, CancellationToken ct)
			{
				var start = (int)Math.Min(offset, (ulong)_bytes.Length);
				var n = Math.Min(length, _bytes.Length - start);
				return Task.FromResult(_bytes.AsSpan(start, n).ToArray());
			}
		}

		/// <summary>Holds every read after the first (the header) and answers the furthest-in-file first.</summary>
		private sealed class LaterFirstSource : IPmtilesSource
		{
			private readonly IPmtilesSource _inner;
			private int _now;
			public LaterFirstSource(IPmtilesSource inner) => _inner = inner;
			public int MostAtOnce { get; private set; }

			public async Task<byte[]> ReadAsync(ulong offset, int length, CancellationToken ct)
			{
				if (offset == 0) return await _inner.ReadAsync(offset, length, ct);
				var n = Interlocked.Increment(ref _now);
				lock (this) MostAtOnce = Math.Max(MostAtOnce, n);
				await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, 400 - (int)offset)), ct);
				Interlocked.Decrement(ref _now);
				return await _inner.ReadAsync(offset, length, ct);
			}
		}
	}
}
