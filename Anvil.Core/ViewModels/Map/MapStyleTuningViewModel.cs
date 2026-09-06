using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// DEV TOOL. The live basemap style tuner: five sliders over a LEVELS transform, applied to the map as
	/// you drag, persisted as a working draft, and exported as a colour map the host bakes into the style
	/// file.
	/// </summary>
	/// <remarks>
	/// ⚠️ IT EXISTS TO KILL THE F5 LOOP. Tuning a basemap by editing a JSON asset and redeploying the MSIX
	/// is minutes per colour; this is a slider. The tuned result is not the product — the EXPORTED style
	/// file is, which then becomes a bundled asset like the other five.
	///
	/// ⚠️ DEBUG-ONLY BY CONSTRUCTION, like <see cref="SiteSweepViewModel"/>: the type ships in Core but
	/// MainWindow only builds it in Debug, and the Dev tab that drives it is neither listed nor constructed
	/// in Release. CONSEQUENCE, and it is the right one: a Release build does NOT apply a persisted tuning.
	/// A working draft is not a shipped look — the exported file is.
	///
	/// ⚠️⚠️ NOTHING HERE PERSISTS — the transform and the overrides are SESSION-ONLY, and that is a
	/// decision, not an omission. An editor draft that silently changed how the app LAUNCHED meant what you
	/// saw on start-up was not what the style actually is, with no way to tell the two apart.
	/// <b>The EXPORTED file is the only way an edit becomes real.</b>
	/// <para>⚠️ It also retired a trap worth remembering: settings auto-save is DEBOUNCED 500 ms with no
	/// flush on shutdown, and stopping the app from Visual Studio kills the process outright — so an edit
	/// (including a CLEAR) made in the last half-second before stopping was silently lost, and the previous
	/// value came back on the next run looking exactly like the clear had failed. That debounce gap is
	/// still there for the settings that genuinely do persist.</para>
	/// </remarks>
	public sealed class MapStyleTuningViewModel : ObservableObject
	{
		private readonly IMapService _mapService;

		private bool _isMapReady;

		// Supersede-token for the push debounce. A slider drag raises a change per frame, and each push is a
		// setPaintProperty sweep over every colour-bearing basemap layer in every pane — cheap individually,
		// wasteful sixty times a second. See SchedulePush.
		private int _pushGen;

		public MapStyleTuningViewModel(IMapService mapService)
		{
			_mapService = mapService;
		}

		/// <summary>The five parameters as the record the map service speaks.</summary>
		public MapStyleTuning Current => new(_white, _black, _gamma, _tintHue, _tintStrength);

		/// <summary>Whether anything is actually tuned — drives the Export button and the readout.</summary>
		public bool HasTuning => !Current.IsIdentity;

		/// <summary>A one-line description of the transform, for the tool's status line.</summary>
		public string SummaryText => HasTuning
			? $"white {_white:0} · black {_black:0} · gamma {_gamma:0.00}" +
			  (_tintStrength > 0.001 ? $" · tint {_tintHue:0}° at {_tintStrength:0.00}" : "")
			: "Untouched — the style's own colours.";

		// ── The five parameters ──────────────────────────────────────────────────────────────────────
		// Each setter does the same three things: store, persist, schedule a push. They are written out
		// rather than routed through one helper because the persisted property differs per parameter and a
		// switch on a name would be worse than the repetition.

		private double _white = 255;
		/// <summary>Output for an input of 255 — the ramp's white point. THE knob for "too bright".</summary>
		public double White
		{
			get => _white;
			set
			{
				if (SetProperty(ref _white, value))
				{
					OnParameterChanged();
				}
			}
		}

		// ⚠️ THESE INITIALISERS ARE THE IDENTITY TRANSFORM, and they are load-bearing now that nothing is
		// restored: the constructor used to overwrite all five from settings, so the CLR defaults never showed.
		// Left at 0, White would fail IsIdentity (which wants >= 254.5), HasTuning would be true on a fresh
		// launch, and the first Slider to coerce its value to its Minimum would push a near-black basemap.
		// _black and _tintStrength are correct AT the CLR default — do not "tidy" them to match the others.
		private double _black;
		/// <summary>Output for an input of 0 — the ramp's black point. Lifts the darkest values off zero.</summary>
		public double Black
		{
			get => _black;
			set
			{
				if (SetProperty(ref _black, value))
				{
					OnParameterChanged();
				}
			}
		}

		private double _gamma = 1.0;
		/// <summary>Midtone shaping between the two points. 1 is a straight line, i.e. a plain scale.</summary>
		public double Gamma
		{
			get => _gamma;
			set
			{
				if (SetProperty(ref _gamma, value))
				{
					OnParameterChanged();
				}
			}
		}

		private double _tintHue = 210;
		/// <summary>Hue (0-360) the ramp is cast toward. Inert while <see cref="TintStrength"/> is 0.</summary>
		public double TintHue
		{
			get => _tintHue;
			set
			{
				if (SetProperty(ref _tintHue, value))
				{
					OnParameterChanged();
				}
			}
		}

		private double _tintStrength;
		/// <summary>How far toward <see cref="TintHue"/> (0-1). 0 is a neutral grey.</summary>
		public double TintStrength
		{
			get => _tintStrength;
			set
			{
				if (SetProperty(ref _tintStrength, value))
				{
					OnParameterChanged();
				}
			}
		}

		// ── Lifecycle ────────────────────────────────────────────────────────────────────────────────

		/// <summary>
		/// Opens the gate on map commands. ⚠️ There is nothing to restore — the editor's state is
		/// SESSION-ONLY (see the class remarks), so every launch starts on the style as it really ships.
		/// </summary>
		public Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			return Task.CompletedTask;
		}

		/// <summary>Puts every parameter back to the identity transform and restores the style's own colours.</summary>
		public void Reset()
		{
			White = 255;
			Black = 0;
			Gamma = 1.0;
			TintStrength = 0;
			// ⚠️ Hue is deliberately LEFT ALONE: at strength 0 it changes nothing, and resetting it would
			// throw away the hue you had dialled in the moment you pulled the strength to zero to compare.
		}

		// ── Per-slot overrides ───────────────────────────────────────────────────────────────────────
		// ⚠️ A SLOT IS (layer, paint property, OCCURRENCE INDEX) — see MapStyleTuning / style-tune.js. Not a
		// layer, because one property can hold several colours; not a distinct colour, because #ffffff is
		// the earth fill AND 12 road casings AND 8 label halos, so editing by colour is not full control.
		// ⚠️ RESOLUTION: an override WINS for its slot, everything else still goes through the transform.
		// The transform is the global grade; overrides are the escape hatch for what the grade gets wrong.

		/// <summary>Every colour slot in the loaded basemap, document order, grouped by layer family.</summary>
		public ObservableCollection<StyleSlotRow> Slots { get; } = new();

		private readonly Dictionary<string, string> _overrides = new(StringComparer.Ordinal);

		/// <summary>How many slots carry an explicit colour.</summary>
		public int OverrideCount => _overrides.Count;

		/// <summary>Whether anything at all is applied — the transform, an override, or both.</summary>
		public bool HasAnyEdit => HasTuning || _overrides.Count > 0;

		/// <summary>
		/// Populates <see cref="Slots"/> from the page. ⚠️ START-THEN-POLL: taking the snapshot is a fetch
		/// and ExecuteScriptAsync does not await a promise, so this kicks the load and then reads the page
		/// global — the same shape the dealias validation harness uses, for the same reason.
		/// </summary>
		public async Task LoadSlotsAsync()
		{
			if (!_isMapReady || Slots.Count > 0)
			{
				return;
			}

			await _mapService.LoadStyleSlotsAsync();

			// Bounded poll: a same-origin fetch of a bundled file, so this lands almost immediately; the cap
			// exists so a failure is a quiet empty list rather than a hang.
			for (var attempt = 0; attempt < 40 && Slots.Count == 0; attempt++)
			{
				await Task.Delay(50);
				var json = await _mapService.PollStyleSlotsAsync();
				json = Unquote(json);
				if (string.IsNullOrWhiteSpace(json)) continue;

				var list = JsonSerializer.Deserialize<List<SlotDto>>(json);
				if (list is null) continue;

				foreach (var slot in list)
				{
					var key = $"{slot.id}|{slot.prop}|{slot.index}";
					_overrides.TryGetValue(key, out var over);
					Slots.Add(new StyleSlotRow(key, slot.id, slot.prop, slot.index, slot.@base, over));
				}
			}

			OnPropertyChanged(nameof(Slots));
		}

		/// <summary>Sets (or, with a null/empty colour, clears) one slot's explicit colour.</summary>
		public void SetOverride(StyleSlotRow row, string? hex)
		{
			if (row is null) return;

			if (string.IsNullOrWhiteSpace(hex))
			{
				_overrides.Remove(row.Key);
				row.ApplyOverride(null);
			}
			else
			{
				_overrides[row.Key] = hex;
				row.ApplyOverride(hex);
			}

			OnPropertyChanged(nameof(OverrideCount));
			OnPropertyChanged(nameof(HasAnyEdit));
			SchedulePush();
		}

		/// <summary>Drops every explicit colour, leaving the style on the transform alone.</summary>
		public void ClearOverrides()
		{
			if (_overrides.Count == 0) return;
			_overrides.Clear();
			foreach (var row in Slots) row.ApplyOverride(null);
			OnPropertyChanged(nameof(OverrideCount));
			OnPropertyChanged(nameof(HasAnyEdit));
			SchedulePush();
		}

		/// <summary>
		/// Every slot's RESOLVED colour, in document order, as JSON — what the host writes into the style
		/// file by position. Empty when the page has no snapshot.
		/// </summary>
		public Task<string> GetSlotColorsJsonAsync() =>
			_isMapReady ? _mapService.GetStyleSlotColorsAsync() : Task.FromResult(string.Empty);

		// ExecuteScriptAsync hands back a JSON-ENCODED value, so a string arrives wrapped in quotes with its
		// own quotes escaped. Everything else that reads a page global does the same unwrap.
		private static string Unquote(string? raw)
		{
			if (string.IsNullOrWhiteSpace(raw) || raw == "null") return "";
			raw = raw.Trim();
			return raw.StartsWith('"') ? JsonSerializer.Deserialize<string>(raw) ?? "" : raw;
		}

		private sealed class SlotDto
		{
			public string id { get; set; } = "";
			public string prop { get; set; } = "";
			public int index { get; set; }
			public string @base { get; set; } = "";
		}

		private void OnParameterChanged()
		{
			OnPropertyChanged(nameof(HasTuning));
			OnPropertyChanged(nameof(SummaryText));
			SchedulePush();
		}

		// Debounced apply. A drag raises a change per frame; this collapses a burst into one push ~60 ms
		// after the last of them, which is under the threshold where a slider stops feeling live.
		// ⚠️ Fire-and-forget by design (a slider has nothing to await), so the body is wrapped: an
		// unobserved throw here would surface only as a [FTL] in the log, exactly the trap the storm-report
		// overlay hit.
		private async void SchedulePush()
		{
			if (!_isMapReady)
			{
				return;
			}

			var gen = Interlocked.Increment(ref _pushGen);
			try
			{
				await Task.Delay(60);
				if (gen != Volatile.Read(ref _pushGen))
				{
					return;   // a newer change superseded this one
				}

				await _mapService.SetStyleTuningAsync(HasTuning ? Current : null);
				await _mapService.SetStyleOverridesAsync(
					_overrides.Count == 0 ? "" : JsonSerializer.Serialize(_overrides));
			}
			catch (Exception)
			{
				// A failed preview must not take the tool (or the app) down; the next drag pushes again.
			}
		}
	}
}
