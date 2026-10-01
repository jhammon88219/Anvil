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
	/// layer groups, as a tree of categories; roads split by kind, names by source). The overlays are never this VM's business: hide the map, then pick
	/// what shows with the overlay sections you already have. Rendering lives in <c>Assets/Map/js/basemap.js</c>.
	/// </summary>
	/// <remarks>
	/// ⚠️ HIDE OVERRIDES THE TICKS WITHOUT CHANGING THEM — show the map again and exactly your set returns.
	/// ⚠️ HIDE IS SESSION-ONLY (the user's call): a launch onto a blank map reads as broken. The ticks are persisted.
	/// ⚠️ Every change sends the WHOLE state (one command), and <see cref="OnMapsReadyAsync"/> replays it.
	/// (The DIMMER that lived here was removed, 2026-10-01 — the user's call.)
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
			Categories = BasemapGroups.Tree
				.Select(c => new BasemapCategory(c.Label, c.Leaves
					.Select(g => new BasemapGroupOption(g.Id, g.Label, !off.Contains(g.Id), OnGroupsChanged))
					.ToList()))
				.ToList();
			Groups = Categories.SelectMany(c => c.Leaves).ToList();
		}

		/// <summary>The flyout's TREE: its top-level rows, bottom of the map first (an expander per multi-group category).</summary>
		public IReadOnlyList<BasemapCategory> Categories { get; }

		/// <summary>Every group (the tree's leaves), flattened in flyout order.</summary>
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
			if (_isMapReady) { _ = _mapService.SetBasemapAsync(!_isMapShown, OffGroups); }
		}

		/// <summary>Marks the page ready and replays the state (the page starts on the full basemap).</summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			if (!_isMapShown || OffGroups.Count > 0)
			{
				await _mapService.SetBasemapAsync(!_isMapShown, OffGroups);
			}
		}
	}
}
