// ===== NATIVE-MAP (bolt-on, dev switch, 2026-11-23) — grep "NATIVE-MAP (bolt-on)" for every hook =====
//
//   Settings → Dev → Map engine   [ MapLibre (WebView) | NativeMaps ]
//
//   MapLibre picked:  ┌ WebView2 map (every overlay) ┐        NativeMaps picked:  ┌ NativeMapView (basemap + labels) ┐
//                     │                              │                            │   drawn OVER the WebView map,    │
//                     └──────────────────────────────┘                            └ which keeps running underneath ──┘
//
// • NativeMapHost (Controls/Composites) is declared straight after the WebView in MainWindow.xaml; its MapView sits behind
//   x:Load = IsActive = MapEngine.IsNativeMaps, so it is only BUILT while picked, and all the chrome declared after it
//   (headers, rail, strip, panes) still sits on top. (x:Load straight in a Window never builds: hence the UserControl.)
// • The WebView map never stops: every overlay keeps its state, so switching back is instant. The cost is two renderers
//   while NativeMaps is up, acceptable for a Debug-only dev switch.
// • HANDED ACROSS: the camera (window.getCamera → the view on load; the view's last camera → window.jumpCamera on the way
//   back), the basemap THEME (Anvil's five style ids map 1:1 onto NativeMaps' five Protomaps themes, live), and the
//   offline MAP FILE (opened as a PMTiles archive; re-opened live when Settings → Storage changes the map in use).
//   NativeMaps reads offline files only, so with online tiles on it still draws the offline file and says so.
// • What NativeMaps draws of Anvil so far is MapEngineViewModel.Layers (the Dev page's coverage list) — tick a layer
//   there in the same change that ports it.
//
// BACK OUT = delete this file, NativeMapHost.xaml(.cs), MapEngineViewModel.cs, MapCameraState.cs and every block tagged NATIVE-MAP (bolt-on) in
// MainWindow.xaml(.cs), MapViewModel.cs, IMapService.cs, MapService.cs, map.js and DevSettingsTab.xaml.

using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using NativeMaps.Core.Meshes;
using NativeMaps.Core.Pmtiles;
using NativeMaps.Core.Themes.Protomaps;
using NativeMapStyle = NativeMaps.Core.Styles.MapStyle;
using NativeMapView = NativeMaps.WinUI.MapView;

namespace Anvil
{
	public sealed partial class MainWindow
	{
		// The archive the native view is drawing, and the camera it last showed (handed back to the WebView map).
		private PmtilesArchive? _nativeArchive;
		private MapCameraState? _nativeCamera;
		private bool _nativeTileFailureReported;

