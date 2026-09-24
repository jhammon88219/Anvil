using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// The Settings rows for ONE range ring's look — colour, opacity, thickness, line pattern — over whichever
	/// AppSettings properties hold that ring. One class for all three rings (Composites/RingStyleEditor draws
	/// it three times), so the three sections can't drift apart.
	/// </summary>
	/// <remarks>
	/// The ring's STROKE is one <see cref="RingStyle"/> record, replaced whole; its COLOUR comes through its own
	/// accessor pair because the outline's colour is <c>AppSettings.ScopeColor</c> (shared with the ruler), not
	/// a field of the record. <c>changed</c> is the owner's push — this class never talks to the page.
	/// </remarks>
	public sealed class RingStyleViewModel : ObservableObject
	{
		private readonly Func<RingStyle> _getStyle;
		private readonly Action<RingStyle> _setStyle;
		private readonly Func<string> _getColor;
		private readonly Action<string> _setColor;
		private readonly Action _changed;

		public RingStyleViewModel(Func<RingStyle> getStyle, Action<RingStyle> setStyle,
			Func<string> getColor, Action<string> setColor, Action changed)
		{
			_getStyle = getStyle;
			_setStyle = setStyle;
			_getColor = getColor;
			_setColor = setColor;
			_changed = changed;
		}

		/// <summary>The colour presets (hex; empty = the theme's colour for this ring).</summary>
		public IReadOnlyList<string> ColorSwatches { get; } = ScopeColors.Presets.Select(p => p.Hex).ToArray();

		/// <summary>The presets' names, parallel to <see cref="ColorSwatches"/>.</summary>
		public IReadOnlyList<string> ColorNames { get; } = ScopeColors.Presets.Select(p => p.Name).ToArray();

		/// <summary>The line-pattern picker's labels, in <see cref="RingLines.All"/> order.</summary>
		public IReadOnlyList<string> LineLabels { get; } = RingLines.Labels;

		/// <summary>The lit preset; -1 = a custom colour (<see cref="ColorHex"/>). The picker never writes -1.</summary>
		public int ColorIndex
		{
			get => ScopeColors.IndexOf(_getColor());
			set
			{
				if (value >= 0)
				{
					SetColor(ScopeColors.FromIndex(value));
				}
			}
		}

		/// <summary>The colour as <c>#RRGGBB</c> ("" = theme) — what the swatch row's CUSTOM chip reads and writes.</summary>
		public string ColorHex
		{
			get => ScopeColors.Normalize(_getColor());
			set => SetColor(ScopeColors.Normalize(value));
		}

		/// <summary>Opacity, percent.</summary>
		public double Opacity
		{
			get => _getStyle().OpacityPct;
			set => SetStyle(_getStyle() with { OpacityPct = double.IsFinite(value) ? (int)Math.Round(value) : 0 });
		}

		/// <summary>Stroke width, px.</summary>
		public double Width
		{
			get => _getStyle().Width;
			set => SetStyle(_getStyle() with { Width = value });
		}

		/// <summary>Index into <see cref="LineLabels"/>. PERSISTED as the token.</summary>
		public int LineIndex
		{
			get => RingLines.IndexOf(_getStyle().Line);
			set => SetStyle(_getStyle() with { Line = RingLines.FromIndex(value) });
		}

		/// <summary>Put the stroke back to <paramref name="defaults"/> and the colour to the theme's.</summary>
		public void Reset(RingStyle defaults)
		{
			_setStyle(defaults);
			_setColor(ScopeColors.ThemeDefault);
			Refresh();
		}

		/// <summary>Re-read every row (the settings changed under us, e.g. a reset).</summary>
		public void Refresh() => OnPropertyChanged(string.Empty);

		private void SetColor(string hex)
		{
			if (hex == ScopeColors.Normalize(_getColor()))
			{
				return;
			}
			_setColor(hex); // persists (auto-save)
			// Index and hex are two faces of one value — re-read both.
			OnPropertyChanged(nameof(ColorIndex));
			OnPropertyChanged(nameof(ColorHex));
			_changed();
		}

		private void SetStyle(RingStyle style)
		{
			var next = style.Normalized();
			if (next == _getStyle())
			{
				return;
			}
			_setStyle(next); // persists (auto-save)
			OnPropertyChanged(nameof(Opacity));
			OnPropertyChanged(nameof(Width));
			OnPropertyChanged(nameof(LineIndex));
			_changed();
		}
	}
}
