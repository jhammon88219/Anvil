using System.Collections.Generic;
using System.Linq;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// The hatch scrubber's OWN look (Settings → Radar Console), apart from Settings → Edges (the user's call, 2026-11-01):
	/// colour, spacing, weight, opacity. The Radar Console tab binds the pickers; the consoles' HatchScrubbers bind the
	/// read-outs. Every value is ONE AppSettings property, normalized on read.
	/// </summary>
	public sealed class HatchScrubberLookViewModel : ObservableObject
	{
		private readonly AppSettings _settings;

		internal HatchScrubberLookViewModel(AppSettings settings) => _settings = settings;

		/// <summary>The colour swatches (hex; "" = the accent, the "A" chip) and their tooltips.</summary>
		public IReadOnlyList<string> ColorSwatches { get; } = HatchScrubberLook.Presets.Select(p => p.Hex).ToArray();
		public IReadOnlyList<string> ColorNames { get; } = HatchScrubberLook.Presets.Select(p => p.Name).ToArray();

		/// <summary>The lit colour preset; -1 = a custom colour (<see cref="ColorHex"/>).</summary>
		public int ColorIndex
		{
			get => HatchScrubberLook.IndexOf(_settings.HatchScrubberColor);
			set
			{
				if (value >= 0) { SetColor(HatchScrubberLook.FromIndex(value)); }
			}
		}

		/// <summary>The line colour as <c>#RRGGBB</c> ("" = the accent) — what the scrubber draws, and the custom chip's value.</summary>
		public string ColorHex
		{
			get => HatchScrubberLook.NormalizeColor(_settings.HatchScrubberColor);
			set => SetColor(HatchScrubberLook.NormalizeColor(value));
		}

		private void SetColor(string hex)
		{
			if (_settings.HatchScrubberColor == hex) { return; }
			_settings.HatchScrubberColor = hex;
			OnPropertyChanged(nameof(ColorIndex));
			OnPropertyChanged(nameof(ColorHex));
		}

		/// <summary>The gap between lines, px at the edge's 35 px (scaled to the strip).</summary>
		public double Spacing
		{
			get => HatchScrubberLook.Spacing(_settings.HatchScrubberSpacing);
			set => SetValue(HatchScrubberLook.Spacing(value), Spacing, v => _settings.HatchScrubberSpacing = v);
		}

		/// <summary>The line thickness, px at the edge's 35 px (half-pixel steps; scaled to the strip).</summary>
		public double Weight
		{
			get => HatchScrubberLook.Weight(_settings.HatchScrubberWeight);
			set => SetValue(HatchScrubberLook.Weight(value), Weight, v => _settings.HatchScrubberWeight = v);
		}

		/// <summary>The lines' opacity, percent.</summary>
		public double Opacity
		{
			get => HatchScrubberLook.Opacity(_settings.HatchScrubberOpacity);
			set
			{
				if (SetValue(HatchScrubberLook.Opacity(value), Opacity, v => _settings.HatchScrubberOpacity = v))
				{
					OnPropertyChanged(nameof(LineOpacity));
				}
			}
		}

		/// <summary><see cref="Opacity"/> as 0–1, for the scrubber.</summary>
		public double LineOpacity => Opacity / 100;

		public string SpacingText => $"{Spacing:0} px";
		public string WeightText => $"{Weight:0.0} px";
		public string OpacityText => $"{Opacity:0}%";

		/// <summary>The tab's Restore defaults: the user's picks from the mock-up.</summary>
		public void RestoreDefaults()
		{
			ColorHex = HatchScrubberLook.Accent;
			Spacing = HatchScrubberLook.DefaultSpacing;
			Weight = HatchScrubberLook.DefaultWeight;
			Opacity = HatchScrubberLook.DefaultOpacity;
		}

		private bool SetValue(double value, double current, System.Action<double> write, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
		{
			if (value == current) { return false; }
			write(value);
			OnPropertyChanged(name);
			OnPropertyChanged(name + "Text");
			return true;
		}
	}
}
