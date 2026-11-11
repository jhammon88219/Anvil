using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The left nav rail that selects one page of its host (see TabStrip.xaml for the drawn shape and the rules).
	/// Give it <see cref="ItemsSource"/> of <see cref="TabEntry"/> and bind <see cref="SelectedIndex"/> two-way;
	/// the host swaps its own body on that index.
	/// </summary>
	public sealed partial class TabStrip : UserControl
	{
		public TabStrip()
		{
			InitializeComponent();
		}

		/// <summary>The rows, in rail order. Items must be <see cref="TabEntry"/>.</summary>
		public IEnumerable? ItemsSource
		{
			get => (IEnumerable?)GetValue(ItemsSourceProperty);
			set => SetValue(ItemsSourceProperty, value);
		}

		public static readonly DependencyProperty ItemsSourceProperty =
			DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(TabStrip),
				new PropertyMetadata(null, OnItemsSourceChanged));

		private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var strip = (TabStrip)d;
			strip.Host.ItemsSource = e.NewValue;
			strip.ApplySelection();
		}

		/// <summary>The selected row's index. Two-way bindable; an out-of-range value selects nothing rather
		/// than throwing (the host is responsible for clamping — see MapViewModel.SettingsTabIndex).</summary>
		public int SelectedIndex
		{
			get => (int)GetValue(SelectedIndexProperty);
			set => SetValue(SelectedIndexProperty, value);
		}

		public static readonly DependencyProperty SelectedIndexProperty =
			DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(TabStrip),
				new PropertyMetadata(0, (d, _) => ((TabStrip)d).ApplySelection()));

		/// <summary>Bool → Visibility for the item template. A static so a DataTemplate can call it (x:Bind
		/// inside a template resolves against the ENTRY, so an instance method here is out of reach).</summary>
		public static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

		private IEnumerable<TabEntry> Entries =>
			ItemsSource?.OfType<TabEntry>() ?? Enumerable.Empty<TabEntry>();

		// Push the current index onto the entries; the item template lights itself from IsSelected.
		private void ApplySelection()
		{
			int i = 0;
			foreach (var entry in Entries)
			{
				entry.IsSelected = i == SelectedIndex;
				i++;
			}
		}

		private void OnTabClick(object sender, RoutedEventArgs e)
		{
			if (sender is not ToggleButton button || button.DataContext is not TabEntry entry) return;

			int index = Entries.ToList().IndexOf(entry);
			if (index >= 0) SelectedIndex = index;

			// ⚠️ RE-ASSERT, do not delete. The click already flipped IsChecked locally, and a OneWay binding
			// only pushes when the SOURCE changes — so clicking the row you are already on would leave it
			// dark with nothing to turn it back on.
			button.IsChecked = entry.IsSelected;
		}
	}
}
