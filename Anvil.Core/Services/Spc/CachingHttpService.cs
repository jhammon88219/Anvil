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

		/// <summary>Atomically writes <paramref name="content"/> to <paramref name="path"/> via a temp file +
		/// move, so a partial/failed write never blanks the last-known-good cache.</summary>
		protected static async Task AtomicWriteAsync(string path, string content, CancellationToken ct = default)
		{
			var temp = path + ".tmp";
			await File.WriteAllTextAsync(temp, content, ct);
			File.Move(temp, path, overwrite: true);
		}

		/// <summary>Atomically writes to <paramref name="path"/> via a temp file + move, letting the caller
		/// stream the bytes into the temp file (a response copy, a <c>Utf8JsonWriter</c>, …). The stream is
		/// flushed/closed before the move.</summary>
		protected static async Task AtomicWriteAsync(string path, Func<Stream, Task> writeBody, CancellationToken ct = default)
		{
			var temp = path + ".tmp";
			await using (var stream = File.Create(temp))
			{
				await writeBody(stream);
			}
			File.Move(temp, path, overwrite: true);
		}
	}
}
