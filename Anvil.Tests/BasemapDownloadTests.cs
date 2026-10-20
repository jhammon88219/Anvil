using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The offline map download end to end, against a small in-memory "planet" (every tile of zooms 0-4, a few sharing
	/// data): the finished file reads back with exactly the source's tiles for the area; a dropped connection keeps what
	/// arrived and the next try finishes the SAME file byte-for-byte; the same options again keep both; too little space
	/// stops before anything is written.
	/// </summary>
	public sealed class BasemapDownloadTests : IDisposable
	{
		private static readonly Uri Build = new("https://build.example/20261020.pmtiles");
		private static readonly BasemapArea[] Area = { BasemapAreas.Lower48 };
		private readonly string _folder = Path.Combine(Path.GetTempPath(), "anvil-map-test-" + Guid.NewGuid().ToString("N"));

		public void Dispose()
		{
			try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
		}

		[Fact]
		public async Task A_finished_map_holds_exactly_the_areas_tiles_from_the_source()
		{
			var planet = Planet.Make();
			var path = await Service(planet).DownloadAsync(_folder, Area, false, null, CancellationToken.None);

			Assert.Equal("anvil-map-standard-l48.pmtiles", Path.GetFileName(path));
			Assert.Empty(Directory.GetFiles(_folder, "*.part*")); // the part + record are gone once finished

			var wanted = await BasemapExtractPlanner.PlanAsync(planet, Area, 4, CancellationToken.None);
			var written = new MemorySource(File.ReadAllBytes(path));
			var got = await BasemapExtractPlanner.PlanAsync(written, Area, 4, CancellationToken.None);

			Assert.Equal(wanted.Entries.Select(e => (e.TileId, e.RunLength)), got.Entries.Select(e => (e.TileId, e.RunLength)));
			Assert.Equal(wanted.TileContents, got.TileContents);
			for (var i = 0; i < got.Entries.Count; i++)
			{
				Assert.Equal(await TileBytes(planet, wanted.Source, wanted.Entries[i]), await TileBytes(written, got.Source, got.Entries[i]));
			}
			Assert.True(got.Source.Clustered);
			Assert.Equal(PmtilesCompression.Gzip, got.Source.InternalCompression);
			Assert.Equal(got.Entries.Count, (int)got.Source.TileEntriesCount);
		}

		[Fact]
		public async Task A_dropped_download_resumes_into_the_same_file()
		{
			var planet = Planet.Make();
			var clean = await Service(planet).DownloadAsync(Path.Combine(_folder, "clean"), Area, false, null, CancellationToken.None);

			var dropping = new DroppingSource(planet, okReads: 6); // index reads + a few chunks, then the line drops
			await Assert.ThrowsAsync<HttpRequestException>(() =>
				Service(dropping).DownloadAsync(_folder, Area, false, null, CancellationToken.None));

			var svc = Service(planet);
			Assert.Equal("anvil-map-standard-l48.pmtiles", svc.UnfinishedFileName(_folder, Area, false));
			Assert.True(File.Exists(Path.Combine(_folder, "anvil-map-standard-l48.pmtiles.part")));

			var seen = new List<BasemapDownloadProgress>();
			var path = await svc.DownloadAsync(_folder, Area, false, new SyncProgress(seen.Add), CancellationToken.None);

			Assert.Equal("anvil-map-standard-l48.pmtiles", Path.GetFileName(path)); // resumed, not "-2"
			Assert.Equal(File.ReadAllBytes(clean), File.ReadAllBytes(path));
			var firstData = seen.First(p => p.Phase == BasemapDownloadPhase.Downloading);
			Assert.True(firstData.BytesDone > 0, "the second try starts where the first stopped");
			Assert.Null(svc.UnfinishedFileName(_folder, Area, false));
		}

		[Fact]
		public async Task The_same_options_again_are_kept_beside()
		{
			var svc = Service(Planet.Make());
			await svc.DownloadAsync(_folder, Area, false, null, CancellationToken.None);
			var second = await svc.DownloadAsync(_folder, Area, false, null, CancellationToken.None);
			Assert.Equal("anvil-map-standard-l48-2.pmtiles", Path.GetFileName(second));
		}

		[Fact]
		public async Task Too_little_space_stops_before_anything_is_written()
		{
			var svc = new BasemapDownloadService(NullLogger<BasemapDownloadService>.Instance, _ => Planet.Make(),
				_ => Task.FromResult(Build), chunkBytes: 256, freeBytes: _ => 10);
			var ex = await Assert.ThrowsAsync<BasemapNotEnoughSpaceException>(() =>
				svc.DownloadAsync(_folder, Area, false, null, CancellationToken.None));
			Assert.True(ex.NeededBytes > 10);
			Assert.Empty(Directory.GetFiles(_folder));
		}

		[Fact]
		public void Many_entries_split_into_leaves_and_read_back()
		{
			// Irregular gaps and sizes (seeded), like a real map — a regular pattern gzips small enough to fit the root.
			var random = new Random(7);
			var entries = new PmtilesEntry[60000];
			ulong id = 0, offset = 0;
			for (var i = 0; i < entries.Length; i++)
			{
				id += (ulong)random.Next(1, 50);
				var length = (uint)random.Next(20, 5000);
				entries[i] = new PmtilesEntry(id, offset, length, (uint)random.Next(1, 3));
				offset += length + (ulong)(random.Next(4) == 0 ? random.Next(1, 999) : 0);
			}
			var (root, leaves) = PmtilesWriter.BuildDirectories(entries);
			Assert.True(root.Length <= PmtilesWriter.MaxRootLength);
			Assert.NotEmpty(leaves);

			var pointers = PmtilesDirectory.Decode(PmtilesHeader.Decompress(root, PmtilesCompression.Gzip));
			Assert.All(pointers, p => Assert.True(p.IsLeaf));
			var back = pointers.SelectMany(p => PmtilesDirectory.Decode(PmtilesHeader.Decompress(
				leaves.AsSpan((int)p.Offset, (int)p.Length).ToArray(), PmtilesCompression.Gzip))).ToArray();
			Assert.Equal(entries, back);
		}

		[Fact]
		public void Failures_read_in_plain_words_with_what_is_kept()
		{
			Assert.Equal("Your connection dropped at 1.7 GB of 4.4 GB. What's downloaded is kept.",
				Anvil.ViewModels.BasemapSetupViewModel.FailureWords(new HttpRequestException("x"),
					new BasemapDownloadProgress(BasemapDownloadPhase.Downloading, 1_700_000_000, 4_400_000_000)));
			Assert.Equal("Couldn't reach the map service. Check your internet connection, then retry.",
				Anvil.ViewModels.BasemapSetupViewModel.FailureWords(new HttpRequestException("x"), default));
			Assert.StartsWith("There isn't room on this drive: the map needs 4.4 GB and 1.0 GB is free.",
				Anvil.ViewModels.BasemapSetupViewModel.FailureWords(new BasemapNotEnoughSpaceException(4_400_000_000, 1_000_000_000), default));
		}

		private static BasemapDownloadService Service(IPmtilesSource source) =>
			new(NullLogger<BasemapDownloadService>.Instance, _ => source, _ => Task.FromResult(Build), chunkBytes: 256, freeBytes: _ => null) { RetryDelay = TimeSpan.FromMilliseconds(1) };

		private static Task<byte[]> TileBytes(IPmtilesSource s, PmtilesHeader h, PmtilesEntry e) =>
			s.ReadAsync(h.TileDataOffset + e.Offset, (int)e.Length, CancellationToken.None);

		/// <summary>A planet: every tile of zooms 0-4 in id order, each its own bytes, except a run of 5 sharing one.</summary>
		private static class Planet
		{
			public static MemorySource Make()
			{
				var entries = new List<PmtilesEntry>();
				var data = new MemoryStream();
				var maxId = PmtilesTileId.ZoomBase(5);
				for (ulong id = 0; id < maxId; id++)
				{
					if (id is > 100 and <= 104) continue; // ids 100-104 = ONE run sharing tile 100's data
					var bytes = Encoding.ASCII.GetBytes($"tile {id} " + new string('x', (int)(id % 37)));
					entries.Add(new PmtilesEntry(id, (ulong)data.Length, (uint)bytes.Length, id == 100 ? 5u : 1u));
					data.Write(bytes);
				}
				var root = PmtilesWriter.SerializeDirectory(entries);
				var metadata = Encoding.UTF8.GetBytes("{\"name\":\"test planet\"}");
				var rootOffset = (ulong)PmtilesHeader.Length;
				var metaOffset = rootOffset + (ulong)root.Length;
				var dataOffset = metaOffset + (ulong)metadata.Length;
				var header = new PmtilesHeader(rootOffset, (ulong)root.Length, metaOffset, (ulong)metadata.Length, dataOffset, 0,
					dataOffset, (ulong)data.Length, maxId, (ulong)entries.Count, (ulong)entries.Count,
					true, PmtilesCompression.None, PmtilesCompression.Gzip, 1, 0, 4, -1800000000, -850000000, 1800000000, 850000000, 0, 0, 0);
				return new MemorySource(PmtilesWriter.HeaderBytes(header).Concat(root).Concat(metadata).Concat(data.ToArray()).ToArray());
			}
		}

		private sealed class MemorySource : IPmtilesSource
		{
			private readonly byte[] _bytes;
			public MemorySource(byte[] bytes) => _bytes = bytes;

			public Task<byte[]> ReadAsync(ulong offset, int length, CancellationToken ct)
			{
				var start = (int)Math.Min(offset, (ulong)_bytes.Length);
				return Task.FromResult(_bytes.AsSpan(start, Math.Min(length, _bytes.Length - start)).ToArray());
			}
		}

		/// <summary>Serves <c>okReads</c> reads, then every read fails as a dropped connection.</summary>
		private sealed class DroppingSource : IPmtilesSource
		{
			private readonly IPmtilesSource _inner;
			private int _left;
			public DroppingSource(IPmtilesSource inner, int okReads) { _inner = inner; _left = okReads; }

			public Task<byte[]> ReadAsync(ulong offset, int length, CancellationToken ct) =>
				Interlocked.Decrement(ref _left) >= 0
					? _inner.ReadAsync(offset, length, ct)
					: Task.FromException<byte[]>(new HttpRequestException("connection dropped"));
		}

		/// <summary>Progress<T> posts to a context; tests want the reports in order, now.</summary>
		private sealed class SyncProgress : IProgress<BasemapDownloadProgress>
		{
			private readonly Action<BasemapDownloadProgress> _on;
			public SyncProgress(Action<BasemapDownloadProgress> on) => _on = on;
			public void Report(BasemapDownloadProgress value) => _on(value);
		}
	}
}
