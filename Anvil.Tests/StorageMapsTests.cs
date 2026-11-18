using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Settings → Storage (2026-11-16): the MAPS kind (every map file Anvil knows, on the bar and first in the table, with no
	/// Clear and no limits), the Disk usage card's Restore defaults, and Live Data's polling defaults.
	/// </summary>
	public class StorageMapsTests
	{
		private static StorageSettingsViewModel New(AppSettings settings)
		{
			var radar = TemporalPanePersistenceTests.Null<ILevel2RadarService>.Create(new()
			{
				["GetCacheSizeBytesAsync"] = _ => Task.FromResult(0L),
				["get_CacheDirectory"] = _ => Path.GetTempPath(),
			});
			var storage = TemporalPanePersistenceTests.Null<IStorageService>.Create(new()
			{
				["MeasureAsync"] = _ => Task.FromResult(0L),
				["FolderOf"] = _ => Path.GetTempPath(),
				["SweepAsync"] = _ => Task.CompletedTask,
			});
			var settingsService = TemporalPanePersistenceTests.Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings });
			return new StorageSettingsViewModel(radar, settingsService, storage);
		}

		[Fact]
		public void Hovering_a_row_dims_every_other_bar_segment()
		{
			var vm = New(new AppSettings());
			Assert.DoesNotContain(vm.Items, i => i.IsDimmed);

			vm.RadarVolumes.IsHovered = true;
			Assert.False(vm.RadarVolumes.IsDimmed);
			Assert.All(vm.Items.Where(i => i != vm.RadarVolumes), i => Assert.True(i.IsDimmed));

			vm.RadarVolumes.IsHovered = false;
			Assert.DoesNotContain(vm.Items, i => i.IsDimmed);
		}

		[Fact]
		public void Maps_is_the_first_row_and_can_not_be_cleared_or_limited()
		{
			var vm = New(new AppSettings());
			Assert.Same(vm.Maps, vm.Items[0]);
			Assert.False(vm.Maps.HasClear);
			Assert.False(vm.Maps.HasMax);
			Assert.False(vm.Maps.HasKeep);
			Assert.Equal("", vm.Maps.NoKeepText);   // a hairline, not "Always kept"
			Assert.Equal("", vm.Maps.DefaultText);  // nothing to restore
			Assert.True(vm.UsageHistory.HasClear);
		}

		[Fact]
		public async Task Maps_counts_every_known_map_file_into_the_total()
		{
			var dir = Path.Combine(Path.GetTempPath(), "AnvilStorageMaps", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			try
			{
				var a = Path.Combine(dir, "a.pmtiles");
				var b = Path.Combine(dir, "elsewhere-b.pmtiles");
				File.WriteAllBytes(a, new byte[3 * 1024 * 1024]);
				File.WriteAllBytes(b, new byte[2 * 1024 * 1024]);

				var vm = New(new AppSettings());
				vm.AttachMaps(() => new[] { a, b, Path.Combine(dir, "gone.pmtiles") }, () => dir);
				await vm.RefreshAsync();

				Assert.Equal(5L * 1024 * 1024, vm.Maps.SizeBytes);
				Assert.Equal("5.0 MB", vm.TotalText); // the other kinds measured 0 here
				Assert.Equal(dir, vm.Maps.Folder);
			}
			finally { Directory.Delete(dir, recursive: true); }
		}

		[Fact]
		public void On_disk_restore_defaults_puts_every_limit_back()
		{
			var fresh = new AppSettings();
			var s = new AppSettings { WeatherDataMaxMb = 2048, WeatherDataKeepDays = 90, LogsMaxMb = 500, LogsKeepDays = 365 };
			var vm = New(s);
			vm.RestoreDefaults();

			Assert.Equal((fresh.WeatherDataMaxMb, fresh.WeatherDataKeepDays, fresh.LogsMaxMb, fresh.LogsKeepDays),
				(s.WeatherDataMaxMb, s.WeatherDataKeepDays, s.LogsMaxMb, s.LogsKeepDays));
			Assert.Equal("500 MB · 30 days", vm.WeatherData.DefaultText);
		}

		[Fact]
		public void A_feed_row_says_its_default_and_restores_it()
		{
			var s = new AppSettings();
			var row = new LivePollRow(LiveFeedKind.Warnings, s) { Seconds = 90 };
			Assert.Equal(90, row.Seconds);
			Assert.Equal("30 s", row.DefaultText);
			Assert.True(row.IsLive);

			row.Reset();
			Assert.Equal(30, row.Seconds);
		}
	}
}
