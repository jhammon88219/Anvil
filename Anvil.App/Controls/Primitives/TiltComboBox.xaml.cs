using System.Collections.Generic;
using System.Collections.Specialized;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Anvil.ViewModels;
using Windows.System;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The radar Tilt (elevation) selector — a background-less dropdown that blends into its host surface
	/// (see TiltComboBox.xaml for the look and what it replaced).
	///
	/// Host contract: bind <see cref="ItemsSource"/> to the VM's tilt list and <see cref="SelectedIndex"/>
	/// two-way to its tilt index.
	/// </summary>
	public sealed partial class TiltComboBox : UserControl
	{
		// Guards the two-way echo between our SelectedIndex and the dropdown ListView's own selection.
		private bool _syncingSelection;

		public TiltComboBox()
		{
			InitializeComponent();
			// WinUI has no public Cursor property — ProtectedCursor is set from inside the control itself.
			ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
		}

		/// <summary>The selectable elevations. Bound to the VM's tilt list.</summary>
		public IReadOnlyList<RadarTiltOption>? ItemsSource
		{
			get => (IReadOnlyList<RadarTiltOption>?)GetValue(ItemsSourceProperty);
			set => SetValue(ItemsSourceProperty, value);
		}

		public static readonly DependencyProperty ItemsSourceProperty =
			DependencyProperty.Register(nameof(ItemsSource), typeof(IReadOnlyList<RadarTiltOption>),
				typeof(TiltComboBox), new PropertyMetadata(null, OnItemsSourceChanged));

		/// <summary>Index of the selected elevation. Two-way bindable to the VM's tilt index.</summary>
		public int SelectedIndex
		{
			get => (int)GetValue(SelectedIndexProperty);
			set => SetValue(SelectedIndexProperty, value);
		}

		public static readonly DependencyProperty SelectedIndexProperty =
			DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(TiltComboBox),
				new PropertyMetadata(-1, OnSelectionInputChanged));

		/// <summary>The selected elevation, resolved from ItemsSource + SelectedIndex. Drives the label.</summary>
		public RadarTiltOption? SelectedItem
		{
			get => (RadarTiltOption?)GetValue(SelectedItemProperty);
			private set => SetValue(SelectedItemProperty, value);
		}

		public static readonly DependencyProperty SelectedItemProperty =
			DependencyProperty.Register(nameof(SelectedItem), typeof(RadarTiltOption),
				typeof(TiltComboBox), new PropertyMetadata(null));

		// ⚠️ The tilt list is REBUILT IN PLACE, not replaced: on a VCP change the VM clears and refills the
		// same ObservableCollection (RadarViewModel.RadarTiltOptions), so the ItemsSource DP never changes
		// and a DP-only resolve would leave the label showing an elevation that is no longer in the list.
		// Track the collection itself so the label follows a rebuild.
		private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var self = (TiltComboBox)d;

			if (e.OldValue is INotifyCollectionChanged oldItems)
			{
				oldItems.CollectionChanged -= self.OnItemsCollectionChanged;
			}
			if (e.NewValue is INotifyCollectionChanged newItems)
			{
				newItems.CollectionChanged += self.OnItemsCollectionChanged;
			}

			self.ResolveSelectedItem();
		}

		private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
			ResolveSelectedItem();

		private static void OnSelectionInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
			((TiltComboBox)d).ResolveSelectedItem();

		private void ResolveSelectedItem()
		{
			var items = ItemsSource;
			var i = SelectedIndex;
			SelectedItem = items is not null && i >= 0 && i < items.Count ? items[i] : null;
		}

		// ── Dropdown ─────────────────────────────────────────────────────────────────────────────
		private void OnTapped(object sender, TappedRoutedEventArgs e) => OpenDropDown();

		// Keyboard parity with a combo box: Space/Enter opens it (the ListView takes arrow keys + Enter
		// from there).
		private void OnKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key is not (VirtualKey.Space or VirtualKey.Enter)) return;
			OpenDropDown();
			e.Handled = true;
		}

		private void OpenDropDown()
		{
			if (!IsEnabled || ItemsSource is not { Count: > 0 }) return;
			DropDown.ShowAt(Root);
		}

		// Match the dropdown to the closed control's width (less the presenter's 1px border each side), and
		// sync the ListView to the current elevation.
		private void OnDropDownOpening(object? sender, object e)
		{
			TiltList.Width = System.Math.Max(0, Root.ActualWidth - 2);

			_syncingSelection = true;
			TiltList.SelectedIndex = SelectedIndex;
			_syncingSelection = false;
		}

		private void OnTiltListSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (_syncingSelection) return;

			// A rebuild of the list clears the ListView's selection; that is not a user pick, so it must not
			// close the dropdown or write an index back.
			var i = TiltList.SelectedIndex;
			if (i < 0) return;

			if (i != SelectedIndex) SelectedIndex = i;
			DropDown.Hide();
		}

		// ── Visual states ────────────────────────────────────────────────────────────────────────
		// Nothing reacts to hover or press by design; Disabled is the only state that changes anything.
		private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) =>
			VisualStateManager.GoToState(this, IsEnabled ? "Normal" : "Disabled", true);
	}
}
