using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// The PastCast window's Saved events section: search + a Built-in/Yours filter over the library,
	/// picking an event (or one of its radar legs), saving the current timeframe, and deleting the user's
	/// own events.
	/// </summary>
	/// <remarks>
	/// ⚠️⚠️ <b>PICKING AN EVENT ARMS, IT NEVER LOADS.</b> It writes the leg's window into the Timeframe
	/// pickers, selects the leg's site WITHOUT loading (<see cref="RadarViewModel.SelectReplaySiteWithoutLoading"/>),
	/// and flies the map there. The card then reads "Selection changed — press Load" (or "Not loaded yet"),
	/// and Load does the rest through the unchanged replay path. There is no event mode in the engine.
	/// <para>⚠️ <b>THE HIGHLIGHT CLEARS THE MOMENT THE PICKERS STOP MATCHING</b> — any date/start/window
	/// edit, a different site clicked on the map, or leaving PastCast. The list never points at an event
	/// the Timeframe no longer describes. Our own writes are fenced by <c>_applying</c> so picking an event
	/// doesn't immediately un-pick it.</para>
	/// <para>⚠️ <b>One radar at a time.</b> A multi-leg event is several viewpoints on one storm, but the
	/// replay loop is one site (every pane shares it), so Previous/Next radar re-arm a leg by hand. An
	/// automatic handoff mid-playback would be a multi-site loop — an engine change, and the engine is
	/// parked.</para>
	/// </remarks>
	public sealed class SavedEventsViewModel : ObservableObject
	{
		private readonly ISavedEventLibrary _library;
		private readonly RadarViewModel _radar;
		private readonly IMapService _mapService;

		// The picked event, by id so it survives a list rebuild (search, filter, delete).
		private string? _selectedId;
		private int _selectedLeg;
		private bool _applying;

		// The picker/site properties whose change means the Timeframe no longer describes the picked event.
		private static readonly HashSet<string> SelectionInvalidators = new()
		{
			nameof(RadarViewModel.PastEventYearIndex),
			nameof(RadarViewModel.PastEventMonthIndex),
			nameof(RadarViewModel.PastEventDayIndex),
			nameof(RadarViewModel.PastEventTime),
			nameof(RadarViewModel.PastEventDurationIndex),
			nameof(RadarViewModel.SelectedRadarOption),
			nameof(RadarViewModel.IsPastEventMode),
		};

		public SavedEventsViewModel(ISavedEventLibrary library, RadarViewModel radar, IMapService mapService)
		{
			_library = library;
			_radar = radar;
			_mapService = mapService;
			_radar.PropertyChanged += OnRadarPropertyChanged;
			Rebuild();
		}

		// ── The list ────────────────────────────────────────────────────────────────────────────────

		public ObservableCollection<SavedEventRow> Rows { get; } = new();

		public bool HasRows => Rows.Count > 0;

		public string EmptyText => _library.GetEvents().Count == 0
			? "No saved events yet. Set a timeframe, then save it below."
			: "No events match. Try a site like KTLX or a year.";

		private string _searchText = string.Empty;
		public string SearchText
		{
			get => _searchText;
			set
			{
				if (SetProperty(ref _searchText, value ?? string.Empty))
				{
					Rebuild();
				}
			}
		}

		/// <summary>The filter segments, indexed by <see cref="FilterIndex"/>.</summary>
		public IReadOnlyList<string> FilterLabels { get; } = new[] { "All", "Built-in", "Yours" };

		private int _filterIndex;
		public int FilterIndex
		{
			get => _filterIndex;
			set
			{
				if (SetProperty(ref _filterIndex, Math.Clamp(value, 0, FilterLabels.Count - 1)))
				{
					Rebuild();
				}
			}
		}

		private string _status = string.Empty;
		/// <summary>A pick that could only partly apply (an unknown site, PastCast off). Empty otherwise.</summary>
		public string Status
		{
			get => _status;
			private set => SetProperty(ref _status, value);
		}

		private void Rebuild()
		{
			var query = _searchText.Trim();
			var rows = _library.GetEvents()
				.Where(e => _filterIndex switch { 1 => e.IsBuiltIn, 2 => !e.IsBuiltIn, _ => true })
				.Where(e => query.Length == 0 || Matches(e, query))
				.Select(e => new SavedEventRow(e))
				.ToList();

			Rows.Clear();
			string? lastGroup = null;
			foreach (var row in rows)
			{
				row.ShowGroupHeader = row.GroupLabel != lastGroup;
				lastGroup = row.GroupLabel;
				if (row.Id == _selectedId)
				{
					row.CurrentLegIndex = _selectedLeg;
					row.IsSelected = true;
				}
				Rows.Add(row);
			}

			OnPropertyChanged(nameof(HasRows));
			OnPropertyChanged(nameof(EmptyText));
		}

		private static bool Matches(SavedEvent e, string query)
		{
			bool Has(string? s) => s?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
			return Has(e.Name) || Has(e.Notes)
				|| e.Legs.Any(l => Has(l.SiteId))
				|| Has(e.StartUtc.ToLocalTime().Year.ToString());
		}

		// ── Picking ─────────────────────────────────────────────────────────────────────────────────

		/// <summary>Pick an event: its DEFAULT leg.</summary>
		public void Pick(SavedEventRow row) => Apply(row, row.Event.DefaultLegIndex);

		/// <summary>Pick a specific radar leg of an event.</summary>
		public void PickLeg(SavedEventLegRow leg) => Apply(leg.Owner, leg.Index);

		public void PreviousLeg(SavedEventRow row)
		{
			if (row.CanGoPrevious) Apply(row, row.CurrentLegIndex - 1);
		}

		public void NextLeg(SavedEventRow row)
		{
			if (row.CanGoNext) Apply(row, row.CurrentLegIndex + 1);
		}

		private void Apply(SavedEventRow row, int legIndex)
		{
			if (!_radar.IsPastEventMode)
			{
				// The section lives in the PastCast window, which only exists while the mode is on; guard anyway.
				Status = "Turn on PastCast to use a saved event.";
				return;
			}

			var leg = row.Event.Legs[Math.Clamp(legIndex, 0, row.Event.Legs.Count - 1)];
			_applying = true;
			try
			{
				Select(row.Id, legIndex);
				Status = string.Empty;

				// ⚠️ ORDER: window FIRST, site second. If the site is the same as the loaded one, the moved
				// pickers are what make the card say "press Load"; if it differs, the site seam forgets the
				// loaded window, and it must do so after the pickers hold the new one.
				_radar.ApplyReplayWindow(leg.StartUtc, leg.DurationMinutes);

				if (leg.SiteId is { } siteId)
				{
					var option = _radar.RadarOptions.FirstOrDefault(o =>
						string.Equals(o.Site?.Id, siteId, StringComparison.OrdinalIgnoreCase));
					if (option?.Site is { } site)
					{
						_radar.SelectReplaySiteWithoutLoading(option);
						_ = _mapService.FlyToAsync(site.Longitude, site.Latitude, 7);
					}
					else
					{
						Status = $"{siteId} isn't in the radar site list, so only the timeframe was set.";
					}
				}
			}
			finally
			{
				_applying = false;
			}
		}

		private void Select(string? id, int legIndex)
		{
			_selectedId = id;
			_selectedLeg = legIndex;
			foreach (var row in Rows)
			{
				var on = row.Id == id;
				if (on)
				{
					row.CurrentLegIndex = legIndex;
				}
				row.IsSelected = on;
			}
		}

		private void OnRadarPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (!_applying && _selectedId is not null && e.PropertyName is { } name && SelectionInvalidators.Contains(name))
			{
				Select(null, 0);
			}
		}

		// ── Deleting ────────────────────────────────────────────────────────────────────────────────

		/// <summary>Delete one of the user's own events. Built-ins are refused by the library.</summary>
		public void Remove(SavedEventRow row)
		{
			if (!_library.Remove(row.Id))
			{
				return;
			}
			if (_selectedId == row.Id)
			{
				_selectedId = null;
			}
			Rebuild();
		}

		// ── Saving the current timeframe ────────────────────────────────────────────────────────────

		private bool _isSaveFormOpen;
		public bool IsSaveFormOpen
		{
			get => _isSaveFormOpen;
			private set => SetProperty(ref _isSaveFormOpen, value);
		}

		private string _newEventName = string.Empty;
		public string NewEventName
		{
			get => _newEventName;
			set
			{
				if (SetProperty(ref _newEventName, value ?? string.Empty))
				{
					SaveError = string.Empty;
				}
			}
		}

		private string _newEventNotes = string.Empty;
		public string NewEventNotes
		{
			get => _newEventNotes;
			set => SetProperty(ref _newEventNotes, value ?? string.Empty);
		}

		private string _saveError = string.Empty;
		public string SaveError
		{
			get => _saveError;
			private set => SetProperty(ref _saveError, value);
		}

		private string _saveSummary = string.Empty;
		/// <summary>What will be saved: "KTLX · Fri May 31, 2013 · 5:30 PM → 7:30 PM".</summary>
		public string SaveSummary
		{
			get => _saveSummary;
			private set => SetProperty(ref _saveSummary, value);
		}

		public void OpenSaveForm()
		{
			var site = _radar.SelectedRadarOption?.Site?.Id ?? "No site (window only)";
			SaveSummary = $"{site} · {_radar.PastEventDateText} · {_radar.PastEventRangeText}";
			SaveError = string.Empty;
			IsSaveFormOpen = true;
		}

		public void CancelSave()
		{
			IsSaveFormOpen = false;
			NewEventName = string.Empty;
			NewEventNotes = string.Empty;
			SaveError = string.Empty;
		}

		/// <summary>Save the Timeframe pickers + selected site as a one-leg user event. False (with
		/// <see cref="SaveError"/> set) when it can't be saved.</summary>
		public bool ConfirmSave()
		{
			var leg = new SavedEventLeg(
				_radar.SelectedRadarOption?.Site?.Id,
				_radar.ReplayStartUtc(),
				RadarViewModel.PastEventMinutesByIndex[_radar.PastEventDurationIndex]);

			SavedEvent saved;
			try
			{
				saved = _library.Add(_newEventName, new[] { leg }, _newEventNotes);
			}
			catch (ArgumentException ex)
			{
				SaveError = ex.Message;
				return false;
			}

			CancelSave();

			// The new event describes exactly what the pickers hold, so it's highlighted as picked — without
			// re-applying it. Clear search + filter first, or it could be saved into a view that hides it.
			_selectedId = saved.Id;
			_selectedLeg = 0;
			_searchText = string.Empty;
			_filterIndex = 0;
			OnPropertyChanged(nameof(SearchText));
			OnPropertyChanged(nameof(FilterIndex));
			Rebuild();
			return true;
		}
	}
}
