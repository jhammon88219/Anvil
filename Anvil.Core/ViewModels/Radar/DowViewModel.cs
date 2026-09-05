using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// View model for the DOW Event Viewer: a single curated mobile-radar (Doppler-on-Wheels) frame
	/// rendered through the SAME radar render path as the NEXRAD loop, but standalone — no loop / live /
	/// site machinery (see tools/dow_import.py). Extracted from RadarViewModel and owned by it, so the
	/// shared radar-display / color-scale gate can react to <see cref="IsShowing"/>.
	/// </summary>
	public sealed class DowViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly IDowEventProvider _dowEventProvider;

		private int _dowEventIndex;
		private string _dowStatus = string.Empty;
		private int _dowProductIndex; // 0 = reflectivity, 1 = velocity
		private bool _isShowing;

		public DowViewModel(IMapService mapService, IDowEventProvider dowEventProvider)
		{
			_mapService = mapService;
			_dowEventProvider = dowEventProvider;
			DowEvents = new ObservableCollection<DowEvent>(_dowEventProvider.GetEvents());
		}

		/// <summary>Whether a DOW frame is currently displayed (separate from a NEXRAD loop). RadarViewModel
		/// watches this to gate the shared radar-display / color-scale state.</summary>
		public bool IsShowing
		{
			get => _isShowing;
			private set => SetProperty(ref _isShowing, value);
		}

		/// <summary>Called by RadarViewModel when a NEXRAD site selection takes over the radar layer.</summary>
		public void OnNexradTookOver() => IsShowing = false;

		/// <summary>
		/// The frames in the library. ⚠️ OBSERVABLE and rebuilt in place by <see cref="RefreshEvents"/>: the
		/// library is a folder the user imports into at runtime, so this is not a fixed list captured at
		/// construction the way it was when frames shipped inside the package.
		/// </summary>
		public ObservableCollection<DowEvent> DowEvents { get; }

		/// <summary>True when the library holds at least one frame. The library is EMPTY on a fresh install
		/// (frames are ~20 MB and are not bundled), so this gates the viewer's Load button and drives the
		/// "import one to get started" empty state.</summary>
		public bool HasDowEvents => DowEvents.Count > 0;

		/// <summary>Re-reads the library folder, keeping the selection on the same file where it survives.</summary>
		public void RefreshEvents(string? selectFileName = null)
		{
			var wanted = selectFileName ?? SelectedEvent?.FileName;

			DowEvents.Clear();
			foreach (var ev in _dowEventProvider.GetEvents())
			{
				DowEvents.Add(ev);
			}

			var idx = wanted is null ? -1 : IndexOfFile(wanted);
			DowEventIndex = idx >= 0 ? idx : 0;

			OnPropertyChanged(nameof(HasDowEvents));
			OnPropertyChanged(nameof(SelectedEvent));
			OnPropertyChanged(nameof(CanLoad));
		}

		private int IndexOfFile(string fileName)
		{
			for (var i = 0; i < DowEvents.Count; i++)
			{
				if (string.Equals(DowEvents[i].FileName, fileName, StringComparison.OrdinalIgnoreCase))
				{
					return i;
				}
			}
			return -1;
		}

		/// <summary>The selected frame, or null when the library is empty.</summary>
		public DowEvent? SelectedEvent =>
			DowEvents.Count == 0 ? null : DowEvents[Math.Clamp(_dowEventIndex, 0, DowEvents.Count - 1)];

		/// <summary>Whether there is something to load (the library is empty on a fresh install).</summary>
		public bool CanLoad => SelectedEvent is not null;

		/// <summary>
		/// Copies a chosen <c>.dow.json</c> into the library and selects it. ⚠️ It must be COPIED IN, not
		/// referenced where it lies: the WebView fetches the frame, so it has to sit under the mapped
		/// <c>dowevents</c> host to be same-origin. The caller supplies the path (the file picker needs a
		/// window HWND, so it lives in the view layer).
		/// </summary>
		public async Task ImportAsync(string? sourcePath)
		{
			if (string.IsNullOrWhiteSpace(sourcePath))
			{
				return; // the picker was cancelled
			}

			DowStatus = "Importing…";
			try
			{
				var imported = await _dowEventProvider.ImportAsync(sourcePath);
				RefreshEvents(imported.FileName);
				DowStatus = $"Imported {imported.Label}";
			}
			catch (Exception ex)
			{
				DowStatus = $"Import failed: {ex.Message}";
			}
		}

		/// <summary>Deletes the selected frame from the library (clearing it first if it is on screen).</summary>
		public async Task RemoveSelectedAsync()
		{
			if (SelectedEvent is not { } ev)
			{
				return;
			}

			if (IsShowing)
			{
				await ClearDowEventAsync();
			}

			_dowEventProvider.Remove(ev.FileName);
			RefreshEvents();
			DowStatus = $"Removed {ev.Label}";
		}

		/// <summary>Selected DOW event (index into <see cref="DowEvents"/>).</summary>
		public int DowEventIndex
		{
			get => _dowEventIndex;
			set
			{
				var c = DowEvents.Count == 0 ? 0 : Math.Clamp(value, 0, DowEvents.Count - 1);
				if (SetProperty(ref _dowEventIndex, c))
				{
					OnPropertyChanged(nameof(SelectedEvent));
					OnPropertyChanged(nameof(CanLoad));
				}
			}
		}

		/// <summary>Rendered DOW moment: 0 = reflectivity, 1 = velocity. Applies live to a shown frame.</summary>
		public int DowProductIndex
		{
			get => _dowProductIndex;
			set
			{
				var c = Math.Clamp(value, 0, 1);
				if (SetProperty(ref _dowProductIndex, c))
				{
					_ = _mapService.SetRadarProductAsync(0, c == 1 ? "velocity" : "reflectivity");
				}
			}
		}

		/// <summary>Product choices for the DOW Event Viewer (matches <see cref="DowProductIndex"/>).</summary>
		public IReadOnlyList<string> DowProductOptions { get; } = new[] { "Reflectivity", "Velocity" };

		/// <summary>Transient status line for the DOW Event Viewer.</summary>
		public string DowStatus
		{
			get => _dowStatus;
			private set => SetProperty(ref _dowStatus, value);
		}

		/// <summary>Loads + shows the selected DOW frame (decoded in the WebView via the radar pipeline).</summary>
		public async Task LoadDowEventAsync()
		{
			if (SelectedEvent is not { } ev)
			{
				DowStatus = "No DOW frames yet — convert one with tools/dow_import.py, then Import it.";
				return;
			}

			DowStatus = $"Loading {ev.Label}…";
			try
			{
				await _mapService.ShowDowFrameAsync(ev.Url);
				await _mapService.SetRadarProductAsync(0, _dowProductIndex == 1 ? "velocity" : "reflectivity");
				DowStatus = $"Showing {ev.Label}";
				IsShowing = true; // RadarViewModel re-raises HasRadarDisplay off this
			}
			catch (Exception ex)
			{
				DowStatus = $"Load failed: {ex.Message}";
			}
		}

		/// <summary>Clears the shown DOW frame.</summary>
		public async Task ClearDowEventAsync()
		{
			await _mapService.ClearDowFrameAsync();
			IsShowing = false;
			DowStatus = string.Empty;
		}
	}
}
