using System.Threading.Tasks;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// "State Isolation" mode: when armed, hovering a US state on the map highlights it (bold outline +
	/// pointer cursor), and clicking it isolates it — everything outside the chosen state is covered with
	/// the basemap's water color, leaving a clean single-state view. Turning the mode off restores the full
	/// map. A building block for the planned NowCast/stream mode (a clean single-state frame for streamers).
	///
	/// The hover/click + inverted-mask rendering all live in the WebView (Assets/Map/js/states.js); this VM
	/// only arms/disarms the mode through <see cref="IMapService"/> and tracks which state the WebView
	/// reports as isolated. Bound to the "Isolate" toggle on the global top bar (an app-wide mode, like the
	/// Sites / Settings buttons — not a temporal or per-pane concern).
	/// </summary>
	public sealed class StateIsolationViewModel : ObservableObject
	{
		private readonly IMapService _mapService;

		// Readiness guard: JS commands only run once the map page has reported 'mapReady'. A toggle flipped
		// before then is remembered and applied in OnMapsReadyAsync (mirrors the other subsystem VMs).
		private bool _isMapReady;

		public StateIsolationViewModel(IMapService mapService)
		{
			_mapService = mapService;
		}

		private bool _isArmed;

		/// <summary>Whether State Isolation mode is on. Armed = hover-to-highlight + click-to-isolate on the
		/// map; off = the full map (also drops any active isolation). Two-way bound to the top-bar toggle.</summary>
		public bool IsArmed
		{
			get => _isArmed;
			set
			{
				if (!SetProperty(ref _isArmed, value)) { return; }
				// Leaving the mode drops any isolation locally too; the WebView's disarm also posts name=null,
				// but clear it eagerly so HasIsolatedState flips without waiting on the round-trip.
				if (!value) { IsolatedStateName = null; }
				if (_isMapReady) { _ = _mapService.SetStateIsolationAsync(value); }
			}
		}

		private string? _isolatedStateName;

		/// <summary>The name of the currently isolated state (e.g. "Texas"), or null if none is isolated
		/// (armed-but-not-yet-clicked, or the mode is off). Set from the WebView via the router.</summary>
		public string? IsolatedStateName
		{
			get => _isolatedStateName;
			private set
			{
				if (SetProperty(ref _isolatedStateName, value))
				{
					OnPropertyChanged(nameof(HasIsolatedState));
				}
			}
		}

		/// <summary>Whether a state is currently isolated (a state has been clicked while armed). Drives a
		/// future "Isolated: Texas" readout / the stream-mode UI.</summary>
		public bool HasIsolatedState => !string.IsNullOrEmpty(_isolatedStateName);

		/// <summary>Programmatically isolate a state by name (arms the mode if needed). For a future picker /
		/// stream-mode presets; the on-map click path goes straight through the WebView.</summary>
		public Task IsolateStateAsync(string name)
		{
			if (!_isArmed) { IsArmed = true; } // arm first (its setter issues the arm command)
			return _isMapReady ? _mapService.SelectIsolatedStateAsync(name) : Task.CompletedTask;
		}

		/// <summary>Exit isolation but STAY armed (back to hover mode so another state can be picked).</summary>
		public Task ClearIsolationAsync() =>
			_isMapReady ? _mapService.ClearStateIsolationAsync() : Task.CompletedTask;

		/// <summary>The WebView reports which state is isolated (name), or that isolation cleared (null).
		/// Routed from the <c>stateIsolated</c> message.</summary>
		public void OnStateIsolated(string? name) => IsolatedStateName = name;

		/// <summary>Marks the map page ready and applies a pre-ready armed state, if any (default is off, so
		/// this is usually a no-op — the toggle lives on the top bar and is flipped well after launch).</summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			if (_isArmed) { await _mapService.SetStateIsolationAsync(true); }
		}
	}
}
