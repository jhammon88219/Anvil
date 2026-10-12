using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// A row of mutually-exclusive segments over a short fixed list (see the XAML header). Host contract:
	/// <see cref="ItemsSource"/> for the labels and <see cref="SelectedIndex"/> two-way for the choice — the
	/// INDEX is the value, and this control never interprets what a label says.
	/// The segments are DECLARED in the XAML (four positional templates); this only turns the labels into
	/// <see cref="SegmentedPickerItem"/>s and keeps their selected state in step with <see cref="SelectedIndex"/>.
	/// </summary>
	public sealed partial class SegmentedPicker : UserControl
	{
		public SegmentedPicker()
		{
			InitializeComponent();
		}

		/// <summary>The segment labels, in order. Re-lists the row when it changes.</summary>
		public IEnumerable? ItemsSource
		{
			get => (IEnumerable?)GetValue(ItemsSourceProperty);
			set => SetValue(ItemsSourceProperty, value);
		}

		public static readonly DependencyProperty ItemsSourceProperty =
			DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(SegmentedPicker),
				new PropertyMetadata(null, (d, _) => ((SegmentedPicker)d).Relist()));

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

		/// <summary>One segment to show but not allow (greyed, unclickable) — an option that exists but isn't
		/// available yet. -1 (the default) disables none.</summary>
		public int DisabledIndex
		{
			get => (int)GetValue(DisabledIndexProperty);
			set => SetValue(DisabledIndexProperty, value);
		}

		public static readonly DependencyProperty DisabledIndexProperty =
			DependencyProperty.Register(nameof(DisabledIndex), typeof(int), typeof(SegmentedPicker),
				new PropertyMetadata(-1, (d, _) => ((SegmentedPicker)d).Relist()));

		// One item per label, carrying its POSITION (which template it gets) and its state.
		private void Relist()
		{
			var items = new List<SegmentedPickerItem>();
			if (ItemsSource is not null)
			{
				var labels = new List<object>();
				foreach (var label in ItemsSource)
				{
					labels.Add(label);
				}

				for (var i = 0; i < labels.Count; i++)
				{
					items.Add(new SegmentedPickerItem(this, i, labels[i], first: i == 0, last: i == labels.Count - 1,
						enabled: i != DisabledIndex));
				}
			}

			_items = items;
			Segments.ItemsSource = items;
			ApplySelection();
		}

		// A segment was clicked. Clicking the SELECTED one must leave it selected: the ToggleButton has already
		// unchecked itself by now, and one of these is always on. ApplySelection puts it back.
		internal void Pick(int index)
		{
			SelectedIndex = index;
			ApplySelection();
		}

		private void ApplySelection()
		{
			for (var i = 0; i < _items.Count; i++)
			{
				_items[i].IsSelected = i == SelectedIndex;
			}
		}

		private List<SegmentedPickerItem> _items = new();
	}

	/// <summary>One segment of a <see cref="SegmentedPicker"/>: its label, its position in the row (which of the
	/// four declared templates it wears) and whether it is on.</summary>
	public sealed class SegmentedPickerItem : INotifyPropertyChanged
	{
		private readonly SegmentedPicker _owner;
		private bool _isSelected;

		internal SegmentedPickerItem(SegmentedPicker owner, int index, object label, bool first, bool last, bool enabled)
		{
			_owner = owner;
			Index = index;
			Label = label;
			IsFirst = first;
			IsLast = last;
			IsEnabled = enabled;
		}

		public int Index { get; }
		public object Label { get; }
		public bool IsFirst { get; }
		public bool IsLast { get; }
		public bool IsEnabled { get; }

		/// <summary>Two-way with the segment's IsChecked — see the XAML templates for why two-way.</summary>
		public bool IsSelected
		{
			get => _isSelected;
			set
			{
				if (_isSelected == value) return;
				_isSelected = value;
				OnPropertyChanged();
			}
		}

		/// <summary>The segment's Click (bound with x:Bind).</summary>
		public void Pick() => _owner.Pick(Index);

		public event PropertyChangedEventHandler? PropertyChanged;
		private void OnPropertyChanged([CallerMemberName] string? name = null) =>
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}

	/// <summary>Picks a segment's template by its POSITION — the one thing a DataTemplate can't see. The four
	/// templates themselves are declared in SegmentedPicker.xaml.</summary>
	public sealed class SegmentTemplateSelector : DataTemplateSelector
	{
		public DataTemplate? First { get; set; }
		public DataTemplate? Middle { get; set; }
		public DataTemplate? Last { get; set; }
		public DataTemplate? Only { get; set; }

		protected override DataTemplate? SelectTemplateCore(object item) => Pick(item);
		protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => Pick(item);

		private DataTemplate? Pick(object item) => item is SegmentedPickerItem s
			? s.IsFirst && s.IsLast ? Only : s.IsFirst ? First : s.IsLast ? Last : Middle
			: Middle;
	}
}
