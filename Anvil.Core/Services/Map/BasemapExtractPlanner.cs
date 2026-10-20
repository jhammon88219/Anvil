using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// What an offline map download WILL fetch, worked out from the planet build's own index before a single tile moves:
	/// the entries to copy (offsets into the source's tile data), and the counts and bytes that size the download.
	/// </summary>
	/// <param name="Source">The source archive's header (its tile data section is where <see cref="Entries"/> point).</param>
	/// <param name="Entries">The new map's tile entries, by tile id — each a run of consecutive picked ids sharing one
	/// piece of source tile data. Comparable to <c>pmtiles extract</c>'s "result tile entries".</param>
	/// <param name="RegionTiles">Tiles the picked areas cover at zooms 0…max, whether the source has them or not
	/// (<c>pmtiles extract</c>'s "Region tiles").</param>
	/// <param name="LeafDirectoriesRead">Leaf directories fetched to find the entries (its "fetching N dirs").</param>
	/// <param name="TileContents">Distinct pieces of tile data (shared ocean/land tiles are stored once).</param>
	/// <param name="TileDataBytes">Their total size — the bulk of the download and of the finished file.</param>
	public sealed record BasemapExtractPlan(PmtilesHeader Source, IReadOnlyList<PmtilesEntry> Entries,
		ulong RegionTiles, int LeafDirectoriesRead, int TileContents, ulong TileDataBytes)
	{
		/// <summary>Tiles the new map addresses (run lengths summed).</summary>
		public ulong AddressedTiles => Entries.Aggregate(0UL, (sum, e) => sum + e.RunLength);
	}

	/// <summary>
	/// Plans an offline map: covers the picked areas (<see cref="BasemapTileCover"/>), then walks the source archive's
	/// directories over range reads — opening ONLY the leaf directories whose id span touches the cover — and keeps the
	/// parts of each run that fall inside it. Mirrors go-pmtiles <c>extract</c>'s planning half, so its dry-run numbers are
	/// the check (<c>BasemapExtractPlannerTests</c>, and live: Lower 48 box at level 13 on the 2026-10-20 build = 1,446,253
	/// region tiles, 1,025,159 entries, 39 leaf directories).
	/// </summary>
	public static class BasemapExtractPlanner
	{
		/// <summary>Standard detail's top zoom (≈ city streets).</summary>
		public const int StandardMaxZoom = 13;

		/// <summary>Full detail's top zoom — every street and building; Protomaps' basemap stops here.</summary>
		public const int FullMaxZoom = 15;

		/// <summary>Leaf directory reads in flight at once (each is one small range read).</summary>
		public const int LeafReadsAtOnce = 12;

		public static async Task<BasemapExtractPlan> PlanAsync(IPmtilesSource source, IEnumerable<BasemapArea> areas,
			int maxZoom, CancellationToken ct, Action<int, int>? leavesRead = null)
		{
			var first = await source.ReadAsync(0, PmtilesHeader.FirstReadLength, ct).ConfigureAwait(false);
			var header = PmtilesHeader.Parse(first);
			var top = Math.Min(maxZoom, header.MaxZoom);
			var cover = BasemapTileCover.Build(areas, top);

			var rootBytes = Slice(first, header.RootDirectoryOffset, header.RootDirectoryLength);
			var root = PmtilesDirectory.Decode(PmtilesHeader.Decompress(rootBytes, header.InternalCompression));

			// Leaf directories are read SEVERAL AT ONCE (2026-10-20): one after another, the ~50-100 an all-areas map
			// needs took 10-20 s of round trips. The gate covers only the network read, never a walk, so a nested leaf
			// can't wait on its own parent's slot.
			using var gate = new SemaphoreSlim(LeafReadsAtOnce);
			var leaves = 0;
			var known = 0; // leaves found so far (the total grows if a leaf points at more leaves)
			var tally = new object(); // reports go out one at a time, in order, though reads finish on many threads
			var entries = await WalkAsync(root, ulong.MaxValue).ConfigureAwait(false);

			// Shared tile data (the same ocean tile under thousands of ids) is fetched and stored once.
			var contents = new Dictionary<ulong, uint>();
			foreach (var e in entries) contents.TryAdd(e.Offset, e.Length);
			var bytes = contents.Values.Aggregate(0UL, (sum, l) => sum + l);
			var regionTiles = cover.Aggregate(0UL, (sum, r) => sum + r.Count);
			return new BasemapExtractPlan(header, entries, regionTiles, leaves, contents.Count, bytes);

			// A directory's entries in TILE-ID ORDER: its own runs and each leaf's result kept in directory order, whatever
			// order the leaf reads finish in.
			async Task<List<PmtilesEntry>> WalkAsync(IReadOnlyList<PmtilesEntry> directory, ulong directoryEnd)
			{
				var pieces = new List<Task<List<PmtilesEntry>>>();
				var runs = new List<PmtilesEntry>();
				for (var i = 0; i < directory.Count; i++)
				{
					var e = directory[i];
					if (e.IsLeaf)
					{
						// A leaf holds ids from its own up to the next entry's (or its parent's end).
						var end = i + 1 < directory.Count ? directory[i + 1].TileId : directoryEnd;
						if (!BasemapTileCover.Touches(cover, e.TileId, end)) continue;
						pieces.Add(Task.FromResult(runs));
						runs = new List<PmtilesEntry>();
						lock (tally) known++;
						pieces.Add(ReadLeafAsync(e, end));
					}
					else
					{
						// Keep each stretch of the run that the cover holds — a run can straddle the edge of an area.
						foreach (var part in BasemapTileCover.Intersect(cover, e.TileId, e.TileId + e.RunLength))
						{
							runs.Add(new PmtilesEntry(part.First, e.Offset, e.Length, checked((uint)part.Count)));
						}
					}
				}
				if (pieces.Count == 0) return runs;
				pieces.Add(Task.FromResult(runs));
				return (await Task.WhenAll(pieces).ConfigureAwait(false)).SelectMany(p => p).ToList();
			}

			async Task<List<PmtilesEntry>> ReadLeafAsync(PmtilesEntry leaf, ulong end)
			{
				byte[] raw;
				await gate.WaitAsync(ct).ConfigureAwait(false);
				try
				{
					raw = await source.ReadAsync(header.LeafDirectoriesOffset + leaf.Offset, checked((int)leaf.Length), ct).ConfigureAwait(false);
				}
				finally { gate.Release(); }
				lock (tally)
				{
					leaves++;
					leavesRead?.Invoke(leaves, known);
				}
				return await WalkAsync(PmtilesDirectory.Decode(PmtilesHeader.Decompress(raw, header.InternalCompression)), end).ConfigureAwait(false);
			}
		}

		private static byte[] Slice(byte[] buffer, ulong offset, ulong length)
		{
			if (offset + length > (ulong)buffer.Length) throw new System.IO.InvalidDataException("The root directory isn't in the first read.");
			return buffer.AsSpan(checked((int)offset), checked((int)length)).ToArray();
		}
	}
}
