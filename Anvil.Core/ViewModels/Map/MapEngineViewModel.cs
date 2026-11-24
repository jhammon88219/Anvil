using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// NATIVE-MAP (bolt-on, dev switch): which renderer draws the main map, MapLibre in the WebView or NativeMaps
	/// (the sibling repo's native C# renderer), plus what NativeMaps can draw of Anvil so far. Settings → Dev → Map engine.
	/// </summary>
	/// <remarks>
	/// The WebView map never goes away: NativeMaps is drawn OVER it while picked, so every overlay keeps its state and
	/// switching back is instant. MainWindow.NativeMap.cs hands the camera, the basemap theme and the map file across.
	/// Not saved: every launch starts on MapLibre (the Dev tab is Debug-only, so a Release build can never pick it).
	/// ⚠️ <see cref="Layers"/> is HAND-KEPT: tick a layer's <c>IsDrawn</c> in the same change that ports it.
	/// </remarks>
	public sealed class MapEngineViewModel : ObservableObject
	{
		/// <summary>The engine segments, in <see cref="EngineIndex"/> order.</summary>
		public IReadOnlyList<string> EngineLabels { get; } = ["MapLibre (WebView)", "NativeMaps"];

		private int _engineIndex;

		/// <summary>0 = MapLibre, 1 = NativeMaps.</summary>
		public int EngineIndex
		{
			get => _engineIndex;
			set
			{
				if (SetProperty(ref _engineIndex, value)) OnPropertyChanged(nameof(IsNativeMaps));
			}
		}

		/// <summary>NativeMaps is drawing the main map.</summary>
		public bool IsNativeMaps => _engineIndex == 1;

		private string _status = "MapLibre is drawing the map.";

		/// <summary>One line on what the picked engine is drawing (set by the view as it hands the map across).</summary>
		public string Status
		{
			get => _status;
			set => SetProperty(ref _status, value);
		}

		/// <summary>Every layer Anvil draws, and whether NativeMaps draws it yet.</summary>
		public IReadOnlyList<MapEngineLayer> Layers { get; } =
		[
			new("Basemap + themes", true),
			new("Place + road labels", true),
			new("Radar", false),
			new("Rings + ruler", false),
			new("Radar site markers", false),
			new("SPC outlook", false),
			new("Watches", false),
			new("Warnings", false),
			new("Storm reports", false),
			new("Storm cells", false),
			new("Damage surveys", false),
			new("Meso discussions", false),
			new("Tropical", false),
			new("Model field", false),
			new("Past alerts", false),
			new("States + isolation", false),
			new("County names", false),
			new("Location + place pins", false),
			new("Double / Quad panes", false),
			new("Online tiles", true),
		];
	}

	/// <summary>One row of the Map engine coverage list.</summary>
	public sealed record MapEngineLayer(string Name, bool IsDrawn)
	{
		public bool IsNotDrawn => !IsDrawn;
	}
}
