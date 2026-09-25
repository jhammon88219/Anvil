using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// The saved-event library, shown TWICE: the PastCast window's Saved events section (search, the list,
	/// picking an event or one of its radar legs, saving the current timeframe, deleting your own) and the
	/// Anvil Atlas's Past events tab (search + a by-type filter — All / Tornado / Hurricane / Derecho / Custom —
	/// the counts in the title band, a browsing detail, Play in PastCast with its replace question, and
	/// editing your own events' type + key times). One list, one search, one filter — the two views agree.
	/// </summary>
	/// <remarks>
	/// ⚠️⚠️ <b>PICKING AN EVENT LOADS IT.</b> It writes the leg's window into the Timeframe pickers, flies the
	/// map to the leg's site (or pins + flies to the town the event is named for — see <c>Apply</c>), and loads through <see cref="RadarViewModel.LoadReplayAtSiteAsync"/> — the same
	/// replay path Load uses, exactly once. There is no event mode in the engine. (It armed-only at first;
	/// changed by request 2026-09-12 — a pick is a request to watch.)
	/// <para>⚠️ <b>THE HIGHLIGHT CLEARS THE MOMENT THE PICKERS STOP MATCHING</b> — any date/start/window
	/// edit, a different site clicked on the map, or leaving PastCast. The list never points at an event
	/// the Timeframe no longer describes. Our own writes are fenced by <c>_applying</c> so picking an event
	/// doesn't immediately un-pick it.</para>
	/// <para>⚠️ <b>One radar at a time.</b> A multi-leg event is several viewpoints on one storm, but the
	/// replay loop is one site (every pane shares it), so Previous/Next radar re-arm a leg by hand. An
	/// automatic handoff mid-playback would be a multi-site loop — an engine change, and the engine is
	/// parked.</para>
	/// <para>⚠️ Saving does NOT load — the new event already describes what the pickers hold.</para>
	/// </remarks>
	public sealed class SavedEventsViewModel : ObservableObject
	{
		private readonly ISavedEventLibrary _library;
		private readonly RadarViewModel _radar;
		private readonly IMapService _mapService;
		private readonly PlaceSearchViewModel _places;

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

		public SavedEventsViewModel(ISavedEventLibrary library, RadarViewModel radar, IMapService mapService,
			PlaceSearchViewModel places)
		{
			_library = library;
			_radar = radar;
			_mapService = mapService;
			_places = places;
			_radar.PropertyChanged += OnRadarPropertyChanged;
			Rebuild();
		}

		// ── The list ────────────────────────────────────────────────────────────────────────────────

		public ObservableCollection<SavedEventRow> Rows { get; } = new();

		/// <summary>The SAME rows as <see cref="Rows"/>, sectioned by type for the Atlas's grouped list
		/// ("Tornado · 10"). The PastCast list draws its headers inline instead (<see cref="SavedEventRow.ShowGroupHeader"/>).</summary>
		public ObservableCollection<SavedEventGroup> Groups { get; } = new();

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
		/// ⚠️ Indices 1-3 are one <see cref="SavedEventKind"/> each — built-in AND custom, since a custom event
		/// has a type too — and 4 is every custom event; see <see cref="PassesFilter"/>.
		public IReadOnlyList<string> FilterLabels { get; } = new[] { "All", "Tornado", "Hurricane", "Derecho", "Custom" };

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

		// ⚠️ Set while Rows is cleared and refilled: the Atlas ListView echoes a null selection when its item
		// goes away, which must not read as the user deselecting (the id restores it on the new row).
		private bool _rebuilding;

		private void Rebuild()
		{
			var all = _library.GetEvents();
			var query = _searchText.Trim();
			var rows = all
				.Where(PassesFilter)
				.Where(e => query.Length == 0 || Matches(e, query))
				.Select(e => new SavedEventRow(e, SiteName))
				.ToList();
			var groupCounts = rows.GroupBy(r => r.GroupLabel).ToDictionary(g => g.Key, g => g.Count());

			SavedEventRow? atlas = null;
			_rebuilding = true;
			try
			{
				Rows.Clear();
				Groups.Clear();
				foreach (var group in rows.GroupBy(r => r.GroupLabel))
				{
					Groups.Add(new SavedEventGroup($"{group.Key} · {group.Count()}", group));
				}
				string? lastGroup = null;
				foreach (var row in rows)
				{
					row.ShowGroupHeader = row.GroupLabel != lastGroup;
					row.GroupHeader = $"{row.GroupLabel} · {groupCounts[row.GroupLabel]}";
					lastGroup = row.GroupLabel;
					if (row.Id == _selectedId)
					{
						row.CurrentLegIndex = _selectedLeg;
						row.IsSelected = true;
					}
					if (row.Id == _atlasId)
					{
						row.DetailLegIndex = _atlasLeg;
						atlas = row;
					}
					Rows.Add(row);
				}
			}
			finally
			{
				_rebuilding = false;
			}

			// The detail keeps its event through a search that hides it (like the site tab keeps the loaded
			// site) — on a FRESH row, so an edit made meanwhile shows. A deleted event clears it.
			if (atlas is null && _atlasId is not null && all.FirstOrDefault(e => e.Id == _atlasId) is { } hidden)
			{
				atlas = new SavedEventRow(hidden, SiteName) { DetailLegIndex = _atlasLeg };
			}
			SetAtlasSelection(atlas);

			TornadoCount = all.Count(e => e.Kind == SavedEventKind.Tornado);
			HurricaneCount = all.Count(e => e.Kind == SavedEventKind.Hurricane);
			DerechoCount = all.Count(e => e.Kind == SavedEventKind.Derecho);
			CustomCount = all.Count(e => !e.IsBuiltIn);
			TotalCount = all.Count;

			OnPropertyChanged(nameof(HasRows));
			OnPropertyChanged(nameof(EmptyText));
		}

		// "Norman · OK" for a leg's site, from the radar site list — the Atlas leg rows name the place.
		private string SiteName(string? siteId)
		{
			var site = siteId is null ? null : _radar.RadarOptions.FirstOrDefault(o =>
				string.Equals(o.Site?.Id, siteId, StringComparison.OrdinalIgnoreCase))?.Site;
			return site is null ? string.Empty : string.IsNullOrEmpty(site.State) ? site.Name : $"{site.Name} · {site.State}";
		}

		private bool PassesFilter(SavedEvent e) => _filterIndex switch
		{
			1 => e.Kind == SavedEventKind.Tornado,
			2 => e.Kind == SavedEventKind.Hurricane,
			3 => e.Kind == SavedEventKind.Derecho,
			4 => !e.IsBuiltIn,
			_ => true,
		};

		// ── The Atlas title band's counts (the WHOLE library, never the filtered list) ──────────────
		// A custom event counts in its kind's number too; "custom" is a tag, not a fourth kind.

		private int _tornadoCount, _hurricaneCount, _derechoCount, _customCount, _totalCount;
		public int TornadoCount { get => _tornadoCount; private set => SetProperty(ref _tornadoCount, value); }
		public int HurricaneCount { get => _hurricaneCount; private set => SetProperty(ref _hurricaneCount, value); }
		public int DerechoCount { get => _derechoCount; private set => SetProperty(ref _derechoCount, value); }
		public int CustomCount { get => _customCount; private set => SetProperty(ref _customCount, value); }
		public int TotalCount { get => _totalCount; private set => SetProperty(ref _totalCount, value); }

		private static bool Matches(SavedEvent e, string query)
		{
			bool Has(string? s) => s?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
			return Has(e.Name) || Has(e.Notes)
				|| (e.Kind != SavedEventKind.Other && Has(e.Kind.ToString()))
				|| e.Legs.Any(l => Has(l.SiteId))
				|| Has(e.StartUtc.ToLocalTime().Year.ToString());
		}

		// ── Picking ─────────────────────────────────────────────────────────────────────────────────

		// Framing for the town an event is named for: the storm's surroundings with the radar site in view,
		// not the town's own close-up search zoom.
		private const double TownZoom = 8;

		/// <summary>Pick an event: its DEFAULT leg, and pin the town it is named for.</summary>
		public void Pick(SavedEventRow row) => Apply(row, row.Event.DefaultLegIndex, pinTown: true);

		/// <summary>Pick a specific radar leg of an event.</summary>
		public void PickLeg(SavedEventLegRow leg) => Apply(leg.Owner, leg.Index, pinTown: false);

		public void PreviousLeg(SavedEventRow row)
		{
			if (row.CanGoPrevious) Apply(row, row.CurrentLegIndex - 1, pinTown: false);
		}

		public void NextLeg(SavedEventRow row)
		{
			if (row.CanGoNext) Apply(row, row.CurrentLegIndex + 1, pinTown: false);
		}

		/// <remarks>⚠️ <b>THE TOWN PIN</b> (whole-event picks only): the event's name goes through
		/// <see cref="PlaceSearchViewModel.ShowNamedPlaceAsync"/> — the search box's own pin, name in the box, so
		/// the box's X removes it. The map flies to the site first; a pin that lands re-flies to the TOWN. A name
		/// that isn't a town (hurricanes, derechos, most of the user's own) pins nothing. A leg switch flies
		/// to its site and leaves the pin where it is.</remarks>
		private async void Apply(SavedEventRow row, int legIndex, bool pinTown)
		{
			if (!_radar.IsPastEventMode)
			{
				// The section lives in the PastCast window, which only exists while the mode is on; guard anyway.
				Status = "Turn on PastCast to use a saved event.";
				return;
			}

			var leg = row.Event.Legs[Math.Clamp(legIndex, 0, row.Event.Legs.Count - 1)];
			RadarOption? option = null;
			Task<bool> load;

			_applying = true;
			try
			{
				Select(row.Id, legIndex);
				Status = string.Empty;

				// ⚠️ ORDER: window FIRST, then site + load. The load reads the pickers, so they must already
				// hold this leg's window when it starts.
				_radar.ApplyReplayWindow(leg.StartUtc, leg.DurationMinutes);

				if (leg.SiteId is { } siteId)
				{
					option = _radar.RadarOptions.FirstOrDefault(o =>
						string.Equals(o.Site?.Id, siteId, StringComparison.OrdinalIgnoreCase));
					if (option?.Site is null)
					{
						Status = $"{siteId} isn't in the radar site list, so the timeframe loaded at the current site.";
					}
				}

				// ⚠️ Started INSIDE the fence: the site change it makes is raised synchronously, before the
				// first await, and must not read as the user moving away from the event.
				load = _radar.LoadReplayAtSiteAsync(option);
			}
			finally
			{
				_applying = false;
			}

			try
			{
				// The site flight goes first so the camera never waits on a town lookup that may go online; a
				// town pin that lands then re-flies to the town.
				if (option?.Site is { } site)
				{
					_ = _mapService.FlyToAsync(site.Longitude, site.Latitude, 7);
				}
				if (pinTown)
				{
					await _places.ShowNamedPlaceAsync(row.Event.Name, TownZoom);
				}
				await load;
			}
			catch (Exception ex)
			{
				// async void: an escaped exception would take the app down. The card's footer carries the
				// load's own failures; this is only for the unexpected.
				Status = $"Couldn't load this event: {ex.Message}";
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

		// ── The Atlas: browsing (selecting does NOT load) ───────────────────────────────────────────
		// ⚠️ The Atlas SELECTION is not the PastCast list's PICK. Selecting a row there only fills the detail;
		// Play in PastCast is the one thing that loads (through the same Apply as a pick).

		private string? _atlasId;
		private int _atlasLeg;
		private SavedEventRow? _atlasSelection;

		/// <summary>The event the Atlas detail shows. Two-way with the Past events list.</summary>
		/// <remarks>⚠️ A null write DURING a rebuild is the ListView echoing its cleared items, not the user —
		/// ignored, and the id puts the selection back on the new row.</remarks>
		public SavedEventRow? AtlasSelection
		{
			get => _atlasSelection;
			set
			{
				if (_rebuilding || (value is null && _atlasSelection is not null && !Rows.Contains(_atlasSelection)))
				{
					return;
				}
				SetAtlasSelection(value);
			}
		}

		private void SetAtlasSelection(SavedEventRow? row)
		{
			var changed = !ReferenceEquals(row, _atlasSelection);
			var sameEvent = row?.Id == _atlasId;
			_atlasSelection = row;
			_atlasId = row?.Id;
			_atlasLeg = row?.DetailLegIndex ?? 0;
			if (!sameEvent)
			{
				CancelEdit();
				CancelReplace();
			}
			if (changed)
			{
				OnPropertyChanged(nameof(AtlasSelection));
				OnPropertyChanged(nameof(HasAtlasSelection));
				OnPropertyChanged(nameof(EditKindIndex));
			}
		}

		public bool HasAtlasSelection => _atlasSelection is not null;

		/// <summary>Show one leg in the Atlas detail (its tiles). Browsing only — nothing loads.</summary>
		public void ShowAtlasLeg(SavedEventLegRow leg)
		{
			if (!ReferenceEquals(leg.Owner, _atlasSelection))
			{
				return;
			}
			leg.Owner.DetailLegIndex = leg.Index;
			_atlasLeg = leg.Index;
			CancelEdit();
			CancelReplace();
		}

		/// <summary>Select an event in the Atlas by id (the PastCast section's "Open in Atlas" lands on the
		/// picked one). Clears the search/filter first if they would hide it.</summary>
		public void SelectInAtlas(string? id)
		{
			if (id is null || _library.GetEvents().All(e => e.Id != id))
			{
				return;
			}
			if (Rows.All(r => r.Id != id))
			{
				_searchText = string.Empty;
				_filterIndex = 0;
				OnPropertyChanged(nameof(SearchText));
				OnPropertyChanged(nameof(FilterIndex));
			}
			_atlasId = id;
			_atlasLeg = id == _selectedId ? _selectedLeg : _library.GetEvents().First(e => e.Id == id).DefaultLegIndex;
			Rebuild();
		}

		/// <summary>The picked event's id, if any — what "Open in Atlas" should land on.</summary>
		public string? PickedId => _selectedId;

		// ── The Atlas: Play in PastCast, and its replace question ───────────────────────────────────
		// ⚠️ PLAY ASKS FIRST only when PastCast already holds a DIFFERENT replay (another window or site). The
		// question replaces the Play button in place — no dialog over the Atlas's own window. The mode switch
		// + closing the Atlas belong to MapViewModel.PlayAtlasEvent, which owns both.

		private bool _isConfirmingReplace;
		public bool IsConfirmingReplace
		{
			get => _isConfirmingReplace;
			private set => SetProperty(ref _isConfirmingReplace, value);
		}

		private string _replacePrompt = string.Empty;
		/// <summary>"PastCast is playing Joplin, MO (May 22, 2011, KSGF). Replace it?"</summary>
		public string ReplacePrompt
		{
			get => _replacePrompt;
			private set => SetProperty(ref _replacePrompt, value);
		}

		private string _keepLabel = "Keep it";
		/// <summary>"Keep Joplin, MO" — the question's decline button.</summary>
		public string KeepLabel
		{
			get => _keepLabel;
			private set => SetProperty(ref _keepLabel, value);
		}

		/// <summary>Whether playing the Atlas selection would REPLACE a different replay PastCast holds.</summary>
		public bool NeedsReplaceConfirm()
		{
			if (_atlasSelection is not { } row || !_radar.IsPastEventMode || !_radar.HasLoadedReplayWindow
				|| _radar.LoadedReplayStartUtc is not { } loadedStart)
			{
				return false;
			}
			var leg = row.DetailLeg.Leg;
			var loadedSite = _radar.SelectedRadarOption?.Site?.Id;
			return loadedStart != leg.StartUtc
				|| !string.Equals(loadedSite, leg.SiteId, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>Raise the replace question, naming what is loaded now.</summary>
		public void ShowReplaceConfirm()
		{
			var loadedStart = _radar.LoadedReplayStartUtc;
			var site = _radar.SelectedRadarOption?.Site?.Id;
			var date = loadedStart is { } s ? s.ToLocalTime().ToString("MMM d, yyyy") : "an earlier date";
			var detail = site is null ? date : $"{date}, {site}";
			var playing = _selectedId is not null
				? _library.GetEvents().FirstOrDefault(e => e.Id == _selectedId)?.Name
				: null;
			ReplacePrompt = playing is not null
				? $"PastCast is playing {playing} ({detail}). Replace it?"
				: $"PastCast is playing a replay ({detail}). Replace it?";
			KeepLabel = playing is not null ? $"Keep {playing}" : "Keep it";
			IsConfirmingReplace = true;
		}

		public void CancelReplace() => IsConfirmingReplace = false;

		/// <summary>Load the Atlas selection's shown leg, exactly as a pick does. PastCast must already be on
		/// (MapViewModel.PlayAtlasEvent turns it on first). The town pin comes with the event's default leg only,
		/// same rule as <see cref="Pick"/> vs <see cref="PickLeg"/>.</summary>
		public void PlayAtlasSelection()
		{
			CancelReplace();
			if (_atlasSelection is { } row)
			{
				Apply(row, row.DetailLegIndex, pinTown: row.DetailLegIndex == row.Event.DefaultLegIndex);
			}
		}

		// ── The Atlas: editing your own event (its type + the shown leg's key time) ─────────────────
		// ⚠️ Times are LOCAL wall-clock (the Timeframe pickers' frame). A time of day is placed on the date that
		// puts it nearest the leg's window (a window can cross midnight); the end is the first such time after
		// the start. The library then checks it overlaps the window (SavedEventLibrary.ValidateKey).

		private bool _isEditing;
		public bool IsEditing
		{
			get => _isEditing;
			private set => SetProperty(ref _isEditing, value);
		}

		/// <summary>The type picker's labels (Tornado / Hurricane / Derecho), indexing <see cref="EditKindIndex"/>
		/// and <see cref="NewEventKindIndex"/>.</summary>
		public IReadOnlyList<string> KindLabels { get; } = SavedEventKinds.Pickable.Select(SavedEventKinds.Label).ToList();

		private int _editKindIndex;
		public int EditKindIndex
		{
			get => _editKindIndex;
			set => SetProperty(ref _editKindIndex, Math.Clamp(value, 0, KindLabels.Count - 1));
		}

		private TimeSpan? _editKeyStart;
		public TimeSpan? EditKeyStart
		{
			get => _editKeyStart;
			set { if (SetProperty(ref _editKeyStart, value)) EditError = string.Empty; }
		}

		private TimeSpan? _editKeyEnd;
		public TimeSpan? EditKeyEnd
		{
			get => _editKeyEnd;
			set { if (SetProperty(ref _editKeyEnd, value)) EditError = string.Empty; }
		}

		private string _editError = string.Empty;
		public string EditError
		{
			get => _editError;
			private set => SetProperty(ref _editError, value);
		}

		/// <summary>Open the editor on the Atlas selection (custom events only).</summary>
		public void BeginEdit()
		{
			if (_atlasSelection is not { IsCustom: true } row)
			{
				return;
			}
			CancelReplace();
			var kind = SavedEventKinds.Pickable.ToList().IndexOf(row.Event.Kind);
			EditKindIndex = kind < 0 ? 0 : kind;
			var key = row.DetailLeg.Leg.Key;
			EditKeyStart = key?.StartUtc.ToLocalTime().TimeOfDay;
			EditKeyEnd = key?.EndUtc?.ToLocalTime().TimeOfDay;
			EditError = string.Empty;
			IsEditing = true;
		}

		public void CancelEdit()
		{
			IsEditing = false;
			EditError = string.Empty;
		}

		/// <summary>Commit the editor. False (with <see cref="EditError"/>) when it can't be saved. No start
		/// time = clear the leg's key time.</summary>
		public bool SaveEdit()
		{
			if (_atlasSelection is not { IsCustom: true } row)
			{
				return false;
			}
			var leg = row.DetailLeg.Leg;
			SavedEventKey? key = null;
			if (_editKeyStart is { } startOfDay)
			{
				var start = PlaceNear(startOfDay, leg.StartUtc, leg.EndUtc);
				DateTimeOffset? end = null;
				if (_editKeyEnd is { } endOfDay)
				{
					var candidate = OnLocalDate(start.ToLocalTime().Date, endOfDay);
					end = candidate <= start ? OnLocalDate(start.ToLocalTime().Date.AddDays(1), endOfDay) : candidate;
				}
				key = new SavedEventKey(start, end, leg.Key?.Place ?? string.Empty);
			}
			else if (_editKeyEnd is not null)
			{
				EditError = "Enter a start time, or clear both.";
				return false;
			}

			try
			{
				_library.SetLegKey(row.Id, row.DetailLegIndex, key);
			}
			catch (ArgumentException ex)
			{
				EditError = ex.Message;
				return false;
			}
			_library.SetKind(row.Id, SavedEventKinds.Pickable[_editKindIndex]);

			IsEditing = false;
			Rebuild();
			return true;
		}

		// A local time of day placed on the date (window start's day, the day before or after) nearest the window.
		internal static DateTimeOffset PlaceNear(TimeSpan timeOfDay, DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc)
		{
			var day = windowStartUtc.ToLocalTime().Date;
			return new[] { -1, 0, 1 }
				.Select(d => OnLocalDate(day.AddDays(d), timeOfDay))
				.OrderBy(t => t < windowStartUtc ? windowStartUtc - t : t > windowEndUtc ? t - windowEndUtc : TimeSpan.Zero)
				.First();
		}

		private static DateTimeOffset OnLocalDate(DateTime localDate, TimeSpan timeOfDay)
		{
			var local = DateTime.SpecifyKind(localDate.Date + timeOfDay, DateTimeKind.Unspecified);
			return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
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

		private int _newEventKindIndex;
		/// <summary>The new event's type (into <see cref="KindLabels"/>) — every custom event is typed, so it
		/// lands in its type's group in the Atlas. Kept between saves (a storm chaser saves tornadoes).</summary>
		public int NewEventKindIndex
		{
			get => _newEventKindIndex;
			set => SetProperty(ref _newEventKindIndex, Math.Clamp(value, 0, KindLabels.Count - 1));
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
				saved = _library.Add(_newEventName, new[] { leg }, _newEventNotes, SavedEventKinds.Pickable[_newEventKindIndex]);
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
