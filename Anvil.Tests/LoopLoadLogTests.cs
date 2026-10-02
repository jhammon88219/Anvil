using System;
using System.IO;
using Anvil.Models;
using Anvil.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The PastCast load-time log (<see cref="LoopLoadLog"/>): one JSON line per load, appended, read back whole — and a
	/// damaged line costs only itself.
	/// </summary>
	public class LoopLoadLogTests
	{
		private static string TempDir()
		{
			var dir = Path.Combine(Path.GetTempPath(), $"anvil-loadlog-{Guid.NewGuid():N}");
			Directory.CreateDirectory(dir);
			return dir;
		}

		private static LoopLoadRecord Record(string site, long totalMs) => new(
			1, new DateTimeOffset(2026, 10, 2, 20, 0, 0, TimeSpan.Zero), site, "moore-1999",
			new DateTimeOffset(1999, 5, 3, 23, 0, 0, TimeSpan.Zero), 120, "0.5°", 1, new[] { "reflectivity" }, 8,
			"finished", 28, 28, 28, 0, totalMs, 3_000, 63_000, totalMs, false, true);

		[Fact]
		public void Appends_one_line_per_load_and_reads_them_back()
		{
			var log = new LoopLoadLog(NullLogger<LoopLoadLog>.Instance, TempDir());
			log.Append(Record("KTLX", 134_000));
			log.Append(Record("KINX", 90_000));

			Assert.Equal(2, File.ReadAllLines(log.FilePath).Length);
			var all = log.ReadAll();
			Assert.Equal(2, all.Count);
			Assert.Equal("KTLX", all[0].Site);
			Assert.Equal(134_000, all[0].TotalMs);
			Assert.Equal(new[] { "reflectivity" }, all[0].Products);
			Assert.Equal(90_000, all[1].TotalMs);
			Assert.Contains("\"totalMs\":134000", File.ReadAllLines(log.FilePath)[0]); // camelCase on disk
		}

		[Fact]
		public void A_damaged_line_is_skipped()
		{
			var log = new LoopLoadLog(NullLogger<LoopLoadLog>.Instance, TempDir());
			log.Append(Record("KTLX", 134_000));
			File.AppendAllText(log.FilePath, "{ not json\n");
			log.Append(Record("KINX", 90_000));
			Assert.Equal(2, log.ReadAll().Count);
		}

		// A version-1 line (before the frame sources) still reads; its counts come back 0 = unknown.
		[Fact]
		public void A_v1_line_without_the_sources_still_reads()
		{
			var log = new LoopLoadLog(NullLogger<LoopLoadLog>.Instance, TempDir());
			var v1 = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "loop-load-v1.json")).Trim();
			File.WriteAllText(log.FilePath, v1 + "\n");
			var r = Assert.Single(log.ReadAll());
			Assert.Equal(1, r.Version);
			Assert.Equal("KTLX", r.Site);
			Assert.Equal(0, r.CachedFrames);
		}

		[Fact]
		public void The_sources_round_trip()
		{
			var log = new LoopLoadLog(NullLogger<LoopLoadLog>.Instance, TempDir());
			log.Append(Record("KTLX", 134_000) with { Version = 2, CachedFrames = 20, LocalRawFrames = 2, NetworkFrames = 6 });
			var r = Assert.Single(log.ReadAll());
			Assert.Equal((20, 2, 6), (r.CachedFrames, r.LocalRawFrames, r.NetworkFrames));
		}

		[Fact]
		public void No_file_yet_reads_empty() =>
			Assert.Empty(new LoopLoadLog(NullLogger<LoopLoadLog>.Instance, TempDir()).ReadAll());
	}
}
