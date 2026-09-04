using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// "State Isolation" mode: when armed, hovering a US state on the map highlights it (bold outline +
	/// pointer cursor), and clicking it isolates it — everything outside the chosen state is covered with
	/// the basemap's water color, leaving a clean single-state view. A state can also be picked directly by
	/// name (<see cref="SelectedState"/>) from the Map Controls card's combo. Turning the mode off restores
	/// the full map. A building block for the planned NowCast/stream mode.
	///
	/// The hover/click + inverted-mask rendering all live in the WebView (Assets/Map/js/states.js); this VM
	/// arms/disarms the mode and drives the direct pick through <see cref="IMapService"/>, and tracks which
	/// state the WebView reports as isolated. Surfaced in the Map Controls card (basemap style + isolation).
	/// </summary>
	public sealed class StateIsolationViewModel : ObservableObject
	{
		private readonly IMapService _mapService;

		// Readiness guard: JS commands only run once the map page has reported 'mapReady'. A toggle flipped
		// before then is remembered and applied in OnMapsReadyAsync (mirrors the other subsystem VMs).
		private bool _isMapReady;

		// Guards SelectedState's setter from echoing an isolate command back when the value is arriving FROM
		// the WebView (a map click / a clear) rather than from the combo.
		private bool _applyingFromSystem;

		public StateIsolationViewModel(IMapService mapService)
		{
			_mapService = mapService;
			IsolationOptions = BuildOptions(States);
			RefreshSelectedOption(); // CONUS is the launch default, so the combo opens reading it
		}

		private bool _isConusIsolated = true;

		/// <summary>Base map extent: true = mask everything outside CONUS (the contiguous 48 + DC), false =
		/// the full map. The launch default (on) — this app is CONUS-only for now. Independent of single-state
		/// isolation (an isolated state overrides it until cleared, then the view falls back to this). Bound
		/// to the "CONUS only" toggle in the Map Controls card.</summary>
		public bool IsConusIsolated
		{
			get => _isConusIsolated;
			set
			{
				if (!SetProperty(ref _isConusIsolated, value)) { return; }
				if (_isMapReady) { _ = _mapService.SetConusIsolationAsync(value); }
				RefreshSelectedOption();
			}
		}

		private bool _isArmed;

		/// <summary>Whether State Isolation mode is on. Armed = hover-to-highlight + click-to-isolate on the
		/// map (and the state combo is live); off = the full map (also drops any active isolation). Bound to
		/// the "Isolate a state" toggle in the Map Controls card.</summary>
		public bool IsArmed
		{
			get => _isArmed;
			set
			{
				if (!SetProperty(ref _isArmed, value)) { return; }
				if (!value) { SetSelectedFromSystem(null); } // leaving the mode → full map, clear the pick
				if (_isMapReady) { _ = _mapService.SetStateIsolationAsync(value); }
				RefreshSelectedOption();
			}
		}

		/// <summary>The isolatable regions — the 52 features in <c>Assets/Map/state-boundaries.geojson</c>
		/// (50 states + DC + Puerto Rico), by name, alphabetical (the combo's items). ⚠️ Must match the
		/// geojson's <c>properties.name</c> exactly — states.js finds the polygon by name.</summary>
		public IReadOnlyList<string> States { get; } = new[]
		{
			"Alabama", "Alaska", "Arizona", "Arkansas", "California", "Colorado", "Connecticut", "Delaware",
			"District of Columbia", "Florida", "Georgia", "Hawaii", "Idaho", "Illinois", "Indiana", "Iowa",
			"Kansas", "Kentucky", "Louisiana", "Maine", "Maryland", "Massachusetts", "Michigan", "Minnesota",
			"Mississippi", "Missouri", "Montana", "Nebraska", "Nevada", "New Hampshire", "New Jersey",
			"New Mexico", "New York", "North Carolina", "North Dakota", "Ohio", "Oklahoma", "Oregon",
			"Pennsylvania", "Puerto Rico", "Rhode Island", "South Carolina", "South Dakota", "Tennessee",
			"Texas", "Utah", "Vermont", "Virginia", "Washington", "West Virginia", "Wisconsin", "Wyoming"
		};

		private string? _selectedState;

		/// <summary>The isolated state's name, or null if none is isolated (armed-but-not-yet-picked, or the
		/// mode is off). Two-way bound to the combo: setting it from the combo isolates that state (arming
		/// the mode if needed); a map click / clear updates it from the WebView WITHOUT re-issuing a command.</summary>
		public string? SelectedState
		{
			get => _selectedState;
			set
			{
				if (!SetProperty(ref _selectedState, value)) { return; }
				OnPropertyChanged(nameof(IsolatedStateName));
				OnPropertyChanged(nameof(HasIsolatedState));
				// ⚠️ This is the line that makes a MAP CLICK show up in the combo: states.js reports the
				// isolated state, SetSelectedFromSystem lands it here, and the combo re-resolves to it.
				RefreshSelectedOption();
				if (_applyingFromSystem) { return; } // came from the WebView — don't echo the command back
				if (!string.IsNullOrEmpty(value)) { _ = IsolateStateAsync(value!); }
			}
		}

		// Set SelectedState from the WebView (map click / clear) without re-issuing an isolate command.
		private void SetSelectedFromSystem(string? name)
		{
			_applyingFromSystem = true;
			SelectedState = name;
			_applyingFromSystem = false;
		}

		/// <summary>Alias of <see cref="SelectedState"/> for readouts / the router doc (the WebView-reported
		/// isolated state).</summary>
		public string? IsolatedStateName => _selectedState;

		/// <summary>Whether a state is currently isolated. Drives a future "Isolated: Texas" readout.</summary>
		public bool HasIsolatedState => !string.IsNullOrEmpty(_selectedState);

		/// <summary>Isolate a state by name (arms the mode if needed). Used by the combo and available for a
		/// future stream-mode preset; the on-map click path goes straight through the WebView.</summary>
		public Task IsolateStateAsync(string name) =>
			_isMapReady ? _mapService.SelectIsolatedStateAsync(name) : Task.CompletedTask;

		/// <summary>
		/// Drop any single-state isolation and leave hover mode, from whatever combination is on now.
		/// </summary>
		/// <remarks>
		/// ⚠️ IT CANNOT BE WRITTEN AS <c>IsArmed = false</c>. That setter clears the isolated state as a side
		/// effect, but only when arming actually CHANGES — and since a list pick no longer arms, the common
		/// case is isolated-but-not-armed, where the assignment is a no-op and the state would survive
		/// "No Isolation". The two JS entry points differ the same way: disarm() clears isolation as part of
		/// leaving hover mode, clear() is the not-armed equivalent.
		/// </remarks>
		private async Task ResetIsolationAsync()
		{
			bool wasArmed = _isArmed;
			if (wasArmed)
			{
				_isArmed = false;          // the field, so the setter's own clear path can't double-fire
				OnPropertyChanged(nameof(IsArmed));
			}

			SetSelectedFromSystem(null);   // forget the state without echoing a command back

			if (!_isMapReady) { return; }
			await (wasArmed
				? _mapService.SetStateIsolationAsync(false)   // disarm() — also clears the isolated state
				: _mapService.ClearStateIsolationAsync());    // clear()  — same, without the hover teardown
		}

		/// <summary>The WebView reports which state is isolated (name), or that isolation cleared (null).
		/// Routed from the <c>stateIsolated</c> message; updates the combo without echoing a command.</summary>
		public void OnStateIsolated(string? name) => SetSelectedFromSystem(name);

		// ── The ONE combo on the map controls strip ──────────────────────────────────────────────────
		// Three controls collapsed into one list: the CONUS checkbox, the "isolate a state (hover & click)"
		// checkbox, and the state-name combo that used to sit in the Settings window's Map tab. The rows
		// above the states are ACTIONS and carry hover text; the states explain themselves.

		/// <summary>Every row of the isolation combo: the three actions, then the 52 places.</summary>
		public IReadOnlyList<StateIsolationOption> IsolationOptions { get; }

		private static IReadOnlyList<StateIsolationOption> BuildOptions(IReadOnlyList<string> states)
		{
			var rows = new List<StateIsolationOption>
			{
				new(StateIsolationKind.None, "No Isolation", "Show the whole map"),
				new(StateIsolationKind.Conus, "Isolate CONUS", "Mask everything outside the lower 48"),
				new(StateIsolationKind.Arm, "Select to Isolate", "Then click a state on the map"),
			};
			rows.AddRange(states.Select((n, i) => new StateIsolationOption(StateIsolationKind.State, n, startsStateList: i == 0)));
			return rows;
		}

		// Set while a pick is being APPLIED, so the cascade of property changes it causes (IsArmed clearing
		// SelectedState, and so on) cannot fight the selection that started it. The option is re-resolved
		// once, from the settled state, when the flag drops.
		private bool _applyingOption;

		private StateIsolationOption? _selectedIsolationOption;

		/// <summary>
		/// The picker's selection — always one of the four real states of this subsystem: no isolation,
		/// CONUS, armed-and-waiting, or one named state.
		/// </summary>
		/// <remarks>
		/// ⚠️ IT IS DERIVED, NOT STORED. The truth is <see cref="IsConusIsolated"/> / <see cref="IsArmed"/> /
		/// <see cref="SelectedState"/>; this resolves them into one row (see <see cref="ResolveOption"/>) and
		/// is re-resolved whenever any of the three moves — including a MAP CLICK, which is how arming then
		/// clicking Oklahoma leaves the picker reading "Oklahoma".
		/// ⚠️ Arming OUTRANKS CONUS in the readout: while armed with nothing picked it reads "Select to
		/// Isolate", because the mode you are in is the more useful thing to report.
		/// ⚠️ It is never null, so the picker's placeholder is a fallback that should not be reachable.
		/// </remarks>
		public StateIsolationOption? SelectedIsolationOption
		{
			get => _selectedIsolationOption;
			set
			{
				if (!SetProperty(ref _selectedIsolationOption, value)) { return; }
				if (_applyingOption || value is null) { return; }

				_applyingOption = true;
				try
				{
					switch (value.Kind)
					{
						case StateIsolationKind.None:
							_ = ResetIsolationAsync();
							IsConusIsolated = false;
							break;
						case StateIsolationKind.Conus:
							// ⚠️ The reset is what makes this work from an isolated state: a state OVERRIDES the
							// base extent, so switching to CONUS has to drop it or nothing visibly changes.
							_ = ResetIsolationAsync();
							IsConusIsolated = true;
							break;
						case StateIsolationKind.Arm:
							IsArmed = true;           // hover mode; the state arrives on the map click
							break;
						case StateIsolationKind.State:
							SelectedState = value.Label;
							break;
					}
				}
				finally
				{
					_applyingOption = false;
				}

				RefreshSelectedOption(); // settle on what the three flags actually say now
			}
		}

		// Which row the CURRENT state of the three flags corresponds to (null = nothing masked).
		private StateIsolationOption? ResolveOption()
		{
			if (HasIsolatedState)
			{
				return IsolationOptions.FirstOrDefault(o => o.Kind == StateIsolationKind.State && o.Label == _selectedState);
			}

			if (_isArmed) { return IsolationOptions.First(o => o.Kind == StateIsolationKind.Arm); }
			if (_isConusIsolated) { return IsolationOptions.First(o => o.Kind == StateIsolationKind.Conus); }

			// ⚠️ "No Isolation" IS A STATE THE PICKER RESTS IN, not just a row you press. It used to resolve
			// to null here, which showed the placeholder — so picking "No Isolation" left the box reading
			// "Isolate State" and looked as though the pick had not taken.
			return IsolationOptions.First(o => o.Kind == StateIsolationKind.None);
		}

		// Push the resolved row into the combo without re-running the setter's apply branch.
		private void RefreshSelectedOption()
		{
			if (_applyingOption) { return; }

			var resolved = ResolveOption();
			if (ReferenceEquals(resolved, _selectedIsolationOption)) { return; }

			_applyingOption = true;
			try
			{
				SelectedIsolationOption = resolved;
			}
			finally
			{
				_applyingOption = false;
			}
		}

		/// <summary>Marks the map page ready and applies the initial map extent (CONUS by default) plus any
		/// pre-ready armed state.</summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			if (_isConusIsolated) { await _mapService.SetConusIsolationAsync(true); } // launch default: CONUS
			if (_isArmed) { await _mapService.SetStateIsolationAsync(true); }

			// ⚠⚠ REPLAY THE ISOLATED STATE TOO. Every command here is dropped while the page is not ready
			// (see the _isMapReady guards), so ANY state this VM holds must be re-applied when it becomes
			// ready — and this one was missing, which is a silent, INTERMITTENT "nothing happened".
			// ⚠️ It only became reachable when isolation MOVED to the map controls strip: the strip is on
			// screen from launch, so a pick can land before mapReady. From the Settings window you had to
			// open the window first, by which time the page was long ready — which is exactly why this
			// looked like a regression in code that had not changed.
			// ⚠️ Add a command to this VM = add its replay here. The guard is not optional and neither is
			// the replay; dropping a command with no way back is the whole bug.
			if (!string.IsNullOrEmpty(_selectedState))
			{
				await _mapService.SelectIsolatedStateAsync(_selectedState!);
			}
		}
	}
}
