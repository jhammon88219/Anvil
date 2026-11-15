using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Anvil.Services
{
	/// <summary>
	/// Shared scaffold for the SPC/NWS data services that fetch over HTTP and cache GeoJSON on disk under a
	/// per-user folder mapped to a WebView virtual host. Owns the two things every one of them built
	/// identically — a per-user <see cref="CacheDirectory"/> (created up front so the host mapping has a
	/// real folder to point at on first run) and a configured <see cref="Http"/> client — plus the atomic
	/// (temp-then-move) write helpers so a partial/failed write never blanks the last-known-good cache.
	///
	/// The fetch/transform logic stays in each subclass — the feeds differ (conditional GETs, CAP
	/// transforms, CSV parsing, IEM enrichment) — and a subclass may further configure <see cref="Http"/>
	/// (e.g. add a default Accept header) in its constructor body.
	/// </summary>
	public abstract class CachingHttpService
	{
		/// <param name="cacheSubfolder">Folder name under <c>%LocalAppData%\Anvil</c> this service caches into.</param>
		/// <param name="userAgent">User-Agent to send — NOAA/IEM/SPC endpoints reject a blank one.</param>
		/// <param name="stampedLogPattern">
		/// Optional glob for PER-LAUNCH stamped files this service's folder accumulates (e.g.
		/// <c>warnings-health-*.jsonl</c>, one per app run). Those are kept newest-N rather than by age,
		/// because a burst of short sessions leaves hundreds of them well inside any age window.
		/// </param>
		/// <param name="keepStampedNewest">How many of <paramref name="stampedLogPattern"/> to keep.</param>
		protected CachingHttpService(string cacheSubfolder, string userAgent = "Anvil/1.0",
			string? stampedLogPattern = null, int keepStampedNewest = 15)
		{
			CacheDirectory = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Anvil", cacheSubfolder);
			Directory.CreateDirectory(CacheDirectory);
			s_cacheDirectories.TryAdd(CacheDirectory, 0);

			Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			Http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

			SweepCacheOnStartup(stampedLogPattern, keepStampedNewest);
		}
		// ── Startup cache sweep ───────────────────────────────────────────────────────────────────────
		// ⚠️ WHY THIS EXISTS: nothing ever deleted from these folders. TryDeleteTemp's own comment said
		// "the cache sweep will get it" — and there was no sweep. Three things grew without bound:
		//   • per-date immutable caches — one file per replay date ever browsed (past-{date}-d{day}-c{cycle},
		//     reports-v2-{date}, narrative-day{N}), which is unbounded in the number of events you look at;
		//   • per-launch stamped logs — warnings-health-{stamp}.jsonl, ONE PER APP RUN, forever;
		//   • orphaned .tmp files from any write that threw between the temp write and the move.
		// Each file is small; the FILE COUNT is the problem, in a folder that is also a WebView2 virtual host.
		//
		// ⭐ The AGE + SIZE caps over these folders moved to StorageService (Settings → Storage → Weather data:
		// "Keep for" + "Max size", AppSettings.WeatherData*), which finds every folder through the registry below.
		// This sweep keeps only what is per-service: orphaned temps and the stamped per-launch logs.
		// ⚠️ THE LIVE CACHES ARE SAFE BY CONSTRUCTION, not by an exclusion list: today's outlook, the watch
		// and warning snapshots are rewritten in place on every refresh, so their mtime is always fresh and
		// an age sweep cannot reach them (and StorageService never size-trims a file under 30 min old). That
		// means NO filename knowledge lives anywhere — a service can add, rename or restructure its cache files.
		// ⚠️ Best-effort throughout, and OFF THE STARTUP PATH: a failure to tidy must never be visible.
		private static readonly TimeSpan OrphanTempMinAge = TimeSpan.FromHours(1); // never race a live write

		// Every subclass's folder, registered in the constructor — Settings → Storage's "Weather data" is exactly
		// this set, so a new feed service is counted, cleared and swept with no other edit.
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> s_cacheDirectories =
			new(StringComparer.OrdinalIgnoreCase);

		/// <summary>The cache folder of every <see cref="CachingHttpService"/> built so far (= weather data).</summary>
		internal static IReadOnlyCollection<string> KnownCacheDirectories => s_cacheDirectories.Keys.ToList();

		private void SweepCacheOnStartup(string? stampedPattern, int keepStampedNewest)
		{
			_ = Task.Run(() =>
			{
				try
				{
					var now = DateTime.UtcNow;

					// 1. Orphaned temps. Only ones old enough that no in-flight write could still own them —
					//    AtomicWriteAsync's names are unique per write, so a fresh .tmp may be live right now.
					foreach (var temp in Directory.EnumerateFiles(CacheDirectory, "*.tmp"))
					{
						try
						{
							if (now - File.GetLastWriteTimeUtc(temp) > OrphanTempMinAge) File.Delete(temp);
						}
						catch { /* best effort */ }
					}

					// 2. (The age cap is StorageService's now — see above.)

					// 3. Per-launch stamped logs: keep the newest N regardless of age, because they are written
					//    once per run and a burst of short sessions would otherwise leave hundreds inside the
					//    age window. Newest-by-name works because the stamp sorts chronologically.
					if (stampedPattern is not null && keepStampedNewest > 0)
					{
						var stamped = Directory.EnumerateFiles(CacheDirectory, stampedPattern)
							.OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
							.Skip(keepStampedNewest);
						foreach (var old in stamped)
						{
							try { File.Delete(old); } catch { /* best effort */ }
						}
					}
				}
				catch { /* the folder may not exist yet, or be locked — tidying is never worth a throw */ }
			});
		}


		/// <summary>The per-user on-disk cache folder (created in the constructor). MainWindow maps it to the
		/// service's WebView virtual host; this satisfies each <c>ISpc*Service.CacheDirectory</c> contract.</summary>
		public string CacheDirectory { get; }

		/// <summary>The shared HTTP client (30 s timeout, User-Agent set). Subclasses may add their own
		/// default headers (e.g. Accept) in their constructor.</summary>
		protected HttpClient Http { get; }

		// ⚠️ THE TEMP NAME MUST BE UNIQUE PER WRITE, NOT "<path>.tmp".
		// Two writers of the SAME cache file at the same time is normal here, not exceptional: every one of
		// these services has a periodic refresh loop AND event-driven callers, so a mode change landing on
		// top of a refresh has both writing today's file. With a shared temp name the second File.Create
		// threw IOException ("used by another process"), which propagated out through a fire-and-forget
		// caller and left the overlay showing the PREVIOUS day's data (measured 2026-09-04: switching out
		// of PastCast left the replay day's storm-report dots on the map). A per-write name makes the
		// concurrent case harmless — both write their own temp, and the last Move wins with identical
		// content, which is what "atomic" was supposed to mean.
		private static string TempPathFor(string path) =>
			$"{path}.{Environment.ProcessId:x}-{Guid.NewGuid():N}.tmp";

		// Best-effort cleanup so a failed write can't leave the temp behind. Never throws over the real error.
		private static void TryDeleteTemp(string temp)
		{
			try { if (File.Exists(temp)) { File.Delete(temp); } }
			catch { /* the cache sweep will get it */ }
		}

		/// <summary>Atomically writes <paramref name="content"/> to <paramref name="path"/> via a temp file +
		/// move, so a partial/failed write never blanks the last-known-good cache. Safe against a concurrent
		/// write of the same path — see the note on <see cref="TempPathFor"/>.</summary>
		protected static async Task AtomicWriteAsync(string path, string content, CancellationToken ct = default)
		{
			var temp = TempPathFor(path);
			try
			{
				await File.WriteAllTextAsync(temp, content, ct);
				File.Move(temp, path, overwrite: true);
			}
			catch
			{
				TryDeleteTemp(temp);
				throw;
			}
		}

		/// <summary>Atomically writes to <paramref name="path"/> via a temp file + move, letting the caller
		/// stream the bytes into the temp file (a response copy, a <c>Utf8JsonWriter</c>, …). The stream is
		/// flushed/closed before the move.</summary>
		protected static async Task AtomicWriteAsync(string path, Func<Stream, Task> writeBody, CancellationToken ct = default)
		{
			var temp = TempPathFor(path);
			try
			{
				await using (var stream = File.Create(temp))
				{
					await writeBody(stream);
				}
				File.Move(temp, path, overwrite: true);
			}
			catch
			{
				TryDeleteTemp(temp);
				throw;
			}
		}
	}
}
