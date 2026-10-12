using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// A rail of tabs that selects one of them (see TabStrip.xaml for the drawn shape and the rules). Give it
	/// <see cref="ItemsSource"/> of <see cref="TabEntry"/> and bind <see cref="SelectedIndex"/> two-way; the
	/// host swaps its own body on that index. <see cref="Placement"/> flips the rail between top and side.
	/// </summary>
	public sealed partial class TabStrip : UserControl
	{
		public TabStrip()
		{
			InitializeComponent();
			// Re-assert once loaded (a placement set by a host before load is already applied; this is cheap).
			Loaded += (_, _) => ApplyPlacement();
		}

		/// <summary>The tabs, in strip order. Items must be <see cref="TabEntry"/>.</summary>
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
			strip.ApplyPlacement();
			strip.ApplySelection();
		}

		/// <summary>The selected tab's index. Two-way bindable; an out-of-range value selects nothing rather
		/// than throwing (the host is responsible for clamping — see MapViewModel.SettingsTabIndex).</summary>
		public int SelectedIndex
		{
			get => (int)GetValue(SelectedIndexProperty);
			set => SetValue(SelectedIndexProperty, value);
		}

		public static readonly DependencyProperty SelectedIndexProperty =
			DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(TabStrip),
				new PropertyMetadata(0, (d, _) => ((TabStrip)d).ApplySelection()));

		/// <summary>Top rail (default) or side rail.</summary>
		public TabPlacement Placement
		{
			get => (TabPlacement)GetValue(PlacementProperty);
			set => SetValue(PlacementProperty, value);
		}

		public static readonly DependencyProperty PlacementProperty =
			DependencyProperty.Register(nameof(Placement), typeof(TabPlacement), typeof(TabStrip),
				new PropertyMetadata(TabPlacement.Top, (d, _) => ((TabStrip)d).ApplyPlacement()));

		/// <summary>SEATED top rail: the strip sits at the bottom of a raised band, and the selected tab is filled
		/// with the GROUND (the body's surface) and squared off at the bottom, so it reads as an opening into the
		/// body below — no underline. The Anvil Atlas's title band. Off = the Settings look.</summary>
		public bool IsSeated
		{
			get => (bool)GetValue(IsSeatedProperty);
			set => SetValue(IsSeatedProperty, value);
		}

		public static readonly DependencyProperty IsSeatedProperty =
			DependencyProperty.Register(nameof(IsSeated), typeof(bool), typeof(TabStrip),
				new PropertyMetadata(false, (d, _) => ((TabStrip)d).ApplyPlacement()));

		/// <summary>The key style for an entry — a static the item template can call (see <see cref="Show"/>).
		/// Styles are safe to resolve in C#; only theme BRUSHES aren't, and those stay in the style's states.</summary>
		public static Style KeyStyle(bool seated) =>
			(Style)Application.Current.Resources[seated ? "SeatedTabKeyStyle" : "SettingsTabKeyStyle"];

		/// <summary>Bool → Visibility for the item template's two indicators. A static so a DataTemplate can
		/// call it (x:Bind inside a template resolves against the ENTRY, so an instance method here is out of
		/// reach).</summary>
		public static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>The inverse of <see cref="Show"/>: a SEATED tab (the Atlas) shows its mark-beside-name
		/// arrangement and hides the mark-over-name one; both are declared in the item template.</summary>
		public static Visibility Hide(bool on) => on ? Visibility.Collapsed : Visibility.Visible;

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

		// Stack direction + which indicator each entry shows. Both follow from Placement, and both have to be
		// re-applied when the items change (a fresh entry defaults to horizontal).
		private void ApplyPlacement()
		{
			bool vertical = Placement == TabPlacement.Left;

			foreach (var entry in Entries)
			{
				entry.IsVertical = vertical;
				entry.IsSeated = IsSeated && !vertical;
			}

			// Pick the declared panel for this direction. (Swapping the TEMPLATE rather than turning the realized
			// panel also retires the old race: ItemsPanelRoot was null until the panel realized, and a side-rail
			// restore could land before it and come back on top.)
			var panel = (ItemsPanelTemplate)Resources[vertical ? "SideRailPanel" : "TopRailPanel"];
			if (!ReferenceEquals(Host.ItemsPanel, panel)) Host.ItemsPanel = panel;
		}

		private void OnTabClick(object sender, RoutedEventArgs e)
		{
			if (sender is not ToggleButton button || button.DataContext is not TabEntry entry) return;

			int index = Entries.ToList().IndexOf(entry);
			if (index >= 0) SelectedIndex = index;

			// ⚠️ RE-ASSERT, do not delete. The click already flipped IsChecked locally, and a OneWay binding
			// only pushes when the SOURCE changes — so clicking the tab you are already on would leave it
			// dark with nothing to turn it back on. (The pane-layout key hit this same wall as three
			// exclusive toggles; it escaped by becoming a control whose value always moves. A tab strip
			// cannot, so it re-asserts instead.)
			button.IsChecked = entry.IsSelected;
		}
	}
}
