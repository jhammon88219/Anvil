using System.Collections;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// A row of mutually-exclusive segments over a short fixed list (see the XAML header). Host contract:
	/// <see cref="ItemsSource"/> for the labels and <see cref="SelectedIndex"/> two-way for the choice — the
	/// INDEX is the value, and this control never interprets what a label says.
	/// </summary>
	public sealed partial class SegmentedPicker : UserControl
	{
		public SegmentedPicker()
		{
			InitializeComponent();
		}

		/// <summary>The segment labels, in order. Rebuilds the row when it changes.</summary>
		public IEnumerable? ItemsSource
		{
			get => (IEnumerable?)GetValue(ItemsSourceProperty);
			set => SetValue(ItemsSourceProperty, value);
		}

		public static readonly DependencyProperty ItemsSourceProperty =
			DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(SegmentedPicker),
				new PropertyMetadata(null, (d, _) => ((SegmentedPicker)d).Rebuild()));

		/// <summary>The selected segment. Two-way: a click writes it, and a write from the view model
		/// re-lights the row.</summary>
		public int SelectedIndex
		{
			get => (int)GetValue(SelectedIndexProperty);
			set => SetValue(SelectedIndexProperty, value);
		}

		public static readonly DependencyProperty SelectedIndexProperty =
			DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(SegmentedPicker),
				new PropertyMetadata(0, (d, _) => ((SegmentedPicker)d).ApplySelection()));

		// Build one column and one segment per label.
		//
		// ⚠️ The corner radius and border are POSITIONAL, which is the whole reason this is code and not an
		// ItemsControl: the ends round outward, and every segment draws the full border EXCEPT its left edge
		// so that each internal seam is drawn exactly once. The first segment adds its left edge back. Give
		// them all four edges and every seam doubles in weight.
		private void Rebuild()
		{
			Segments.Children.Clear();
			Segments.ColumnDefinitions.Clear();
			_buttons.Clear();

			if (ItemsSource is null)
			{
				return;
			}

			var labels = new List<object>();
			foreach (var item in ItemsSource)
			{
				labels.Add(item);
			}

			for (var i = 0; i < labels.Count; i++)
			{
				var first = i == 0;
				var last = i == labels.Count - 1;

				Segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

				var segment = new ToggleButton
				{
					Content = labels[i],
					Style = Resources.TryGetValue("SegmentStyle", out var style) ? style as Style : null,
					// left, top, right, bottom — the left edge only on the first, so seams don't double.
					BorderThickness = new Thickness(first ? 1 : 0, 1, 1, 1),
					CornerRadius = new CornerRadius(first ? 4 : 0, last ? 4 : 0, last ? 4 : 0, first ? 4 : 0),
				};

				var index = i;
				// Clicking the SELECTED segment must leave it selected: a ToggleButton has already unchecked
				// itself by now, and one of these is always on. ApplySelection puts it back.
				segment.Click += (_, _) =>
				{
					SelectedIndex = index;
					ApplySelection();
				};

				Grid.SetColumn(segment, i);
				Segments.Children.Add(segment);
				_buttons.Add(segment);
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

		private readonly List<ToggleButton> _buttons = new();
	}
}
