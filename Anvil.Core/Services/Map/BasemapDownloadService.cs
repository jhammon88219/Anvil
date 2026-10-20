using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Microsoft.Extensions.Logging;

namespace Anvil.Services
{
	/// <summary>Where a download is: finding the newest build, reading its index, moving tile data, writing the end.</summary>
	public enum BasemapDownloadPhase { FindingBuild, Planning, Downloading, Finishing }

	/// <summary>A download's progress. Bytes = tile data (the bulk of the file); the total is known once planned.</summary>
	public readonly record struct BasemapDownloadProgress(BasemapDownloadPhase Phase, ulong BytesDone, ulong BytesTotal);

	/// <summary>The drive can't hold the map. Thrown BEFORE any tile data moves.</summary>
	public sealed class BasemapNotEnoughSpaceException : IOException
	{
		public BasemapNotEnoughSpaceException(ulong neededBytes, ulong freeBytes)
			: base($"Needs {neededBytes} bytes, {freeBytes} free.")
		{
			NeededBytes = neededBytes;
			FreeBytes = freeBytes;
		}

		public ulong NeededBytes { get; }
		public ulong FreeBytes { get; }
	}

	/// <summary>Makes offline maps: the picked areas at Standard or Full detail, extracted from Protomaps' newest planet build.</summary>
	public interface IBasemapDownloadService
	{
		/// <summary>
		/// Download a map into <paramref name="folder"/> and return its path. An unfinished download of the SAME options in
		/// that folder is RESUMED (same name); otherwise the name is the next free one (<see cref="BasemapFileNames"/>).
		/// Cancelling or failing keeps what arrived — the next call with the same options picks up there.
		/// </summary>
		Task<string> DownloadAsync(string folder, IReadOnlyList<BasemapArea> areas, bool fullDetail,
			IProgress<BasemapDownloadProgress>? progress, CancellationToken ct);

		/// <summary>The name an unfinished download of these options in <paramref name="folder"/> will finish as, or null.</summary>
		string? UnfinishedFileName(string folder, IReadOnlyList<BasemapArea> areas, bool fullDetail);
	}

	/// <summary>
	/// The offline map download (step 4 of the map setup). <see cref="BasemapExtractPlanner"/> works out every tile entry
	/// first, so the WHOLE file layout — header, directories, metadata, where each tile's bytes go — is known before any
	/// tile data moves. The file is written in that final layout from the start, and tile data is APPENDED in source order,
	/// so it never needs a second copy at the end, and resuming = truncate to the last finished chunk and carry on.
	/// </summary>
	/// <remarks>
	/// ON DISK, beside the finished name while it runs: <c>{name}.part</c> (the file being written) and <c>{name}.part.json</c>
	/// (<see cref="BasemapDownloadJob"/>: the build, the options, chunks done). Finishing deletes the job and renames the part.
	/// Chunks: tile contents sorted by source offset, joined while the gap is ≤ <see cref="MaxGapBytes"/> and the request
	/// stays ≤ the chunk size; <see cref="Parallel"/> in flight, written strictly in order. Each chunk retries twice.
	/// ⚠️ <c>ConfigureAwait(false)</c> throughout — this runs off the UI thread; progress reaches the UI through IProgress.
	/// </remarks>
	public sealed class BasemapDownloadService : IBasemapDownloadService
	{
		public const string PartExtension = ".part";
		public const string JobExtension = ".part.json";
		public const int MaxGapBytes = 64 * 1024;
		// Each range read from build.protomaps.com runs ~12-30 MB/s on its own; 8 at once measured ~83 MB/s combined
		// (2026-10-20, 1 Gb fiber), so a fast line needs many in flight. 12 × 16 MB chunks ≈ 200 MB held at most.
		public const int Parallel = 12;
		private const int DefaultChunkBytes = 16 * 1024 * 1024;

		/// <summary>The pause before a chunk's retry, times the attempt (3 s, 6 s). Tests shorten it.</summary>
		internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(3);

		private readonly ILogger<BasemapDownloadService> _logger;
		private readonly Func<Uri, IPmtilesSource> _open;
		private readonly Func<CancellationToken, Task<Uri>> _latestBuild;
		private readonly int _chunkBytes;
		private readonly Func<string, ulong?> _freeBytes;

