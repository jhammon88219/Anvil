using System;
using System.IO;
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
		protected CachingHttpService(string cacheSubfolder, string userAgent = "Anvil/1.0")
		{
			CacheDirectory = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Anvil", cacheSubfolder);
			Directory.CreateDirectory(CacheDirectory);

			Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			Http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
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
