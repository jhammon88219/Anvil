using System;
using System.Collections.Generic;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Anvil.ViewModels;
using Windows.System;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The state-isolation picker on the map controls strip (see the XAML header for the look and, more
	/// importantly, for why it is not a <c>ComboBox</c>).
	///
	/// Host contract: bind <see cref="ItemsSource"/> to the VM's option list and <see cref="SelectedItem"/>
	/// two-way to its selected option; <see cref="PlaceholderText"/> shows when that is null.
	/// </summary>
	public sealed partial class IsolationComboBox : UserControl
	{
		// Guards the echo between our SelectedItem and the dropdown ListView's own selection.
		private bool _syncingSelection;

		public IsolationComboBox()
		{
			InitializeComponent();
			// WinUI has no public Cursor property — ProtectedCursor is set from inside the control itself.
			ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
		}

		/// <summary>The rows: the three isolation actions, then the 52 places.</summary>
		public IReadOnlyList<StateIsolationOption>? ItemsSource
		{
			get => (IReadOnlyList<StateIsolationOption>?)GetValue(ItemsSourceProperty);
			set => SetValue(ItemsSourceProperty, value);
		}

		public static readonly DependencyProperty ItemsSourceProperty =
			DependencyProperty.Register(nameof(ItemsSource), typeof(IReadOnlyList<StateIsolationOption>),
				typeof(IsolationComboBox), new PropertyMetadata(null));

		/// <summary>
		/// The selected row, or NULL when nothing is masked — which is the state the placeholder shows for.
		/// </summary>
		/// <remarks>
		/// ⚠️ The VM's <c>SelectedIsolationOption</c> is DERIVED (it resolves the CONUS / armed / isolated
		/// flags into one row) and can therefore change WITHOUT the user touching this control — a map click
		/// while armed is the everyday case. So this must stay a two-way binding that also accepts pushes
		/// from below, not a write-only report of what was picked here.
		/// </remarks>
		public StateIsolationOption? SelectedItem
		{
			get => (StateIsolationOption?)GetValue(SelectedItemProperty);
			set => SetValue(SelectedItemProperty, value);
		}

		public static readonly DependencyProperty SelectedItemProperty =
			DependencyProperty.Register(nameof(SelectedItem), typeof(StateIsolationOption),
				typeof(IsolationComboBox), new PropertyMetadata(null));

		/// <summary>Closed-state text while <see cref="SelectedItem"/> is null.</summary>
		public string PlaceholderText
		{
			get => (string)GetValue(PlaceholderTextProperty);
			set => SetValue(PlaceholderTextProperty, value);
		}

		public static readonly DependencyProperty PlaceholderTextProperty =
			DependencyProperty.Register(nameof(PlaceholderText), typeof(string),
				typeof(IsolationComboBox), new PropertyMetadata(string.Empty));

		// ── Closed-state face ────────────────────────────────────────────────────────────────────
		public string LabelOf(StateIsolationOption? item) => item?.Label ?? string.Empty;

		public Visibility HasSelection(StateIsolationOption? item) =>
			item is null ? Visibility.Collapsed : Visibility.Visible;

		public Visibility NoSelection(StateIsolationOption? item) =>
			item is null ? Visibility.Visible : Visibility.Collapsed;

		// ── Dropdown ─────────────────────────────────────────────────────────────────────────────
		private void OnTapped(object sender, TappedRoutedEventArgs e) => OpenDropDown();

		// Keyboard parity with a combo box: Space/Enter opens it (the ListView takes arrow keys + Enter
		// from there).
		private void OnKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key is not (VirtualKey.Space or VirtualKey.Enter)) { return; }
			OpenDropDown();
			e.Handled = true;
		}

		private void OpenDropDown()
		{
			if (!IsEnabled || ItemsSource is not { Count: > 0 }) { return; }
			DropDown.ShowAt(Root);
		}

		// Match the dropdown to the closed control's width (less the presenter's 1px border each side), and
		// sync the list to the current row.
		private void OnDropDownOpening(object? sender, object e)
		{
			OptionList.Width = Math.Max(0, Root.ActualWidth - 2);

			_syncingSelection = true;
			OptionList.SelectedItem = SelectedItem;   // null clears it, which is the placeholder state
			_syncingSelection = false;
		}

		private void OnOptionListSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (_syncingSelection) { return; }

			// A cleared selection is not a user pick, so it must not close the dropdown or write back.
			if (OptionList.SelectedItem is not StateIsolationOption picked) { return; }

			// ⚠️ Write even when it matches: picking "No Isolation" resolves BACK to null in the VM, so the
			// two can be equal here while the map still has to change. The VM's own setter is the guard
			// against redundant work, not this.
			SelectedItem = picked;
			DropDown.Hide();
		}

		// ── Visual states ────────────────────────────────────────────────────────────────────────
		private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) =>
			VisualStateManager.GoToState(this, IsEnabled ? "Normal" : "Disabled", true);
	}
}