		public BasemapDownloadService(ILogger<BasemapDownloadService> logger)
			: this(logger, CreateHttp(), DefaultChunkBytes)
		{
		}

		private BasemapDownloadService(ILogger<BasemapDownloadService> logger, HttpClient http, int chunkBytes)
			: this(logger, url => new HttpPmtilesSource(http, url), ct => ProtomapsBuilds.LatestAsync(http, ct), chunkBytes, DriveFreeBytes)
		{
		}

		/// <summary>TESTS: an in-memory archive, a small chunk size, a fake free-space figure.</summary>
		internal BasemapDownloadService(ILogger<BasemapDownloadService> logger, Func<Uri, IPmtilesSource> open,
			Func<CancellationToken, Task<Uri>> latestBuild, int chunkBytes, Func<string, ulong?> freeBytes)
		{
			_logger = logger;
			_open = open;
			_latestBuild = latestBuild;
			_chunkBytes = chunkBytes;
			_freeBytes = freeBytes;
		}

		private static HttpClient CreateHttp()
		{
			var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) }; // per request: a 16 MB chunk on a slow line
			http.DefaultRequestHeaders.UserAgent.ParseAdd("Anvil (offline map download)");
			return http;
		}

		/// <summary>Every name taken in <paramref name="folder"/> for <see cref="BasemapFileNames.NextFileName"/>: finished
		/// maps AND unfinished ones (their <c>.part</c> stripped), so a new download never collides with one in progress.</summary>
		public static List<string> TakenFileNames(string folder)
		{
			try
			{
				if (!Directory.Exists(folder)) return new List<string>();
				return Directory.EnumerateFiles(folder, BasemapFileNames.Prefix + "*")
					.Select(Path.GetFileName).OfType<string>()
					.Select(n => n.EndsWith(PartExtension, StringComparison.OrdinalIgnoreCase) ? n[..^PartExtension.Length] : n)
					.Where(n => n.EndsWith(BasemapFileNames.Extension, StringComparison.OrdinalIgnoreCase))
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToList();
			}
			catch (IOException) { return new List<string>(); }
			catch (UnauthorizedAccessException) { return new List<string>(); }
		}

		public string? UnfinishedFileName(string folder, IReadOnlyList<BasemapArea> areas, bool fullDetail) =>
			FindJob(folder, areas, fullDetail)?.FileName;

		public async Task<string> DownloadAsync(string folder, IReadOnlyList<BasemapArea> areas, bool fullDetail,
			IProgress<BasemapDownloadProgress>? progress, CancellationToken ct)
		{
			Directory.CreateDirectory(folder);
			var job = FindJob(folder, areas, fullDetail);
			try
			{
				return await RunAsync(folder, areas, fullDetail, job, progress, ct).ConfigureAwait(false);
			}
			catch (HttpRequestException ex) when (job is not null && ex.StatusCode is System.Net.HttpStatusCode.NotFound
				or System.Net.HttpStatusCode.Forbidden)
			{
				// The build an unfinished download started from is gone (Protomaps keeps a few days) — start over on the newest.
				_logger.LogInformation("Build {Build} is gone; restarting {File} on the newest", job.Build, job.FileName);
				Discard(folder, job.FileName);
				return await RunAsync(folder, areas, fullDetail, null, progress, ct).ConfigureAwait(false);
			}
		}

		private async Task<string> RunAsync(string folder, IReadOnlyList<BasemapArea> areas, bool fullDetail,
			BasemapDownloadJob? job, IProgress<BasemapDownloadProgress>? progress, CancellationToken ct)
		{
			var fileName = job?.FileName ?? BasemapFileNames.NextFileName(fullDetail, areas, TakenFileNames(folder));
			var finalPath = Path.Combine(folder, fileName);
			var partPath = finalPath + PartExtension;
			var jobPath = finalPath + JobExtension;

			progress?.Report(new BasemapDownloadProgress(BasemapDownloadPhase.FindingBuild, 0, 0));
			var build = job is not null ? new Uri(job.Build) : await _latestBuild(ct).ConfigureAwait(false);
			var source = _open(build);

			progress?.Report(new BasemapDownloadProgress(BasemapDownloadPhase.Planning, 0, 0));
			var maxZoom = fullDetail ? BasemapExtractPlanner.FullMaxZoom : BasemapExtractPlanner.StandardMaxZoom;
			var plan = await BasemapExtractPlanner.PlanAsync(source, areas, maxZoom, ct).ConfigureAwait(false);
			var layout = await LayoutAsync(source, plan, areas, ct).ConfigureAwait(false);

			// A job that doesn't fit this layout (it shouldn't — same build, same options) starts over.
			if (job is not null && (job.DataBytes != layout.DataBytes || job.ChunksDone > layout.Chunks.Count))
			{
				_logger.LogWarning("Unfinished {File} doesn't match its build's plan; starting it over", fileName);
				job = null;
			}
			job ??= new BasemapDownloadJob(BasemapDownloadJob.CurrentVersion, build.ToString(),
				areas.Select(a => a.Code).ToArray(), fullDetail, fileName, layout.DataBytes, 0, 0);

			var needed = (ulong)layout.Prefix.Length + layout.DataBytes - job.BytesDone;
			if (_freeBytes(folder) is { } free && free < needed) throw new BasemapNotEnoughSpaceException(needed, free);

			_logger.LogInformation("Offline map {File}: {Entries} entries, {Contents} contents, {Bytes} bytes in {Chunks} chunks from {Build}; resuming at chunk {Done}",
				fileName, plan.Entries.Count, plan.TileContents, layout.DataBytes, layout.Chunks.Count, build, job.ChunksDone);

			await using (var file = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 20, useAsync: true))
			{
				// The layout is deterministic, so rewriting the prefix on a resume writes the same bytes.
				file.SetLength((long)((ulong)layout.Prefix.Length + job.BytesDone));
				file.Position = 0;
				await file.WriteAsync(layout.Prefix, ct).ConfigureAwait(false);
				file.Position = file.Length;
				SaveJob(jobPath, job);
				progress?.Report(new BasemapDownloadProgress(BasemapDownloadPhase.Downloading, job.BytesDone, layout.DataBytes));

				job = await CopyChunksAsync(source, plan.Source.TileDataOffset, layout, job, file, jobPath, progress, ct).ConfigureAwait(false);

				progress?.Report(new BasemapDownloadProgress(BasemapDownloadPhase.Finishing, job.BytesDone, layout.DataBytes));
				await file.FlushAsync(ct).ConfigureAwait(false);
				var expected = (long)((ulong)layout.Prefix.Length + layout.DataBytes);
				if (file.Length != expected) throw new IOException($"The finished map is {file.Length} bytes, expected {expected}.");
			}

			File.Move(partPath, finalPath); // the name was free (or ours) when picked; a clash here is a real error
			File.Delete(jobPath);
			_logger.LogInformation("Offline map {File} finished", fileName);
			return finalPath;
		}

		// ── layout ─────────────────────────────────────────────────────────────────────────────────────────────

		/// <summary>One range read of the source's tile data and the contents inside it (source offset → bytes to keep).</summary>
		private readonly record struct Chunk(ulong SourceStart, ulong SourceEnd, int FirstContent, int ContentCount, ulong NewBytes);

		/// <summary>The finished file's prefix (header + root + metadata + leaves), its tile data size, and the chunks to fetch.</summary>
		private sealed record Layout(byte[] Prefix, ulong DataBytes, IReadOnlyList<(ulong Offset, uint Length)> Contents, IReadOnlyList<Chunk> Chunks);

		private async Task<Layout> LayoutAsync(IPmtilesSource source, BasemapExtractPlan plan, IReadOnlyList<BasemapArea> areas, CancellationToken ct)
		{
			// Each distinct piece of source tile data once, in SOURCE order — a clustered source keeps the new file clustered.
			var contents = plan.Entries.Select(e => (e.Offset, e.Length)).Distinct().OrderBy(c => c.Offset).ToList();
			var newOffset = new Dictionary<ulong, ulong>(contents.Count);
			ulong data = 0;
			foreach (var (offset, length) in contents)
			{
				newOffset[offset] = data;
				data += length;
			}
			var entries = plan.Entries.Select(e => e with { Offset = newOffset[e.Offset] }).ToArray();
			var (root, leaves) = PmtilesWriter.BuildDirectories(entries);

			var src = plan.Source;
			var rawMetadata = await source.ReadAsync(src.MetadataOffset, checked((int)src.MetadataLength), ct).ConfigureAwait(false);
			var metadata = PmtilesWriter.Gzip(PmtilesHeader.Decompress(rawMetadata, src.InternalCompression));

			var boxes = areas.SelectMany(a => a.Boxes).ToList();
			double w = boxes.Min(b => b.West), s = boxes.Min(b => b.South), e = boxes.Max(b => b.East), n = boxes.Max(b => b.North);
			static int E7(double v) => (int)Math.Round(v * 1e7);
			var rootOffset = (ulong)PmtilesHeader.Length;
			var metadataOffset = rootOffset + (ulong)root.Length;
			var leafOffset = metadataOffset + (ulong)metadata.Length;
			var dataOffset = leafOffset + (ulong)leaves.Length;
			var header = new PmtilesHeader(
				rootOffset, (ulong)root.Length, metadataOffset, (ulong)metadata.Length, leafOffset, (ulong)leaves.Length,
				dataOffset, data, plan.AddressedTiles, (ulong)entries.Length, (ulong)contents.Count,
				src.Clustered, PmtilesCompression.Gzip, src.TileCompression, src.TileType,
				0, (byte)entries.Max(x => ZoomOf(x.TileId)), E7(w), E7(s), E7(e), E7(n),
				4, E7((w + e) / 2), E7((s + n) / 2));

			var prefix = PmtilesWriter.HeaderBytes(header).Concat(root).Concat(metadata).Concat(leaves).ToArray();
			return new Layout(prefix, data, contents, MakeChunks(contents));
		}

		private List<Chunk> MakeChunks(IReadOnlyList<(ulong Offset, uint Length)> contents)
		{
			var chunks = new List<Chunk>();
			var i = 0;
			while (i < contents.Count)
			{
				var start = contents[i].Offset;
				var end = start + contents[i].Length;
				ulong bytes = contents[i].Length;
				var j = i + 1;
				while (j < contents.Count && contents[j].Offset - end <= MaxGapBytes
					&& contents[j].Offset + contents[j].Length - start <= (ulong)_chunkBytes)
				{
					end = Math.Max(end, contents[j].Offset + contents[j].Length);
					bytes += contents[j].Length;
					j++;
				}
				chunks.Add(new Chunk(start, end, i, j - i, bytes));
				i = j;
			}
			return chunks;
		}

		private static int ZoomOf(ulong tileId)
		{
			var z = 0;
			while (z < PmtilesTileId.MaxZoom && PmtilesTileId.ZoomBase(z + 1) <= tileId) z++;
			return z;
		}

		// ── copying ────────────────────────────────────────────────────────────────────────────────────────────

		private async Task<BasemapDownloadJob> CopyChunksAsync(IPmtilesSource source, ulong tileDataOffset, Layout layout,
			BasemapDownloadJob job, FileStream file, string jobPath, IProgress<BasemapDownloadProgress>? progress, CancellationToken ct)
		{
			using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
			var inFlight = new Queue<Task<byte[]>>();
			var next = job.ChunksDone;
			try
			{
				for (var done = job.ChunksDone; done < layout.Chunks.Count; done++)
				{
					while (next < layout.Chunks.Count && inFlight.Count < Parallel)
					{
						var c = layout.Chunks[next++];
						inFlight.Enqueue(FetchAsync(source, tileDataOffset + c.SourceStart, checked((int)(c.SourceEnd - c.SourceStart)), stop.Token));
					}

					var bytes = await inFlight.Dequeue().ConfigureAwait(false);
					var chunk = layout.Chunks[done];
					for (var k = chunk.FirstContent; k < chunk.FirstContent + chunk.ContentCount; k++)
					{
						var (offset, length) = layout.Contents[k];
						await file.WriteAsync(bytes.AsMemory(checked((int)(offset - chunk.SourceStart)), (int)length), ct).ConfigureAwait(false);
					}
					await file.FlushAsync(ct).ConfigureAwait(false);

					job = job with { ChunksDone = done + 1, BytesDone = job.BytesDone + chunk.NewBytes };
					SaveJob(jobPath, job);
					progress?.Report(new BasemapDownloadProgress(BasemapDownloadPhase.Downloading, job.BytesDone, layout.DataBytes));
				}
				return job;
			}
			finally
			{
				stop.Cancel(); // a failure or a cancel: stop what's still in flight, and observe it
				foreach (var t in inFlight)
				{
					try { await t.ConfigureAwait(false); } catch (Exception) { /* already failing or cancelled */ }
				}
			}
		}

		private async Task<byte[]> FetchAsync(IPmtilesSource source, ulong offset, int length, CancellationToken ct)
		{
			for (var attempt = 1; ; attempt++)
			{
				try
				{
					var bytes = await source.ReadAsync(offset, length, ct).ConfigureAwait(false);
					if (bytes.Length != length) throw new IOException($"Got {bytes.Length} of {length} bytes.");
					return bytes;
				}
				catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested && ex is HttpRequestException or IOException or TaskCanceledException)
				{
					_logger.LogWarning("Map chunk at {Offset} failed (attempt {Attempt}): {Message}", offset, attempt, ex.Message);
					await Task.Delay(RetryDelay * attempt, ct).ConfigureAwait(false);
				}
			}
		}

		// ── the job file ───────────────────────────────────────────────────────────────────────────────────────

		private BasemapDownloadJob? FindJob(string folder, IReadOnlyList<BasemapArea> areas, bool fullDetail)
		{
			var codes = BasemapAreas.All.Where(areas.Contains).Select(a => a.Code).ToArray();
			try
			{
				if (!Directory.Exists(folder)) return null;
				foreach (var path in Directory.EnumerateFiles(folder, BasemapFileNames.Prefix + "*" + JobExtension))
				{
					var job = LoadJob(path);
					if (job is null || job.FullDetail != fullDetail || !job.Areas.SequenceEqual(codes)) continue;
					if (File.Exists(Path.Combine(folder, job.FileName + PartExtension))) return job;
				}
			}
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
			return null;
		}

		private BasemapDownloadJob? LoadJob(string path)
		{
			try
			{
				var job = JsonSerializer.Deserialize<BasemapDownloadJob>(File.ReadAllText(path));
				return job?.Version == BasemapDownloadJob.CurrentVersion ? job : null;
			}
			catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
			{
				_logger.LogWarning("Unreadable download record {Path}: {Message}", path, ex.Message);
				return null;
			}
		}

		private static void SaveJob(string path, BasemapDownloadJob job)
		{
			var temp = path + ".tmp";
			File.WriteAllText(temp, JsonSerializer.Serialize(job));
			File.Move(temp, path, overwrite: true);
		}

		private static void Discard(string folder, string fileName)
		{
			var final = Path.Combine(folder, fileName);
			File.Delete(final + PartExtension);
			File.Delete(final + JobExtension);
		}

		private static ulong? DriveFreeBytes(string folder)
		{
			try
			{
				var root = Path.GetPathRoot(Path.GetFullPath(folder));
				return string.IsNullOrEmpty(root) ? null : (ulong)new DriveInfo(root).AvailableFreeSpace;
			}
			catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
			{
				return null;
			}
		}
	}

	/// <summary>
	/// An unfinished download's record (<c>{name}.part.json</c>): enough to re-plan the SAME map from the SAME build and
	/// carry on after <see cref="ChunksDone"/>. Bump <see cref="CurrentVersion"/> if the layout rules change — an old record
	/// is then ignored and its download starts over.
	/// </summary>
	public sealed record BasemapDownloadJob(int Version, string Build, string[] Areas, bool FullDetail, string FileName,
		ulong DataBytes, int ChunksDone, ulong BytesDone)
	{
		public const int CurrentVersion = 1;
	}
}
