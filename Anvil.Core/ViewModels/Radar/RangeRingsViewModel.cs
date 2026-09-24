using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// Everything Settings → Radar Range Ring holds: WHICH rings are drawn, how each one LOOKS, and where the
	/// distance labels sit. Sub-VM of <see cref="RadarViewModel"/> (<c>Radar.RangeRings</c>), like <c>Dow</c>.
	/// </summary>
	/// <remarks>
	/// ⚠️ PREFERENCES ONLY. The page SIZES the reflectivity and velocity rings from the displayed frame
	/// (radar-scope.js); nothing here knows a radius. Three seams reach the page: <c>setRangeRings</c> (which),
	/// <c>setRangeRingStyle</c> (the look, one JSON object) and <c>setScopeColor</c> (the outline colour, which
	/// the RULER shares — that is why it stays a CSS variable and not a field of the style).
	/// ⚠️ The label BEARING also changes from the page: dragging the label handle posts
	/// <c>rangeRingLabelBearing</c> → <see cref="OnLabelBearingDragged"/>, which persists it WITHOUT pushing it
	/// back (the page already drew it; an echo would re-add the layers under the finger).
	/// </remarks>
	public sealed class RangeRingsViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly ISettingsService _settings;
		private bool _isMapReady;

		public RangeRingsViewModel(IMapService mapService, ISettingsService settings)
		{
			_mapService = mapService;
			_settings = settings;

			Outline = new RingStyleViewModel(
				() => _settings.Settings.OutlineRingStyle, v => _settings.Settings.OutlineRingStyle = v,
				() => _settings.Settings.ScopeColor,
				v => { _settings.Settings.ScopeColor = v; _ = PushOutlineColorAsync(); },
				() => _ = PushStyleAsync());
			Velocity = new RingStyleViewModel(
				() => _settings.Settings.VelocityRingStyle, v => _settings.Settings.VelocityRingStyle = v,
				() => _settings.Settings.VelocityRingColor, v => _settings.Settings.VelocityRingColor = v,
				() => _ = PushStyleAsync());
			Distance = new RingStyleViewModel(
				() => _settings.Settings.DistanceRingStyle, v => _settings.Settings.DistanceRingStyle = v,
				() => _settings.Settings.DistanceRingColor, v => _settings.Settings.DistanceRingColor = v,
				() => _ = PushStyleAsync());
		}

		/// <summary>The reflectivity outline's look. Its colour is <c>ScopeColor</c> — the ruler wears it too.</summary>
		public RingStyleViewModel Outline { get; }

		/// <summary>The velocity reach ring's look.</summary>
		public RingStyleViewModel Velocity { get; }

		/// <summary>The distance rings' look.</summary>
		public RingStyleViewModel Distance { get; }

		// ── Which rings ───────────────────────────────────────────────────────────────────────────────

		/// <summary>The reflectivity outline (where the data ends). PERSISTED.</summary>
		public bool ShowReflectivityRing
		{
			get => _settings.Settings.ShowReflectivityRing;
			set
			{
				if (_settings.Settings.ShowReflectivityRing == value) { return; }
				_settings.Settings.ShowReflectivityRing = value; // persists (auto-save)
				OnPropertyChanged();
				_ = PushRingsAsync();
			}
		}

		/// <summary>The velocity reach ring. PERSISTED.</summary>
		public bool ShowVelocityRing
		{
			get => _settings.Settings.ShowVelocityRing;
			set
			{
				if (_settings.Settings.ShowVelocityRing == value) { return; }
				_settings.Settings.ShowVelocityRing = value;
				OnPropertyChanged();
				_ = PushRingsAsync();
			}
		}

		/// <summary>The fixed-spacing distance rings. PERSISTED. Also enables their spacing + label rows.</summary>
		public bool ShowDistanceRings
		{
			get => _settings.Settings.ShowDistanceRings;
			set
			{
				if (_settings.Settings.ShowDistanceRings == value) { return; }
				_settings.Settings.ShowDistanceRings = value;
				OnPropertyChanged();
				_ = PushRingsAsync();
			}
		}

		/// <summary>The spacing picker's labels, in <see cref="RangeRingSpacings.All"/> order.</summary>
		public IReadOnlyList<string> DistanceRingSpacingLabels { get; } = RangeRingSpacings.Labels;

		/// <summary>Two-way for the spacing picker. PERSISTED as the value, never the index.</summary>
		public int DistanceRingSpacingIndex
		{
			get => RangeRingSpacings.IndexOf(_settings.Settings.DistanceRingSpacing);
			set
			{
				var spacing = RangeRingSpacings.FromIndex(value);
				if (spacing == RangeRingSpacings.Normalize(_settings.Settings.DistanceRingSpacing))
				{
					return;
				}
				_settings.Settings.DistanceRingSpacing = spacing; // persists (auto-save)
				OnPropertyChanged();
				_ = PushRingsAsync();
			}
		}

		// ── Distance labels ───────────────────────────────────────────────────────────────────────────

		/// <summary>The label colour presets; the empty one ("A") means "the distance rings' colour".</summary>
		public IReadOnlyList<string> LabelColorSwatches { get; } = ScopeColors.Presets.Select(p => p.Hex).ToArray();

		/// <summary>Names for <see cref="LabelColorSwatches"/>; the first reads "Same as the rings" here.</summary>
		public IReadOnlyList<string> LabelColorNames { get; } =
			ScopeColors.Presets.Select((p, i) => i == 0 ? "Same as the distance rings" : p.Name).ToArray();

		/// <summary>The lit label preset; -1 = custom (<see cref="LabelColorHex"/>).</summary>
		public int LabelColorIndex
		{
			get => ScopeColors.IndexOf(_settings.Settings.DistanceLabelColor);
			set
			{
				if (value >= 0)
				{
					SetLabelColor(ScopeColors.FromIndex(value));
				}
			}
		}

		/// <summary>The label colour as <c>#RRGGBB</c> ("" = the rings' colour) — the custom chip's value.</summary>
		public string LabelColorHex
		{
			get => ScopeColors.Normalize(_settings.Settings.DistanceLabelColor);
			set => SetLabelColor(ScopeColors.Normalize(value));
		}

		/// <summary>Label opacity, percent.</summary>
		public double LabelOpacity
		{
			get => _settings.Settings.DistanceLabelStyle.OpacityPct;
			set => SetLabelStyle(_settings.Settings.DistanceLabelStyle with { OpacityPct = double.IsFinite(value) ? (int)Math.Round(value) : 0 });
		}

		/// <summary>Label text size, px.</summary>
		public double LabelSize
		{
			get => _settings.Settings.DistanceLabelStyle.Size;
			set => SetLabelStyle(_settings.Settings.DistanceLabelStyle with { Size = value });
		}

		/// <summary>Width of the dark halo behind the labels, px (0 = none).</summary>
		public double LabelHalo
		{
			get => _settings.Settings.DistanceLabelStyle.Halo;
			set => SetLabelStyle(_settings.Settings.DistanceLabelStyle with { Halo = value });
		}

		/// <summary>Where the labels sit, degrees clockwise from north. The map's label HANDLE writes this too.</summary>
		public double LabelBearing
		{
			get => _settings.Settings.DistanceLabelBearing;
			set
			{
				var deg = RingLabelBearing.Normalize(value);
				if (deg == _settings.Settings.DistanceLabelBearing) { return; }
				_settings.Settings.DistanceLabelBearing = deg; // persists (auto-save)
				OnPropertyChanged();
				_ = PushStyleAsync();
			}
		}

		/// <summary>Whether the map shows the label drag handle. PERSISTED.</summary>
		public bool ShowLabelHandle
		{
			get => _settings.Settings.ShowDistanceLabelHandle;
			set
			{
				if (_settings.Settings.ShowDistanceLabelHandle == value) { return; }
				_settings.Settings.ShowDistanceLabelHandle = value;
				OnPropertyChanged();
				_ = PushStyleAsync();
			}
		}

		/// <summary>Swing the labels back to north (the Settings "North" button).</summary>
		public void ResetLabelBearing() => LabelBearing = 0;

		/// <summary>The page reports a finished drag of the label handle. Persist it, re-read the slider, and do
		/// NOT push it back — see the class remarks.</summary>
		public void OnLabelBearingDragged(double degrees)
		{
			var deg = RingLabelBearing.Normalize(degrees);
			if (deg == _settings.Settings.DistanceLabelBearing) { return; }
			_settings.Settings.DistanceLabelBearing = deg;
			OnPropertyChanged(nameof(LabelBearing));
		}

		/// <summary>Put every ring's LOOK back to the defaults: strokes, colours (the outline's too, so the ruler
		/// follows), label style and bearing. Which rings are shown, and the spacing, are left alone.</summary>
		public void ResetLook()
		{
			Outline.Reset(RingStyle.OutlineDefault);
			Velocity.Reset(RingStyle.VelocityDefault);
			Distance.Reset(RingStyle.DistanceDefault);
			var s = _settings.Settings;
			s.DistanceLabelColor = ScopeColors.ThemeDefault;
			s.DistanceLabelStyle = RingLabelStyle.Default;
			s.DistanceLabelBearing = 0;
			s.ShowDistanceLabelHandle = true;
			OnPropertyChanged(string.Empty);
			_ = PushOutlineColorAsync();
			_ = PushStyleAsync();
		}

		/// <summary>Replays every persisted ring preference into a freshly loaded page. Called from
		/// <c>RadarViewModel.OnMapsReadyAsync</c>.</summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			await PushOutlineColorAsync();
			await PushStyleAsync();
			await PushRingsAsync();
		}

		private void SetLabelColor(string hex)
		{
			if (hex == ScopeColors.Normalize(_settings.Settings.DistanceLabelColor)) { return; }
			_settings.Settings.DistanceLabelColor = hex;
			OnPropertyChanged(nameof(LabelColorIndex));
			OnPropertyChanged(nameof(LabelColorHex));
			_ = PushStyleAsync();
		}

		private void SetLabelStyle(RingLabelStyle style)
		{
			var next = style.Normalized();
			if (next == _settings.Settings.DistanceLabelStyle) { return; }
			_settings.Settings.DistanceLabelStyle = next;
			OnPropertyChanged(nameof(LabelOpacity));
			OnPropertyChanged(nameof(LabelSize));
			OnPropertyChanged(nameof(LabelHalo));
			_ = PushStyleAsync();
		}

		private Task PushRingsAsync()
		{
			if (!_isMapReady) { return Task.CompletedTask; }
			var s = _settings.Settings;
			return _mapService.SetRangeRingsAsync(s.ShowReflectivityRing, s.ShowVelocityRing, s.ShowDistanceRings,
				s.DistanceRingSpacing);
		}

		private Task PushOutlineColorAsync() =>
			_isMapReady ? _mapService.SetScopeColorAsync(ScopeColors.Normalize(_settings.Settings.ScopeColor)) : Task.CompletedTask;

		private Task PushStyleAsync() =>
			_isMapReady ? _mapService.SetRangeRingStyleAsync(BuildStyleJson(_settings.Settings)) : Task.CompletedTask;

		/// <summary>The page's style object (radar-scope.js setStyle). Opacities go over as 0–1. ⚠️ Colours are
		/// re-normalized here: they land in MapLibre paint properties, and "" means "use the theme's".</summary>
		internal static string BuildStyleJson(AppSettings s)
		{
			static object Ring(RingStyle r, string? color) => new
			{
				color = color is null ? null : ScopeColors.Normalize(color),
				op = r.OpacityPct / 100.0,
				w = r.Width,
				line = RingLines.Normalize(r.Line),
			};
			var label = s.DistanceLabelStyle;
			return JsonSerializer.Serialize(new
			{
				refl = Ring(s.OutlineRingStyle, null), // the outline's colour is the --anvil-scope-ring variable
				vel = Ring(s.VelocityRingStyle, s.VelocityRingColor),
				dist = Ring(s.DistanceRingStyle, s.DistanceRingColor),
				label = new
				{
					color = ScopeColors.Normalize(s.DistanceLabelColor),
					op = label.OpacityPct / 100.0,
					size = label.Size,
					halo = label.Halo,
				},
				bearing = RingLabelBearing.Normalize(s.DistanceLabelBearing),
				handle = s.ShowDistanceLabelHandle,
			});
		}
	}
}
