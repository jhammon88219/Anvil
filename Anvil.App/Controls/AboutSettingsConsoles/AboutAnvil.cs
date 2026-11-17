using System;
using System.Diagnostics;

namespace Anvil.Controls.AboutSettingsConsoles
{
	/// <summary>
	/// The facts Settings → About shows (and the Settings band's VERSION / BUILD): read once, never throw. Also the page's
	/// links and its "Copy details" text. ⚠️ global:: on every WinRT type — inside Anvil.Controls.*, a bare Windows.* resolves
	/// to Anvil.Controls.Windows.
	/// </summary>
	internal static class AboutAnvil
	{
		public const string SourceUrl = "https://github.com/jhammon88219/Anvil";
		public const string IssuesUrl = SourceUrl + "/issues";
		public const string LicenseUrl = SourceUrl + "/blob/master/LICENSE.txt";

		/// <summary>The MapLibre GL JS build in Assets/Map/vendor/maplibre-gl.js. ⚠️ Change it with the vendor file.</summary>
		public const string MapLibreVersion = "5.21.1";

		/// <summary>The installed package's version ("1.0.0.0"), or "—" when there's no package identity.</summary>
		public static string Version { get; } = ReadVersion();

		/// <summary>"Debug" or "Release" — which build is running.</summary>
#if DEBUG
		public const string BuildKind = "Debug";
#else
		public const string BuildKind = "Release";
#endif

		/// <summary>The Windows App SDK runtime the app is running on, or "—".</summary>
		public static string WindowsAppSdk { get; } = ReadWindowsAppSdk();

		/// <summary>The WebView2 runtime that draws the map, or "—".</summary>
		public static string WebView2 { get; } = ReadWebView2();

		/// <summary>Every line of "This copy", for a bug report (the plate's Copy details).</summary>
		public static string Details =>
			$"Anvil {Version} ({BuildKind})\nWindows App SDK {WindowsAppSdk}\nWebView2 {WebView2}\nMapLibre GL JS {MapLibreVersion}\n" +
			$"Windows {Environment.OSVersion.Version}";

		private static string ReadVersion()
		{
			try
			{
				var v = global::Windows.ApplicationModel.Package.Current.Id.Version;
				return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
			}
			catch (InvalidOperationException) { return "—"; } // run without package identity
		}

		private static string ReadWindowsAppSdk()
		{
			try { return Microsoft.Windows.ApplicationModel.WindowsAppRuntime.RuntimeInfo.AsString; }
			catch (Exception ex) { Debug.WriteLine($"[about] Windows App SDK version: {ex.Message}"); return "—"; }
		}

		private static string ReadWebView2()
		{
			try { return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "—"; }
			catch (Exception ex) { Debug.WriteLine($"[about] WebView2 version: {ex.Message}"); return "—"; }
		}

		/// <summary>Opens a web link in the user's browser (fire and forget).</summary>
		public static void OpenLink(string url) => _ = global::Windows.System.Launcher.LaunchUriAsync(new Uri(url));

		/// <summary>Puts <see cref="Details"/> on the clipboard.</summary>
		public static void CopyDetails()
		{
			var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
			package.SetText(Details);
			global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
		}
	}
}
