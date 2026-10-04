using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="Level2RadarService.WriteCacheFileAsync"/>: two fetches of the SAME volume at once are normal (a replay's
	/// newest volume is a loop frame AND the storm motion's reference). A shared "{file}.tmp" made one of them fail
	/// silently and drop the frame (2011 + 1999 seeding loads, 2026-10-02).
	/// </summary>
	public class RadarCacheWriteTests
	{
		private static string TempDir()
		{
			var dir = Path.Combine(Path.GetTempPath(), $"anvil-cachewrite-{Guid.NewGuid():N}");
			Directory.CreateDirectory(dir);
			return dir;
		}

		[Fact]
		public async Task Many_writers_of_one_file_all_succeed_and_leave_no_temp()
		{
			var dir = TempDir();
			var path = Path.Combine(dir, "KSGF_20110522_225818.V06");
			var data = Enumerable.Range(0, 8).Select(i => Enumerable.Repeat((byte)i, 200_000).ToArray()).ToArray();

			await Task.WhenAll(data.Select(d => Task.Run(() => Level2RadarService.WriteCacheFileAsync(path, d, default))));

			Assert.True(File.Exists(path));
			Assert.Contains(data, d => d.AsSpan().SequenceEqual(File.ReadAllBytes(path))); // one writer's whole file
			Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
		}

		[Fact]
		public async Task Writing_over_a_file_someone_has_open_keeps_theirs_instead_of_failing()
		{
			var dir = TempDir();
			var path = Path.Combine(dir, "KTLX_19990503_235621.V06");
			await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });

			using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) // the page reading it
			{
				await Level2RadarService.WriteCacheFileAsync(path, new byte[] { 9, 9, 9 }, default); // must not throw
			}

			Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
			Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
		}

		// ── Transient fetch failures (Level2RadarService.RetryTransientAsync) ─────────────────────────────────────
		// One connection reset by S3 lost a frame for good (Rainsville 3 h cold seeding load, 2026-10-02: 38 of 39).

		private static Task<string> Retry(Func<int, string> attempt, List<int>? retried = null, CancellationToken ct = default)
		{
			var n = 0;
			return Level2RadarService.RetryTransientAsync(() => Task.FromResult(attempt(++n)),
				(_, a) => retried?.Add(a), ct, _ => TimeSpan.Zero);
		}

		[Fact]
		public async Task A_dropped_connection_is_retried_until_it_lands()
		{
			var retried = new List<int>();
			var result = await Retry(n => n < 3 ? throw new HttpRequestException("reset", new IOException("forcibly closed")) : "volume", retried);
			Assert.Equal("volume", result);
			Assert.Equal(new[] { 1, 2 }, retried);
		}

		[Fact]
		public async Task It_gives_up_after_the_last_attempt()
		{
			var attempts = 0;
			await Assert.ThrowsAsync<IOException>(() => Retry(_ => { attempts++; throw new IOException("dropped"); }));
			Assert.Equal(Level2RadarService.FetchAttempts, attempts);
		}

		[Fact]
		public async Task A_missing_file_is_not_retried()
		{
			var attempts = 0;
			await Assert.ThrowsAsync<HttpRequestException>(() => Retry(_ =>
			{
				attempts++;
				throw new HttpRequestException("404", null, HttpStatusCode.NotFound);
			}));
			Assert.Equal(1, attempts);
		}

		[Theory]
		[InlineData(null, true)]                                  // no response at all: reset, refused, TLS
		[InlineData(HttpStatusCode.ServiceUnavailable, true)]
		[InlineData(HttpStatusCode.InternalServerError, true)]
		[InlineData(HttpStatusCode.TooManyRequests, true)]
		[InlineData(HttpStatusCode.RequestTimeout, true)]
		[InlineData(HttpStatusCode.NotFound, false)]
		[InlineData(HttpStatusCode.Forbidden, false)]
		public void Which_http_failures_are_transient(HttpStatusCode? status, bool transient) =>
			Assert.Equal(transient, Level2RadarService.IsTransientFetchFailure(new HttpRequestException("x", null, status), default));

		[Fact]
		public void Other_failures_and_a_cancelled_load_are_not_retried()
		{
			Assert.True(Level2RadarService.IsTransientFetchFailure(new IOException("body dropped"), default));
			Assert.True(Level2RadarService.IsTransientFetchFailure(new TaskCanceledException("timeout", new TimeoutException()), default));
			Assert.False(Level2RadarService.IsTransientFetchFailure(new TaskCanceledException("cancelled"), default));
			Assert.False(Level2RadarService.IsTransientFetchFailure(new InvalidDataException("bad gzip"), default));
			Assert.False(Level2RadarService.IsTransientFetchFailure(new IOException("dropped"), new CancellationToken(canceled: true)));
		}
	}
}
