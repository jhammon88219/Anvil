using System;
using System.Linq;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Settings → Storage's rules: the weather-data / log TRIM plan (<see cref="StorageService.PlanTrim"/>) and the
	/// Size colour levels (<see cref="StorageLimits.FillOf"/>).
	/// </summary>
	public class StorageServiceTests
	{
		private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
		private const long MB = StorageLimits.MB;

		private static (string, long, DateTime) F(string name, long mb, TimeSpan age) => (name, mb * MB, Now - age);

		[Fact]
		public void AgedFilesGoFirst_EvenUnderTheSizeCap()
		{
			var files = new[] { F("old", 1, TimeSpan.FromDays(31)), F("new", 1, TimeSpan.FromDays(1)) };
			var doomed = StorageService.PlanTrim(files, 100 * MB, TimeSpan.FromDays(30), Now);
			Assert.Equal(new[] { "old" }, doomed.Select(d => d.Path));
		}

		[Fact]
		public void OverTheCap_OldestGoUntilItFits()
		{
			var files = new[]
			{
				F("c", 40, TimeSpan.FromHours(3)), F("a", 40, TimeSpan.FromHours(10)), F("b", 40, TimeSpan.FromHours(5)),
			};
			var doomed = StorageService.PlanTrim(files, 50 * MB, TimeSpan.FromDays(30), Now);
			Assert.Equal(new[] { "a", "b" }, doomed.Select(d => d.Path)); // 120 → 80 → 40, oldest first
		}

		[Fact]
		public void RecentFilesAreNeverSizeTrimmed_ButStillCount()
		{
			var files = new[] { F("fresh", 80, TimeSpan.FromMinutes(5)), F("older", 10, TimeSpan.FromHours(2)) };
			var doomed = StorageService.PlanTrim(files, 50 * MB, TimeSpan.FromDays(30), Now);
			// fresh alone is over the cap; it's protected, so only "older" goes — and the folder stays over.
			Assert.Equal(new[] { "older" }, doomed.Select(d => d.Path));
		}

		[Fact]
		public void UnderTheCap_NothingGoes()
		{
			var files = new[] { F("a", 10, TimeSpan.FromDays(2)), F("b", 10, TimeSpan.FromDays(3)) };
			Assert.Empty(StorageService.PlanTrim(files, 100 * MB, TimeSpan.FromDays(30), Now));
		}

		[Theory]
		[InlineData(59, StorageFill.Low)]
		[InlineData(60, StorageFill.Medium)]
		[InlineData(84, StorageFill.Medium)]
		[InlineData(85, StorageFill.High)]
		[InlineData(120, StorageFill.High)]
		public void FillLevels_FollowTheThresholds(int percent, StorageFill expected)
		{
			Assert.Equal(expected, StorageLimits.FillOf(percent * MB, 100 * MB));
		}

		[Fact]
		public void NoMaxSize_IsAlwaysLow()
		{
			Assert.Equal(StorageFill.Low, StorageLimits.FillOf(10_000 * MB, null));
		}

		[Fact]
		public void AnUnlistedPersistedValue_IsKeptAsAChoice()
		{
			var (list, pick) = StorageLimits.Resolve(StorageLimits.WeatherDataKeep, 45, days: true);
			Assert.Equal("45 days", pick.Label);
			Assert.Contains(pick, list);
			Assert.True(list.Select(c => c.Value).SequenceEqual(list.Select(c => c.Value).OrderBy(v => v)));
		}
	}
}
