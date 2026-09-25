using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// What of the BASEMAP draws — the tools tier's Map key (show / hide the whole map) and its flyout (which
	/// layer groups, and a dimmer). The overlays are never this VM's business: hide the map, then pick what
	/// shows with the overlay sections you already have. Rendering lives in <c>Assets/Map/js/basemap.js</c>.
	/// </summary>
	/// <remarks>
	/// ⚠️ HIDE OVERRIDES THE TICKS WITHOUT CHANGING THEM — show the map again and exactly your set returns.
	/// ⚠️ HIDE IS SESSION-ONLY (the user's call): a launch onto a blank map reads as broken. The ticks and the
	/// dimmer are persisted.
	/// ⚠️ Every change sends the WHOLE state (one command), and <see cref="OnMapsReadyAsync"/> replays it.
	/// </remarks>
	public sealed class BasemapViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly ISettingsService _settings;
		private bool _isMapReady;

		public BasemapViewModel(IMapService mapService, ISettingsService settings)
		{
			_mapService = mapService;
			_settings = settings;

			var off = new HashSet<string>(BasemapGroups.Normalize(settings.Settings.HiddenBasemapGroups));
			Groups = BasemapGroups.All
				.Select(g => new BasemapGroupOption(g.Id, g.Label, !off.Contains(g.Id), OnGroupsChanged))
				.ToList();
			_dim = Math.Clamp(settings.Settings.BasemapDim, 0, MaxDim);
		}

		/// <summary>The dimmer's ceiling. Never fully blank — that is what the Map key is for.</summary>
		public const double MaxDim = 0.9;

		/// <summary>The flyout's rows, bottom of the map first.</summary>
		public IReadOnlyList<BasemapGroupOption> Groups { get; }

		private bool _isMapShown = true;

		/// <summary>The Map key: lit = the basemap draws; unlit = a blank, theme-coloured ground under the
		/// overlays. NOT persisted.</summary>
		public bool IsMapShown
		{
			get => _isMapShown;
			set
			{
				if (SetProperty(ref _isMapShown, value)) { Push(); }
			}
		}

		private double _dim;

		/// <summary>How far the basemap fades toward the blank ground, 0 to <see cref="MaxDim"/>. Persisted.</summary>
		public double Dim
		{
			get => _dim;
			set
			{
				var v = Math.Clamp(double.IsFinite(value) ? value : 0, 0, MaxDim);
				if (!SetProperty(ref _dim, v)) { return; }
				OnPropertyChanged(nameof(DimPercent));
				_settings.Settings.BasemapDim = v;
				Push();
			}
		}

		/// <summary>The dimmer slider's value, 0–90 (%). Two-way.</summary>
		public double DimPercent
		{
			get => Math.Round(_dim * 100);
			set => Dim = value / 100.0;
		}

		/// <summary>Called by the coordinator whenever the basemap STYLE changes: greys the rows the style has
		/// nothing for (only county lines differ between the bundled five).</summary>
		public void SetStyle(MapStyle? style)
		{
			foreach (var g in Groups)
			{
				g.IsAvailable = g.Id != BasemapGroups.Counties || style?.HasCountyLines == true;
			}
		}

		/// <summary>The unticked group ids, in canonical order — what is stored and what the page gets.</summary>
		public IReadOnlyList<string> OffGroups => Groups.Where(g => !g.IsShown).Select(g => g.Id).ToList();

		private void OnGroupsChanged()
		{
			_settings.Settings.HiddenBasemapGroups = BasemapGroups.Normalize(OffGroups); // a NEW list, so it saves
			Push();
		}

		private void Push()
		{
			if (_isMapReady) { _ = _mapService.SetBasemapAsync(!_isMapShown, OffGroups, _dim); }
		}

		/// <summary>Marks the page ready and replays the state (the page starts on the full basemap).</summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			if (!_isMapShown || _dim > 0 || OffGroups.Count > 0)
			{
				await _mapService.SetBasemapAsync(!_isMapShown, OffGroups, _dim);
			}
		}
	}
}
