using System;
using System.IO;
using System.Linq;
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
	}
}
