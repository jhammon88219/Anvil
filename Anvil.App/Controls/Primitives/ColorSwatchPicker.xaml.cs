using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ToggleButton = Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// A row of colour swatches over a short fixed list (see the XAML header). Host contract:
	/// <see cref="ItemsSource"/> (hex strings, <c>""</c> = the default swatch), <see cref="SelectedIndex"/>
	/// two-way, and optional <see cref="ToolTips"/>. The INDEX is the value.
	/// </summary>
	public sealed partial class ColorSwatchPicker : UserControl
	{
		public ColorSwatchPicker()
		{
			InitializeComponent();
		}

		/// <summary>The swatch colours as <c>#RRGGBB</c>, in order; <c>""</c> draws the default ("A") swatch.</summary>
		public IEnumerable? ItemsSource
		{
			get => (IEnumerable?)GetValue(ItemsSourceProperty);
			set => SetValue(ItemsSourceProperty, value);
		}

		public static readonly DependencyProperty ItemsSourceProperty =
			DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(ColorSwatchPicker),
				new PropertyMetadata(null, (d, _) => ((ColorSwatchPicker)d).Rebuild()));

		/// <summary>A name per swatch, parallel to <see cref="ItemsSource"/> — the tooltip and the accessible name.</summary>
		public IEnumerable? ToolTips
		{
			get => (IEnumerable?)GetValue(ToolTipsProperty);
			set => SetValue(ToolTipsProperty, value);
		}

		public static readonly DependencyProperty ToolTipsProperty =
			DependencyProperty.Register(nameof(ToolTips), typeof(IEnumerable), typeof(ColorSwatchPicker),
				new PropertyMetadata(null, (d, _) => ((ColorSwatchPicker)d).Rebuild()));

		/// <summary>The chosen swatch; -1 lights none. Two-way: a click writes it, a write re-lights the row.</summary>
		public int SelectedIndex
		{
			get => (int)GetValue(SelectedIndexProperty);
			set => SetValue(SelectedIndexProperty, value);
		}

		public static readonly DependencyProperty SelectedIndexProperty =
			DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(ColorSwatchPicker),
				new PropertyMetadata(-1, (d, _) => ((ColorSwatchPicker)d).ApplySelection()));

		private void Rebuild()
		{
			Swatches.Children.Clear();
			_buttons.Clear();
			if (ItemsSource is null)
			{
				return;
			}

			var names = new List<string>();
			if (ToolTips is not null)
			{
				foreach (var n in ToolTips)
				{
					names.Add(n?.ToString() ?? string.Empty);
				}
			}

			var i = 0;
			foreach (var item in ItemsSource)
			{
				var brush = TryParse(item?.ToString() ?? string.Empty);
				var swatch = new ToggleButton
				{
					Style = Resources.TryGetValue("SwatchStyle", out var style) ? style as Style : null,
					// ⚠️ A literal DATA colour, not a theme brush — see the header. Null = the empty default chip.
					Background = brush,
					Content = brush is null ? "A" : string.Empty,
				};
				if (i < names.Count && names[i].Length > 0)
				{
					ToolTipService.SetToolTip(swatch, names[i]);
					Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, names[i]);
				}

				var index = i;
				// Clicking the CHOSEN swatch must leave it chosen: the ToggleButton has already unchecked itself.
				swatch.Click += (_, _) =>
				{
					SelectedIndex = index;
					ApplySelection();
				};

				Swatches.Children.Add(swatch);
				_buttons.Add(swatch);
				i++;
			}

			ApplySelection();
		}

		private void ApplySelection()
		{
			for (var i = 0; i < _buttons.Count; i++)
			{
				var on = i == SelectedIndex;
				if ((_buttons[i].IsChecked == true) != on)
				{
					_buttons[i].IsChecked = on;
				}
			}
		}

		// "#RRGGBB" → an opaque brush; anything else (including "") → null, the default swatch's face.
		private static SolidColorBrush? TryParse(string hex)
		{
			if (hex.Length != 7 || hex[0] != '#'
				|| !uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
			{
				return null;
			}
			return new SolidColorBrush(ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
		}

		private readonly List<ToggleButton> _buttons = new();
	}
}
