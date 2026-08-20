using System.Collections.Generic;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Anvil.ViewModels;
using Windows.System;

namespace Anvil.Controls
{
	/// <summary>
	/// The radar Product selector, drawing each product's color ramp beside its name so the selector doubles
	/// as the legend (see ProductColorRampComboBox.xaml for the two presentations and why this is a
	/// UserControl rather than a templated ComboBox).
	///
	/// Host contract: bind <see cref="ItemsSource"/> to the product list and <see cref="SelectedIndex"/>
	/// two-way to the VM's product index, then feed the live inspect read-out in through
	/// <see cref="InspectFraction"/> / <see cref="IsInspectVisible"/> — plain DPs, which is what replaced the
	/// attached properties the old ControlTemplate needed.
	/// </summary>
	public sealed partial class ProductColorRampComboBox : UserControl
	{
		// Guards the two-way echo between our SelectedIndex and the dropdown ListView's own selection.
		private bool _syncingSelection;
		private bool _isPointerOver;
		private bool _isPressed;

		public ProductColorRampComboBox()
		{
			InitializeComponent();
			// WinUI has no public Cursor property — ProtectedCursor is set from inside the control itself.
			// Children inherit it, so the ramp reads as clickable too.
			ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
		}

		/// <summary>The selectable products. Bound to the VM's product list.</summary>
		public IReadOnlyList<RadarProductOption>? ItemsSource
		{
			get => (IReadOnlyList<RadarProductOption>?)GetValue(ItemsSourceProperty);
			set => SetValue(ItemsSourceProperty, value);
		}

		public static readonly DependencyProperty ItemsSourceProperty =
			DependencyProperty.Register(nameof(ItemsSource), typeof(IReadOnlyList<RadarProductOption>),
				typeof(ProductColorRampComboBox), new PropertyMetadata(null, OnSelectionInputChanged));

		/// <summary>Index of the selected product. Two-way bindable to the VM's product index.</summary>
		public int SelectedIndex
		{
			get => (int)GetValue(SelectedIndexProperty);
			set => SetValue(SelectedIndexProperty, value);
		}

		public static readonly DependencyProperty SelectedIndexProperty =
			DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(ProductColorRampComboBox),
				new PropertyMetadata(-1, OnSelectionInputChanged));

		/// <summary>The selected product, resolved from ItemsSource + SelectedIndex. Drives the closed area.</summary>
		public RadarProductOption? SelectedItem
		{
			get => (RadarProductOption?)GetValue(SelectedItemProperty);
			private set => SetValue(SelectedItemProperty, value);
		}

		public static readonly DependencyProperty SelectedItemProperty =
			DependencyProperty.Register(nameof(SelectedItem), typeof(RadarProductOption),
				typeof(ProductColorRampComboBox), new PropertyMetadata(null));

		/// <summary>Where the Inspect marker sits along the selected product's ramp (0-1).</summary>
		public double InspectFraction
		{
			get => (double)GetValue(InspectFractionProperty);
			set => SetValue(InspectFractionProperty, value);
		}

		public static readonly DependencyProperty InspectFractionProperty =
			DependencyProperty.Register(nameof(InspectFraction), typeof(double), typeof(ProductColorRampComboBox),
				new PropertyMetadata(0.0));

		/// <summary>Whether the Inspect marker is shown (inspect mode on + a value under the cursor).</summary>
		public bool IsInspectVisible
		{
			get => (bool)GetValue(IsInspectVisibleProperty);
			set => SetValue(IsInspectVisibleProperty, value);
		}

		public static readonly DependencyProperty IsInspectVisibleProperty =
			DependencyProperty.Register(nameof(IsInspectVisible), typeof(bool), typeof(ProductColorRampComboBox),
				new PropertyMetadata(false));

		// SelectedItem is derived from the two inputs, so re-resolve it when either lands (the list arrives
		// from the VM after the index in practice, so this can't assume an order).
		private static void OnSelectionInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
			((ProductColorRampComboBox)d).ResolveSelectedItem();

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

		// Match the dropdown to the closed control's width (less the presenter's 1px border each side) so the
		// rows' ramps line up with the one in the closed area, and sync the ListView to the current product.
		private void OnDropDownOpening(object? sender, object e)
		{
			ProductList.Width = System.Math.Max(0, Root.ActualWidth - 2);

			_syncingSelection = true;
			ProductList.SelectedIndex = SelectedIndex;
			_syncingSelection = false;
		}

		private void OnProductListSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (_syncingSelection) return;

			// A cleared selection is not a user pick, so it must not close the dropdown or write back.
			var i = ProductList.SelectedIndex;
			if (i < 0) return;

			if (i != SelectedIndex) SelectedIndex = i;
			DropDown.Hide();
		}

		// ── Visual states ────────────────────────────────────────────────────────────────────────
		private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
		{
			_isPointerOver = true;
			UpdateVisualState();
		}

		private void OnPointerExited(object sender, PointerRoutedEventArgs e)
		{
			_isPointerOver = false;
			_isPressed = false;
			UpdateVisualState();
		}

		private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
		{
			_isPressed = true;
			UpdateVisualState();
		}

		private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
		{
			_isPressed = false;
			UpdateVisualState();
		}

		private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateVisualState();

		private void UpdateVisualState()
		{
			var state = !IsEnabled ? "Disabled"
				: _isPressed ? "Pressed"
				: _isPointerOver ? "PointerOver"
				: "Normal";
			VisualStateManager.GoToState(this, state, true);
		}
	}
}
