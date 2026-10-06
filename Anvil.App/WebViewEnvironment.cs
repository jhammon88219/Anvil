using System;
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
	}
}
