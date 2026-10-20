using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// Map setup's actions past the download: the map key (test, save inside the tile URL), a file the user brings (the
	/// three checks), and the file list (add, delete — the map in use is let go of first, a refused delete waits for the
	/// next launch). Settings are a real <see cref="SettingsService"/> in a temp folder.
	/// </summary>
	public sealed class BasemapSetupActionsTests : IDisposable
	{
		private readonly string _dir = Path.Combine(Path.GetTempPath(), "anvil-setup-test-" + Guid.NewGuid().ToString("N"));

		public BasemapSetupActionsTests() => Directory.CreateDirectory(_dir);

		public void Dispose()
		{
			try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
		}

		// ── the key ──

		[Fact]
		public void The_key_goes_into_the_tile_url_and_back()
		{
			var url = ProtomapsKeys.UrlFor(" abc123 ");
			Assert.Equal("https://api.protomaps.com/tiles/v4.json?key=abc123", url);
			Assert.Equal("abc123", ProtomapsKeys.KeyOf(url));
			Assert.Equal("", ProtomapsKeys.KeyOf(AppSettings.DefaultOnlineTilesUrl));
			Assert.Equal("", ProtomapsKeys.KeyOf("pmtiles://https://my.bucket/map.pmtiles")); // self-hosted: no key to show
		}

		[Theory]
		[InlineData(HttpStatusCode.OK, MapKeyCheck.Works)]
		[InlineData(HttpStatusCode.Forbidden, MapKeyCheck.Rejected)]
		[InlineData(HttpStatusCode.InternalServerError, MapKeyCheck.Unreachable)]
		public async Task A_key_test_reads_the_tile_answer(HttpStatusCode status, MapKeyCheck expected)
		{
			var keys = new ProtomapsKeys(new HttpClient(new Answer(_ => new HttpResponseMessage(status))));
			Assert.Equal(expected, await keys.CheckAsync("k", CancellationToken.None));
		}

		[Fact]
		public async Task No_connection_is_not_a_bad_key()
		{
			var keys = new ProtomapsKeys(new HttpClient(new Answer(_ => throw new HttpRequestException("offline"))));
			Assert.Equal(MapKeyCheck.Unreachable, await keys.CheckAsync("k", CancellationToken.None));
		}

		[Fact]
		public async Task Use_online_map_saves_the_key_only_when_it_works()
		{
			var (settings, vm) = Make(new FixedKeys(MapKeyCheck.Rejected));
			vm.MapKey = "bad";
			vm.UseOnlineMap();
			await Until(() => !vm.IsCheckingKey);
			Assert.False(settings.Settings.UseOnlineTiles);
			Assert.StartsWith("That key didn't work", vm.KeyMessage);

			(settings, vm) = Make(new FixedKeys(MapKeyCheck.Works));
			vm.MapKey = "good";
			vm.IsOnlineChosen = true;
			vm.RunPrimaryAction(); // the window's "Use online map"
			await Until(() => vm.Step == BasemapSetupStep.Ready);
			Assert.True(settings.Settings.UseOnlineTiles);
			Assert.Equal(ProtomapsKeys.UrlFor("good"), settings.Settings.OnlineTilesUrl);
			Assert.Equal("Online map · Protomaps", vm.ReadyMapText);
		}

		// ── a file the user brings ──

		[Fact]
		public void A_protomaps_map_passes_the_checks_with_its_detail_and_cover()
		{
			var path = MapFile("ok.pmtiles", maxZoom: 13, protomaps: true);
			var check = BasemapFileCheck.Inspect(path, new[] { (35.0, -97.0), (61.0, -150.0) }); // Oklahoma inside, Alaska not

			Assert.True(check.CanUse);
			Assert.Equal("Covers 1 of 2 NEXRAD radars.", check.CoverLine);
			Assert.StartsWith("Standard detail · ", check.DetailLine);
		}

		[Fact]
		public void Other_files_are_refused_in_plain_words()
		{
			var text = Path.Combine(_dir, "notes.pmtiles");
			File.WriteAllText(text, "not a map");
			Assert.False(BasemapFileCheck.Inspect(text, Array.Empty<(double, double)>()).CanUse);

			var other = MapFile("other.pmtiles", maxZoom: 14, protomaps: false); // a map, but not in Anvil's map style
			var check = BasemapFileCheck.Inspect(other, Array.Empty<(double, double)>());
			Assert.False(check.CanUse);
			Assert.Contains("can't use it", check.ReadLine);
		}

		[Fact]
		public async Task An_existing_file_becomes_the_map_in_use_and_stays_listed()
		{
			var (settings, vm) = Make();
			var path = MapFile("brought.pmtiles", maxZoom: 15, protomaps: true);

			vm.ExistingFilePath = path;
			await Until(() => vm.CanUseExistingFile);
			Assert.StartsWith("Full detail", vm.ExistingDetailLine);
			vm.UseExistingFile();

			Assert.Equal(path, settings.Settings.BasemapFilePath);
			Assert.Equal(BasemapSetupStep.Ready, vm.Step);
			Assert.Contains(vm.Files, r => r.Path == path && r.IsInUse);
		}

		// ── the list ──

		[Fact]
		public async Task Add_lists_a_file_where_it_sits_and_refuses_a_bad_one()
		{
			var (settings, vm) = Make();
			var good = MapFile("elsewhere.pmtiles", maxZoom: 13, protomaps: true);
			await vm.AddFileAsync(good);
			Assert.Contains(vm.Files, r => r.Path == good && !r.IsInUse);
			Assert.Contains(good, settings.Settings.BasemapAddedFiles);

			var bad = Path.Combine(_dir, "bad.pmtiles");
			File.WriteAllText(bad, "nope");
			await vm.AddFileAsync(bad);
			Assert.DoesNotContain(vm.Files, r => r.Path == bad);
			Assert.Equal("This isn't a map file Anvil can read.", vm.FilesMessage);
		}

		[Fact]
		public void Deleting_the_map_in_use_lets_go_of_it_first()
		{
			var (settings, vm) = Make();
			var path = MapFile("inuse.pmtiles", maxZoom: 13, protomaps: true);
			settings.Settings.BasemapFilePath = path;
			vm.Refresh();

			string? inUseWhenDeleted = null;
			settings.Settings.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(AppSettings.BasemapFilePath)) inUseWhenDeleted = File.Exists(path) ? "still there" : "gone";
			};
			vm.DeleteFile(vm.Files.Single(r => r.Path == path));

			Assert.Equal("still there", inUseWhenDeleted); // the map was re-pointed BEFORE the file went
			Assert.False(File.Exists(path));
			Assert.Equal("", settings.Settings.BasemapFilePath);
			Assert.DoesNotContain(vm.Files, r => r.Path == path);
		}

		[Fact]
		public void A_file_windows_holds_open_is_deleted_at_the_next_launch()
		{
			var (settings, vm) = Make();
			var path = MapFile("held.pmtiles", maxZoom: 13, protomaps: true);
			settings.Settings.BasemapFilePath = path;
			vm.Refresh();

			using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) // no delete share: Windows refuses
			{
				vm.DeleteFile(vm.Files.Single(r => r.Path == path));
				Assert.True(File.Exists(path));
				Assert.Contains(path, settings.Settings.BasemapPendingDeletes);
				Assert.DoesNotContain(vm.Files, r => r.Path == path); // gone, as far as the user is concerned
				Assert.Contains("next time it opens", vm.FilesMessage);
			}

			vm.DeletePendingFiles(); // the next launch
			Assert.False(File.Exists(path));
			Assert.Empty(settings.Settings.BasemapPendingDeletes);
		}

		[Fact]
		public void The_save_folder_is_remembered()
		{
			var (settings, vm) = Make();
			var folder = Path.Combine(_dir, "maps");
			vm.SaveFolder = folder;
			Assert.Equal(folder, settings.Settings.BasemapSaveFolder);
			Assert.Equal(folder, new BasemapSetupViewModel(settings, new NoSites()).SaveFolder);

			vm.SaveFolder = BasemapSetupViewModel.DefaultSaveFolder;
			Assert.Equal("", settings.Settings.BasemapSaveFolder); // the default is stored as "use the default"
		}

		// ── helpers ──

		private (SettingsService Settings, BasemapSetupViewModel Vm) Make(IMapKeyChecker? keys = null)
		{
			var settings = new SettingsService(NullLogger<SettingsService>.Instance, Path.Combine(_dir, "settings-" + Guid.NewGuid().ToString("N")));
			settings.Settings.MapDataFolder = Path.Combine(_dir, "no-legacy-here");
			return (settings, new BasemapSetupViewModel(settings, new NoSites(), null, keys));
		}

		/// <summary>A tiny PMTiles file: a header + metadata naming Protomaps' layers (or not), over the Lower 48.</summary>
		private string MapFile(string name, byte maxZoom, bool protomaps)
		{
			var metadata = Encoding.UTF8.GetBytes(protomaps
				? "{\"vector_layers\":[{\"id\":\"earth\"},{\"id\":\"water\"},{\"id\":\"roads\"},{\"id\":\"places\"}]}"
				: "{\"vector_layers\":[{\"id\":\"transportation\"},{\"id\":\"building\"}]}");
			var root = PmtilesWriter.SerializeDirectory(new[] { new PmtilesEntry(0, 0, 1, 1) });
			ulong rootOffset = PmtilesHeader.Length, metaOffset = rootOffset + (ulong)root.Length, dataOffset = metaOffset + (ulong)metadata.Length;
			var header = new PmtilesHeader(rootOffset, (ulong)root.Length, metaOffset, (ulong)metadata.Length, dataOffset, 0, dataOffset, 1,
				1, 1, 1, true, PmtilesCompression.None, PmtilesCompression.Gzip, 1, 0, maxZoom,
				-1270000000, 235000000, -650000000, 500000000, 4, -960000000, 380000000);
			var path = Path.Combine(_dir, name);
			File.WriteAllBytes(path, PmtilesWriter.HeaderBytes(header).Concat(root).Concat(metadata).Append((byte)0).ToArray());
			return path;
		}

		private static async Task Until(Func<bool> done)
		{
			for (var i = 0; i < 400 && !done(); i++) await Task.Delay(5);
			Assert.True(done(), "timed out");
		}

		private sealed class NoSites : IRadarSiteProvider
		{
			public IReadOnlyList<RadarSite> GetSites() => Array.Empty<RadarSite>();
		}

		private sealed class FixedKeys : IMapKeyChecker
		{
			private readonly MapKeyCheck _answer;
			public FixedKeys(MapKeyCheck answer) => _answer = answer;
			public Task<MapKeyCheck> CheckAsync(string key, CancellationToken ct) => Task.FromResult(_answer);
		}

		private sealed class Answer : HttpMessageHandler
		{
			private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
			public Answer(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;
			protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
				Task.FromResult(_answer(request));
		}
	}
}
