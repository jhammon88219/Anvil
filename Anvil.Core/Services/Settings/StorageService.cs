using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Microsoft.Extensions.Logging;

namespace Anvil.Services
{
	/// <summary>
	/// Settings → Storage's file work (see <see cref="IStorageService"/>). Everything runs on the thread pool and is
	/// best effort: a file in use (today's log, the diagnostics run being written) is skipped, never an error.
	/// </summary>
	/// <remarks>
	/// ⚠️ WEATHER DATA = every <see cref="CachingHttpService"/> folder (its <see cref="CachingHttpService.KnownCacheDirectories"/>
	/// registry), so a new feed service is counted, cleared and swept with no edit here. Those services are DI
	/// singletons injected into MapViewModel, so they are all registered before anything here runs.
	/// ⚠️ Anvil's ROOT holds the user's saved events (<c>SavedEventLibrary</c>) — nothing here ever deletes from the
	/// root itself, only from the named sub-folders.
	/// </remarks>
	public sealed class StorageService : IStorageService
	{
		/// <summary><c>%LocalAppData%\Anvil</c> — every Anvil folder lives under it.</summary>
		public static string AnvilRoot { get; } = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anvil");

		/// <summary>Serilog's folder. ⚠️ App.ConfigureLogging writes here — read from this, never re-typed.</summary>
		public static string LogsDirectory { get; } = Path.Combine(AnvilRoot, "Logs");

		/// <summary>The usage records (site usage, loop-load times, live-poll timing).</summary>
		public static string UsageDirectory { get; } = Path.Combine(AnvilRoot, "Usage");

		/// <summary>A file this new is never size-trimmed: it may be what's on screen or being written.</summary>
		internal static readonly TimeSpan ProtectNewerThan = TimeSpan.FromMinutes(30);

		private readonly ILevel2RadarService _radar;
		private readonly ILogger<StorageService>? _logger;

		public StorageService(ILevel2RadarService radar, ILogger<StorageService>? logger = null)
		{
			_radar = radar;
			_logger = logger;
		}

		private string DiagnosticsDirectory => Path.Combine(_radar.CacheDirectory, "Diagnostics");

		public string FolderOf(StorageKind kind) => kind switch
		{
			StorageKind.RadarVolumes => _radar.CacheDirectory,
			StorageKind.RadarDiagnostics => DiagnosticsDirectory,
			StorageKind.Logs => LogsDirectory,
			StorageKind.UsageHistory => UsageDirectory,
			_ => AnvilRoot,
		};

		// The files a kind owns. Weather data skips in-flight .tmp writes (CachingHttpService's own sweep takes the
		// old ones); usage history is the jsonl logs + site-usage.json.
		private IEnumerable<FileInfo> FilesOf(StorageKind kind)
		{
			IEnumerable<string> dirs = kind switch
			{
				StorageKind.RadarDiagnostics => new[] { DiagnosticsDirectory },
				StorageKind.WeatherData => CachingHttpService.KnownCacheDirectories,
				StorageKind.Logs => new[] { LogsDirectory },
				StorageKind.UsageHistory => new[] { UsageDirectory },
				_ => Array.Empty<string>(),
			};
			foreach (var dir in dirs)
			{
				if (!Directory.Exists(dir)) continue;
				IEnumerable<string> paths;
				try { paths = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList(); }
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
				foreach (var p in paths)
				{
					if (kind == StorageKind.WeatherData && p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
					FileInfo fi;
					try { fi = new FileInfo(p); _ = fi.Length; }
					catch { continue; } // vanished mid-scan
					yield return fi;
				}
			}
		}

		public Task<long> MeasureAsync(StorageKind kind, CancellationToken cancellationToken = default) => Task.Run(() =>
		{
			long total = 0;
			foreach (var fi in FilesOf(kind))
			{
				cancellationToken.ThrowIfCancellationRequested();
				try { total += fi.Length; } catch { /* vanished */ }
			}
			return total;
		}, cancellationToken);

		public Task ClearAsync(StorageKind kind, CancellationToken cancellationToken = default) => Task.Run(() =>
		{
			if (kind == StorageKind.RadarVolumes) return;
			int removed = 0, kept = 0;
			foreach (var fi in FilesOf(kind))
			{
				cancellationToken.ThrowIfCancellationRequested();
				// Usage history: the caller clears site-usage.json through SiteUsageStore (it holds the live record).
				if (kind == StorageKind.UsageHistory && fi.Name.Equals("site-usage.json", StringComparison.OrdinalIgnoreCase)) continue;
				try { fi.Delete(); removed++; } catch { kept++; } // in use → stays
			}
			if (kind == StorageKind.RadarDiagnostics) RemoveEmptyFolders(DiagnosticsDirectory);
			_logger?.LogInformation("Storage clear {Kind}: {Removed} file(s) deleted, {Kept} in use", kind, removed, kept);
		}, cancellationToken);

		public Task SweepAsync(StorageKind kind, long maxBytes, TimeSpan maxAge, CancellationToken cancellationToken = default) => Task.Run(() =>
		{
			if (kind is not (StorageKind.WeatherData or StorageKind.Logs)) return;
			var doomed = PlanTrim(FilesOf(kind).Select(f => (f.FullName, f.Length, f.LastWriteTimeUtc)).ToList(),
				maxBytes, maxAge, DateTime.UtcNow);
			int removed = 0; long bytes = 0;
			foreach (var (path, length) in doomed)
			{
				cancellationToken.ThrowIfCancellationRequested();
				try { File.Delete(path); removed++; bytes += length; } catch { /* in use → next sweep */ }
			}
			if (removed > 0)
			{
				_logger?.LogInformation("Storage sweep {Kind}: removed {Removed} file(s) ({MB:F0} MB)", kind, removed, bytes / (double)StorageLimits.MB);
			}
		}, cancellationToken);

		/// <summary>
		/// The files a sweep deletes, in order: every file older than <paramref name="maxAge"/>, then the oldest of the
		/// rest until the survivors fit <paramref name="maxBytes"/>. Files written within <see cref="ProtectNewerThan"/>
		/// are never size-trimmed (they still COUNT toward the total — so a folder whose newest files alone exceed the cap
		/// stays over until they age). Pure, for the tests.
		/// </summary>
		internal static List<(string Path, long Length)> PlanTrim(
			IReadOnlyList<(string Path, long Length, DateTime WrittenUtc)> files, long maxBytes, TimeSpan maxAge, DateTime nowUtc)
		{
			var doomed = new List<(string, long)>();
			var survivors = new List<(string Path, long Length, DateTime WrittenUtc)>();
			foreach (var f in files)
			{
				if (nowUtc - f.WrittenUtc > maxAge) doomed.Add((f.Path, f.Length));
				else survivors.Add(f);
			}
			long total = survivors.Sum(f => f.Length);
			foreach (var f in survivors.OrderBy(f => f.WrittenUtc))
			{
				if (total <= maxBytes) break;
				if (nowUtc - f.WrittenUtc < ProtectNewerThan) continue;
				doomed.Add((f.Path, f.Length));
				total -= f.Length;
			}
			return doomed;
		}

		private static void RemoveEmptyFolders(string root)
		{
			try
			{
				foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
					.OrderByDescending(d => d.Length).ToList())
				{
					try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
				}
			}
			catch { /* best effort */ }
		}
	}
}
