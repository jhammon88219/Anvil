using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// The mile distance grid: a square grid anchored to the SELECTED radar at a chosen mile spacing, that
	/// the user can hide/show (with an opacity slider) as a distance reference — a persistent alternative to
	/// a point-to-point measure tool. Surfaced in the Map Controls card.
	///
	/// Anchored to the radar (not the map), so it re-centers whenever the site changes — this VM watches
	/// <see cref="RadarViewModel.SelectedRadarOption"/> and re-draws. With no site selected it draws nothing
	/// (the toggle can be on, but the grid appears once a radar is picked). The grid geometry lives in the
	/// WebView (Assets/Map/js/grid.js, using the radar's own local projection); this VM just drives it
	/// through <see cref="IMapService"/>.
	/// </summary>
	public sealed class MileGridViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly RadarViewModel _radar;

		// Readiness guard: JS commands only run once the map page has reported 'mapReady' (mirrors the other
		// map-overlay VMs); a toggle flipped before then is applied in OnMapsReadyAsync.
		private bool _isMapReady;

		public MileGridViewModel(IMapService mapService, RadarViewModel radar)
		{
			_mapService = mapService;
			_radar = radar;
			// Re-anchor to the new site whenever the radar selection changes (only matters while shown).
			_radar.PropertyChanged += OnRadarPropertyChanged;
		}

		private void OnRadarPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (_isShown && e.PropertyName == nameof(RadarViewModel.SelectedRadarOption))
			{
				_ = ApplyAsync();
			}
		}

		private bool _isShown;
		/// <summary>Whether the mile grid is shown. Bound to the Map Controls "Mile grid" toggle. With no
		/// radar selected, on = armed but nothing draws until a site is picked.</summary>
		public bool IsShown
		{
			get => _isShown;
			set
			{
				if (SetProperty(ref _isShown, value))
				{
					_ = ApplyAsync();
				}
			}
		}

		/// <summary>Selectable grid spacings, in miles.</summary>
		public IReadOnlyList<int> SpacingOptions { get; } = new[] { 10, 25, 50, 100 };

		private int _spacingMiles = 50;
		/// <summary>Grid spacing in miles (one cell = this many miles square). Re-draws when changed while shown.</summary>
		public int SpacingMiles
		{
			get => _spacingMiles;
			set
			{
				if (SetProperty(ref _spacingMiles, value) && _isShown)
				{
					_ = ApplyAsync();
				}
			}
		}

		private double _opacity = 0.4;
		/// <summary>Grid line opacity (0-1). Bound to the opacity slider.</summary>
		public double Opacity
		{
			get => _opacity;
			set
			{
				if (SetProperty(ref _opacity, value) && _isMapReady)
				{
					_ = _mapService.SetMileGridOpacityAsync(value);
				}
			}
		}

		// Draw the grid at the current site + spacing, or clear it (not shown, or no site to anchor to).
		private Task ApplyAsync()
		{
			if (!_isMapReady)
			{
				return Task.CompletedTask;
			}
			if (_isShown && _radar.SelectedRadarOption?.Site is { } site)
			{
				return _mapService.ShowMileGridAsync(site.Latitude, site.Longitude, _spacingMiles);
			}
			return _mapService.ClearMileGridAsync();
		}

		/// <summary>Marks the map ready and applies the current state (grid + opacity) if it was pre-armed.</summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			await _mapService.SetMileGridOpacityAsync(_opacity);
			await ApplyAsync();
		}
	}
}
