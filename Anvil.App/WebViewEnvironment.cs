using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Anvil
{
	/// <summary>
	/// The ONE WebView2 environment every map page runs in: the main map (MainWindow) and the Settings
	/// window's preview (Controls/Composites/MapPreview).
	/// </summary>
	/// <remarks>
	/// ⚠️ COLOUR IDENTITY: Chromium colour-manages the page to the monitor's profile while WinUI draws values
	/// untouched, so the same literal came out as two colours (the site keys' #3fb950 measured ~#6fce62 on the
	/// map vs exactly #3fb950 in the Atlas). Forcing sRGB makes the page emit its values as written — so a radar
	/// ramp, SPC colour or status square is the number in the file, and matches the chrome beside it.
	/// ⚠️ Arguments are fixed per user-data folder: a second WebView2 created with DIFFERENT options fails, which
	/// is why there is one shared, lazily created environment rather than one per page.
	/// </remarks>
	internal static class WebViewEnvironment
	{
		private static Task<CoreWebView2Environment>? _environment;

		/// <summary>The shared environment (created on first use; every caller is on the UI thread).</summary>
		public static Task<CoreWebView2Environment> GetAsync() =>
			_environment ??= CoreWebView2Environment.CreateWithOptionsAsync(null, null,
				new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--force-color-profile=srgb" }).AsTask();

		/// <summary>
		/// The basemap folder the MAIN map's <c>mapdata</c> host was mapped to at launch. A folder change applies
		/// next launch (MainWindow maps the host once), so a preview must map THIS one, not the setting's
		/// current value, or it would show a basemap the map isn't drawing.
		/// </summary>
		public static string? LaunchMapDataFolder { get; set; }

		/// <summary>The offline map FILE the main map reads in <see cref="LaunchMapDataFolder"/> (map setup's in-use file or
		/// <c>usa_full.pmtiles</c>) — the preview passes it on as its <c>mapfile</c> too.</summary>
		public static string? LaunchMapDataFile { get; set; }

		private static Task? _freshAssets;

		/// <summary>
		/// Make sure the page runs the map files THIS build shipped, not a copy from an earlier build. Call after
		/// EnsureCoreWebView2Async and before the first navigation; runs once per launch (both pages share it).
		/// </summary>
		/// <remarks>
		/// ⚠️ WHY: the environment keeps a persistent HTTP DISK CACHE, and the <c>mapassets</c> folder mapping serves
		/// files with no cache headers — so Chromium may keep reusing a cached map.js / radar-sites.js / stylesheet
		/// after the file on disk changed. A stale module is worse than a stale style: an old radar-sites.js still
		/// injects its old key CSS over the new stylesheet, and the change looks like it never happened.
		/// So: stamp the page's own files (the newest write time under map.html, preview.html, js/ and styles/), and
		/// when the stamp differs from the last launch's, clear the disk cache once before anything loads. Basemap
		/// tiles, fonts and the data caches are local folders too, so clearing costs a re-read, never a download
		/// (online tiles excepted, once per changed build).
		/// </remarks>
		public static Task EnsureFreshMapAssetsAsync(CoreWebView2 core) => _freshAssets ??= ClearIfChangedAsync(core);

		private static async Task ClearIfChangedAsync(CoreWebView2 core)
		{
			try
			{
				var root = Path.Combine(AppContext.BaseDirectory, "Assets", "Map");
				var files = new[] { Path.Combine(root, "map.html"), Path.Combine(root, "preview.html") }
					.Where(File.Exists)
					.Concat(new[] { "js", "styles" }
						.Select(d => Path.Combine(root, d))
						.Where(Directory.Exists)
						.SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)));
				var stamp = files.Select(f => File.GetLastWriteTimeUtc(f).Ticks).DefaultIfEmpty(0).Max()
					.ToString(CultureInfo.InvariantCulture);

				var stampFile = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anvil", "WebView", "map-assets.stamp");
				var last = File.Exists(stampFile) ? File.ReadAllText(stampFile).Trim() : string.Empty;
				if (last == stamp)
				{
					return;
				}

				await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
				Directory.CreateDirectory(Path.GetDirectoryName(stampFile)!);
				File.WriteAllText(stampFile, stamp);
			}
			catch
			{
				// A failed check must never stop the map loading — worst case is the old behaviour (a stale cache).
			}
		}
	}
}
