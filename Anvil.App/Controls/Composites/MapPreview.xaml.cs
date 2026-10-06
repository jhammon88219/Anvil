using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Anvil.Services;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The Settings window's live map preview (see the XAML header). Owns a WebView2 for exactly as long as it is
	/// loaded, and registers that page as a mirror of the main map's look commands.
	/// </summary>
	public sealed partial class MapPreview : UserControl
	{
		private PreviewView? _view;
		private bool _started, _closed;

		public MapPreview()
		{
			InitializeComponent();
			Loaded += OnLoaded;
			Unloaded += OnUnloaded;
		}

		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(MapPreview), new PropertyMetadata(null));

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			if (_started) return;
			_started = true;
			_ = StartAsync();
		}

		private async Task StartAsync()
		{
			var vm = ViewModel;
			if (vm is null) return;

			PreviewWebView.DefaultBackgroundColor = ParseColor(vm.SelectedTheme.GroundColor);
			try
			{
				await PreviewWebView.EnsureCoreWebView2Async(await WebViewEnvironment.GetAsync());
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[map-preview] WebView2 failed to start: {ex.Message}");
				return;
			}
			if (_closed) return; // the window closed while the environment came up

			var core = PreviewWebView.CoreWebView2;
			core.Settings.AreDefaultContextMenusEnabled = false;
			// ONLY what a basemap + rings need — no cache hosts, this page draws no data.
			core.SetVirtualHostNameToFolderMapping("mapassets",
				Path.Combine(AppContext.BaseDirectory, "Assets", "Map"), CoreWebView2HostResourceAccessKind.Allow);
			if (!string.IsNullOrEmpty(WebViewEnvironment.LaunchMapDataFolder))
			{
				core.SetVirtualHostNameToFolderMapping("mapdata", WebViewEnvironment.LaunchMapDataFolder,
					CoreWebView2HostResourceAccessKind.Allow);
			}
			core.WebMessageReceived += OnWebMessageReceived;

			// Mirror from NOW: a command that lands before the page is up is lost, and the replay on
			// 'previewReady' covers exactly that gap.
			_view = new PreviewView(this);
			vm.AttachMapPreview(_view);

			PreviewWebView.Source = new Uri(BuildUrl(vm));
		}

		// The page is launched on the main map's CURRENT look (same params as MainWindow.BuildMapUrl), so its
		// first paint is already right; the replay then no-ops for those.
		private static string BuildUrl(MapViewModel vm)
		{
			var site = vm.PreviewSite;
			var url = "https://mapassets/preview.html" +
				$"?style={Uri.EscapeDataString(vm.SelectedStyle?.Url ?? "https://mapassets/style.json")}" +
				$"&tiles={(vm.IsOnlineTilesActive ? "online" : "offline")}" +
				$"&tilesUrl={Uri.EscapeDataString(vm.OnlineTilesUrl ?? "")}" +
				$"&theme={Uri.EscapeDataString(vm.SelectedTheme.Id ?? "")}";
			if (site is not null)
			{
				url += $"&lat={site.Latitude.ToString(CultureInfo.InvariantCulture)}" +
					$"&lon={site.Longitude.ToString(CultureInfo.InvariantCulture)}";
			}
			return url;
		}

		private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
		{
			if (_closed || ViewModel is not { } vm || _view is null) return;
			try
			{
				using var doc = JsonDocument.Parse(args.TryGetWebMessageAsString());
				var root = doc.RootElement;
				switch (root.GetProperty("type").GetString())
				{
					case "previewReady":
						_ = vm.ReplayToMapPreviewAsync(_view);
						break;
					case "rangeRingLabelBearing":
						vm.OnPreviewLabelBearingDragged(root.GetProperty("deg").GetDouble());
						break;
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[map-preview] bad message: {ex.Message}");
			}
		}

		private void OnFitClick(object sender, RoutedEventArgs e) => _ = _view?.RunScriptAsync("window.previewFit && window.previewFit();");

		private void OnUnloaded(object sender, RoutedEventArgs e)
		{
			if (_closed) return;
			_closed = true;
			if (_view is not null) ViewModel?.DetachMapPreview(_view);
			if (PreviewWebView.CoreWebView2 is { } core) core.WebMessageReceived -= OnWebMessageReceived;
			PreviewWebView.Close(); // frees the renderer — this page must not outlive the window
		}

		// "#RRGGBB" → a Color, BLACK on anything malformed (a wrong flash colour, never a crash).
		private static global::Windows.UI.Color ParseColor(string hex)
		{
			var h = (hex ?? "").TrimStart('#');
			if (h.Length == 6 && int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
			{
				return Microsoft.UI.ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
			}
			return Microsoft.UI.Colors.Black;
		}

		/// <summary>The preview page as an <see cref="IMapView"/> — what the mirror runs scripts against.
		/// Swallows everything: a preview that failed must never fail a main-map command.</summary>
		private sealed class PreviewView : IMapView
		{
			private readonly MapPreview _owner;

			public PreviewView(MapPreview owner) => _owner = owner;

			public async Task<string> RunScriptAsync(string javaScript)
			{
				if (_owner._closed || _owner.PreviewWebView.CoreWebView2 is not { } core) return string.Empty;
				try { return await core.ExecuteScriptAsync(javaScript); }
				catch { return string.Empty; }
			}
		}
	}
}
