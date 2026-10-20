using System;
using System.IO;
using System.Threading.Tasks;
using Anvil.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The JSON-backed <see cref="SettingsService"/> ported from AppBase: defaults on a missing/corrupt
	/// file, a save→reload round-trip (via the debounced auto-save), and reset. Each test points the
	/// service at a throwaway temp folder (the ctor's test-only directory override) so runs stay isolated
	/// and never touch the real <c>%LocalAppData%\Anvil\Settings</c>.
	/// </summary>
	public class SettingsServiceTests
	{
		private static string TempDir() =>
			Path.Combine(Path.GetTempPath(), "AnvilSettingsTests", Guid.NewGuid().ToString("N"));

		private static SettingsService New(string dir) =>
			new(NullLogger<SettingsService>.Instance, dir);

		[Fact]
		public void Defaults_WhenNoFileExists()
		{
			var svc = New(TempDir());
			Assert.NotNull(svc.Settings);
			Assert.Equal("", svc.Settings.MapDataFolder);
			Assert.Equal("usa_full.pmtiles", svc.MapDataFileName);
		}

		[Fact]
		public void Map_setups_in_use_file_drives_the_folder_and_name_while_it_exists()
		{
			var dir = TempDir();
			var maps = Path.Combine(dir, "maps");
			Directory.CreateDirectory(maps);
			var file = Path.Combine(maps, "anvil-map-standard-hi.pmtiles");
			File.WriteAllText(file, "x");
			var svc = New(dir);
			svc.Settings.MapDataFolder = @"C:\Old\Folder";

			svc.Settings.BasemapFilePath = file;
			Assert.Equal(maps, svc.MapDataFolder);
			Assert.Equal("anvil-map-standard-hi.pmtiles", svc.MapDataFileName);
			Assert.True(svc.MapDataFilePresent());
			Assert.Equal(@"C:\Old\Folder", svc.LegacyMapDataFolder);

			File.Delete(file); // gone → the older usa_full rule again, never a dead path
			Assert.Equal(@"C:\Old\Folder", svc.MapDataFolder);
			Assert.Equal("usa_full.pmtiles", svc.MapDataFileName);
		}

		[Fact]
		public async Task Persists_AcrossInstances()
		{
			var dir = TempDir();
			var svc = New(dir);
			svc.Settings.MapDataFolder = @"C:\Some\Basemap\Folder";
			await Task.Delay(700); // debounce (500 ms) + buffer

			var reloaded = New(dir);
			Assert.Equal(@"C:\Some\Basemap\Folder", reloaded.Settings.MapDataFolder);
		}

		[Fact]
		public async Task ResetToDefaults_RestoresValues()
		{
			var svc = New(TempDir());
			svc.Settings.MapDataFolder = @"C:\Changed";
			await svc.ResetToDefaultsAsync();
			Assert.Equal("", svc.Settings.MapDataFolder);
		}

		[Fact]
		public void CorruptFile_FallsBackToDefaults()
		{
			var dir = TempDir();
			Directory.CreateDirectory(dir);
			File.WriteAllText(Path.Combine(dir, "AppSettings.json"), "{ not valid json !!!");

			var svc = New(dir); // must not throw
			Assert.NotNull(svc.Settings);
			Assert.Equal("", svc.Settings.MapDataFolder);
		}

		[Fact]
		public void MapDataFolder_ResolvesEffectiveDefault_WhenUnset()
		{
			var svc = New(TempDir());
			// Empty persisted value → the effective folder resolves to a non-empty runtime path.
			Assert.False(string.IsNullOrEmpty(svc.MapDataFolder));
		}
	}
}
