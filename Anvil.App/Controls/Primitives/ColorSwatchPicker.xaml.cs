using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using FlyoutBase = Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase;

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

		/// <summary>Adds a trailing CUSTOM chip that opens a full colour picker. Off by default (the existing rows
		/// stay presets-only); set once in markup.</summary>
		public bool AllowCustom
		{
			get => (bool)GetValue(AllowCustomProperty);
			set => SetValue(AllowCustomProperty, value);
		}

		public static readonly DependencyProperty AllowCustomProperty =
			DependencyProperty.Register(nameof(AllowCustom), typeof(bool), typeof(ColorSwatchPicker),
				new PropertyMetadata(false, (d, _) => ((ColorSwatchPicker)d).Rebuild()));

		/// <summary>The colour as <c>#RRGGBB</c> (two-way, with <see cref="AllowCustom"/>). The custom picker WRITES
		/// it; the host reads it back so the custom chip can wear a colour that is not a preset (SelectedIndex -1).</summary>
		public string CustomHex
		{
			get => (string)GetValue(CustomHexProperty);
			set => SetValue(CustomHexProperty, value);
		}

		public static readonly DependencyProperty CustomHexProperty =
			DependencyProperty.Register(nameof(CustomHex), typeof(string), typeof(ColorSwatchPicker),
				new PropertyMetadata(string.Empty, (d, _) => ((ColorSwatchPicker)d).ApplySelection()));

		// One item per hex string: its data colour, its name, its selected state. The swatch itself is the XAML's.
		private void Rebuild()
		{
			var items = new List<ColorSwatchItem>();
			if (ItemsSource is not null)
			{
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
					items.Add(new ColorSwatchItem(this, i, TryParse(item?.ToString() ?? string.Empty),
						i < names.Count ? names[i] : string.Empty));
					i++;
				}
			}

			_items = items;
			Swatches.ItemsSource = items;
			CustomChip.Visibility = AllowCustom ? Visibility.Visible : Visibility.Collapsed;
			ApplySelection();
		}

		// A swatch was clicked. Clicking the CHOSEN swatch must leave it chosen: the ToggleButton has already
		// unchecked itself. ApplySelection puts it back.
		internal void Pick(int index)
		{
			SelectedIndex = index;
			ApplySelection();
		}

		// A ToggleButton flips itself on click; the state is ours to set (ApplySelection), not the click's.
		private void OnCustomChipClick(object sender, RoutedEventArgs e)
		{
			ApplySelection();
			FlyoutBase.ShowAttachedFlyout(CustomChip);
		}

		// Seed the picker with the colour in force (or white) WITHOUT that counting as a pick.
		private void OnCustomFlyoutOpening(object? sender, object e)
		{
			_syncingPicker = true;
			CustomPicker.Color = TryParse(CustomHex ?? string.Empty)?.Color ?? Colors.White;
			_syncingPicker = false;
		}

		private void OnCustomColorChanged(ColorPicker sender, ColorChangedEventArgs e)
		{
			if (_syncingPicker)
			{
				return; // our own seeding on open — not a choice
			}
			var c = e.NewColor;
			CustomHex = string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}");
		}

		private void ApplySelection()
		{
			for (var i = 0; i < _items.Count; i++)
			{
				_items[i].IsSelected = i == SelectedIndex;
			}

			if (CustomChip is not null && AllowCustom)
			{
				// Lit only for a colour no preset matches; then it WEARS that colour (a data brush).
				var brush = SelectedIndex < 0 ? TryParse(CustomHex ?? string.Empty) : null;
				CustomChip.Background = brush;
				CustomChip.Content = brush is null ? "+" : string.Empty;
				if ((CustomChip.IsChecked == true) != (brush is not null))
				{
					CustomChip.IsChecked = brush is not null;
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

		private List<ColorSwatchItem> _items = new();
		private bool _syncingPicker;
	}
}

namespace Anvil.Controls.Primitives
{
	/// <summary>One swatch of a <see cref="ColorSwatchPicker"/>: its DATA colour (null = the empty default chip),
	/// its name, and whether it is the chosen one. Drawn by the declared SwatchTemplate.</summary>
	public sealed class ColorSwatchItem : INotifyPropertyChanged
	{
		private readonly ColorSwatchPicker _owner;
		private readonly int _index;
		private bool _isSelected;

		internal ColorSwatchItem(ColorSwatchPicker owner, int index, SolidColorBrush? fill, string name)
		{
			_owner = owner;
			_index = index;
			Fill = fill;
			Name = name;
		}

		/// <summary>The chip's colour — a literal data brush, never a theme one. Null = the default chip.</summary>
		public SolidColorBrush? Fill { get; }
		/// <summary>"A" on the default chip, nothing on a coloured one.</summary>
		public string Face => Fill is null ? "A" : string.Empty;
		public string Name { get; }
		/// <summary>The tooltip: the name, or none at all when there is no name.</summary>
		public object? ToolTip => Name.Length > 0 ? Name : null;

		/// <summary>Two-way with the swatch's IsChecked — see the XAML template for why two-way.</summary>
		public bool IsSelected
		{
			get => _isSelected;
			set
			{
				if (_isSelected == value) return;
				_isSelected = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
			}
		}

		/// <summary>The swatch's Click (bound with x:Bind).</summary>
		public void Pick() => _owner.Pick(_index);

		public event PropertyChangedEventHandler? PropertyChanged;
	}
}