		// Called once from the constructor, after InitializeComponent.
		private void InitNativeMap()
		{
			ViewModel.MapEngine.PropertyChanged += OnMapEnginePropertyChanged;
			ViewModel.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(MapViewModel.SelectedStyle) && NativeMapHost.View is { } view)
					view.MapStyle = NativeStyleFor(ViewModel.SelectedStyle);
			};
			_settingsService.Settings.PropertyChanged += OnNativeMapFileSettingChanged;
		}

		// To NativeMaps: x:Load builds the view; this only reports whether it did. Back to MapLibre: put the WebView map
		// where the native one was looking. (x:Load drops the native view itself.)
		private void OnMapEnginePropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName != nameof(MapEngineViewModel.IsNativeMaps)) return;
			if (ViewModel.MapEngine.IsNativeMaps)
			{
				_logger.LogInformation("Map engine: switching to NativeMaps");
				ViewModel.MapEngine.Status = "Starting NativeMaps...";
				// The x:Load binding runs in this same change notification, so by the next dispatcher turn the view is
				// either built or it never will be. If it isn't, say so instead of leaving the old status up.
				DispatcherQueue.TryEnqueue(() =>
				{
					if (!ViewModel.MapEngine.IsNativeMaps) return;
					if (NativeMapHost.View is not { } view)
					{
						_logger.LogWarning("Map engine: x:Load did not create NativeMapView");
						ViewModel.MapEngine.Status = "NativeMaps view was not created (x:Load did not build it). See the log.";
					}
					else
					{
						_logger.LogInformation("Map engine: NativeMapView created, loaded={Loaded}", view.IsLoaded);
					}
				});
				return;
			}
			if (_nativeCamera is { } camera) _ = _mapService.JumpToAsync(camera);
			ViewModel.MapEngine.Status = "MapLibre is drawing the map.";
			_logger.LogInformation("Map engine: MapLibre");
		}

		private async void OnNativeMapViewLoaded(object sender, RoutedEventArgs e)
		{
			var view = (NativeMapView)sender;
			_logger.LogInformation("Map engine: NativeMapView loaded, {Width}x{Height}", view.ActualWidth, view.ActualHeight);
			// Every step is logged and any failure lands on the Dev page: an async void handler otherwise fails silently.
			try
			{
				_nativeTileFailureReported = false;
				view.MapStyle = NativeStyleFor(ViewModel.SelectedStyle);
				view.TileFailed += OnNativeTileFailed;

				// Start where the WebView map is looking (its primary pane).
				_nativeCamera = await _mapService.GetCameraAsync();
				_logger.LogInformation("Map engine: camera from MapLibre {Camera}", _nativeCamera);
				if (_nativeCamera is { } camera)
				{
					view.CenterLongitude = camera.Longitude;
					view.CenterLatitude = camera.Latitude;
					view.Zoom = camera.Zoom;
					view.Bearing = camera.Bearing;
					view.Pitch = camera.Pitch;
				}
				view.CameraChanged += OnNativeCameraChanged;

				await OpenNativeMapFileAsync(view);
				_logger.LogInformation("Map engine: NativeMaps ready. {Status}", ViewModel.MapEngine.Status);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Map engine: NativeMaps failed to start");
				ViewModel.MapEngine.Status = $"NativeMaps failed to start: {ex.GetType().Name}: {ex.Message}";
			}
		}

		private void OnNativeMapViewUnloaded(object sender, RoutedEventArgs e)
		{
			var view = (NativeMapView)sender;
			view.CameraChanged -= OnNativeCameraChanged;
			view.TileFailed -= OnNativeTileFailed;
			view.RemoveSource(ProtomapsTheme.SourceName);
			_nativeArchive?.Dispose();
			_nativeArchive = null;
		}

		private void OnNativeCameraChanged(object? sender, EventArgs e)
		{
			if (sender is NativeMapView view)
				_nativeCamera = new MapCameraState(view.CenterLongitude, view.CenterLatitude, view.Zoom, view.Bearing, view.Pitch);
		}

		// Raised on a background thread. Logged every time; shown on the Dev page once per switch so it can't flood.
		private void OnNativeTileFailed(object? sender, TileLoadFailedEventArgs e)
		{
			_logger.LogWarning(e.Exception, "NativeMaps tile {Tile} failed", e.Tile);
			if (_nativeTileFailureReported) return;
			_nativeTileFailureReported = true;
			DispatcherQueue.TryEnqueue(() => ViewModel.MapEngine.Status += " A tile failed to load (see the log).");
		}

		// The map in use changed (Settings → Storage): the native view re-opens it, like the WebView's live switch.
		private void OnNativeMapFileSettingChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is not (nameof(AppSettings.BasemapFilePath) or nameof(AppSettings.MapDataFolder))) return;
			DispatcherQueue.TryEnqueue(async () =>
			{
				if (NativeMapHost.View is { } view) await OpenNativeMapFileAsync(view);
			});
		}

		private async Task OpenNativeMapFileAsync(NativeMapView view)
		{
			var file = _settingsService.MapDataFileName;
			var path = Path.Combine(_settingsService.MapDataFolder, file);
			if (!File.Exists(path))
			{
				view.RemoveSource(ProtomapsTheme.SourceName);
				ViewModel.MapEngine.Status = $"NativeMaps has no map to draw: {path} is missing. It reads offline map files only.";
				return;
			}

			PmtilesArchive archive;
			try
			{
				archive = await PmtilesArchive.OpenFileAsync(path);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "NativeMaps could not open {Path}", path);
				ViewModel.MapEngine.Status = $"NativeMaps could not open {file}: {ex.Message}";
				return;
			}

			// The view was switched away while the file opened: don't leak the archive.
			if (!ReferenceEquals(NativeMapHost.View, view))
			{
				archive.Dispose();
				return;
			}

			var previous = _nativeArchive;
			view.AddSource(ProtomapsTheme.SourceName, archive); // replaces the previous archive under the same name
			_nativeArchive = archive;
			previous?.Dispose();

			ViewModel.MapEngine.Status = ViewModel.IsOnlineTilesActive
				? $"NativeMaps is drawing {file}. MapLibre is on online tiles; NativeMaps reads the offline file only."
				: $"NativeMaps is drawing {file}.";
		}

		// Anvil's style ids (StyleProvider) → the matching NativeMaps theme. An imported style has no native twin, so it
		// gets Data Viz Black, Anvil's default look.
		private static NativeMapStyle NativeStyleFor(MapStyle? style) => style?.Id switch
		{
			"regular" => ProtomapsTheme.Light,
			"dark" => ProtomapsTheme.Dark,
			"dataVizlight" => ProtomapsTheme.DataVizLight,
			"dataVizGrayscale" => ProtomapsTheme.Grayscale,
			_ => ProtomapsTheme.DataVizBlack,
		};
	}
}
