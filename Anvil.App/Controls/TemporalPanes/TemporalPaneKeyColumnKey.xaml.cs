using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Anvil.Controls.TemporalPanes
{
	/// <summary>
	/// One icon key of a minimized temporal pane (see the XAML header): click = the layer's show/hide, right-click = its
	/// opacity row, which hides itself <see cref="AutoHide"/> after the pointer and focus leave it.
	/// </summary>
	public sealed partial class TemporalPaneKeyColumnKey : UserControl
	{
		/// <summary>The opacity row's auto-hide delay (the user's call, 2026-11-21).</summary>
		private static readonly TimeSpan AutoHide = TimeSpan.FromSeconds(3);

		private readonly DispatcherTimer _hide = new() { Interval = AutoHide };
		private bool _pointerInRow, _focusInRow;

		public TemporalPaneKeyColumnKey()
		{
			InitializeComponent();
			_hide.Tick += (_, _) => { _hide.Stop(); OpacityFlyout.Hide(); };
			// ⚠️ GoToState is a no-op before the template applies — re-applied on Loaded.
			Loaded += (_, _) => ApplyItem();
		}

		/// <summary>The layer this key is (one entry of the column's list).</summary>
		public TemporalPaneKeyColumnItem? Item
		{
			get => (TemporalPaneKeyColumnItem?)GetValue(ItemProperty);
			set => SetValue(ItemProperty, value);
		}
		public static readonly DependencyProperty ItemProperty =
			DependencyProperty.Register(nameof(Item), typeof(TemporalPaneKeyColumnItem), typeof(TemporalPaneKeyColumnKey),
				new PropertyMetadata(null, (d, e) => ((TemporalPaneKeyColumnKey)d).OnItemChanged(
					e.OldValue as TemporalPaneKeyColumnItem, e.NewValue as TemporalPaneKeyColumnItem)));

		private void OnItemChanged(TemporalPaneKeyColumnItem? old, TemporalPaneKeyColumnItem? item)
		{
			if (old is not null) { old.PropertyChanged -= OnItemPropertyChanged; }
			if (item is not null) { item.PropertyChanged += OnItemPropertyChanged; }
			// The icon is the layer's DATA (a path string), set once per item — the Path itself is declared in the XAML.
			KeyIcon.Data = item is null ? null : (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), item.IconData);
			ApplyItem();
		}

		private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(TemporalPaneKeyColumnItem.IsChecked) or nameof(TemporalPaneKeyColumnItem.Name)) { ApplyItem(); }
		}

		private void ApplyItem()
		{
			var state = Item?.IsChecked switch { true => "On", null when Item is not null => "Partial", _ => "Off" };
			VisualStateManager.GoToState(this, state, false);
			AutomationProperties.SetName(this, Item?.Name ?? "");
		}

		// ── Show / hide ─────────────────────────────────────────────────────────────────────────────────

		private void OnTapped(object sender, TappedRoutedEventArgs e)
		{
			e.Handled = true;
			Item?.Toggle();
		}

		private void OnKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key is VirtualKey.Enter or VirtualKey.Space)
			{
				e.Handled = true;
				Item?.Toggle();
			}
		}

		private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => VisualStateManager.GoToState(this, "Over", false);
		private void OnPointerExited(object sender, PointerRoutedEventArgs e) => VisualStateManager.GoToState(this, "Rest", false);
		private void OnPointerPressed(object sender, PointerRoutedEventArgs e) => VisualStateManager.GoToState(this, "Pressed", false);
		private void OnPointerReleased(object sender, PointerRoutedEventArgs e) => VisualStateManager.GoToState(this, "Over", false);

		// ── Opacity row (right-click / Shift+F10 / Menu key) ────────────────────────────────────────────

		private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
		{
			e.Handled = true;
			if (Item is not { HasOpacity: true }) { return; }
			OpacityFlyout.ShowAt(this);
		}

		private void OnFlyoutOpened(object? sender, object e)
		{
			_pointerInRow = _focusInRow = false;
			ArmHide(); // a row nobody reaches closes on its own
		}

		private void OnFlyoutClosed(object? sender, object e) => _hide.Stop();

		private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e) { _pointerInRow = true; _hide.Stop(); }
		private void OnRowPointerExited(object sender, PointerRoutedEventArgs e) { _pointerInRow = false; ArmHide(); }
		// Only KEYBOARD focus holds the row: a right-click opening puts focus in it too (programmatically), and that must
		// not stop the countdown, or a mouse-opened row would never hide.
		private void OnRowGotFocus(object sender, RoutedEventArgs e)
		{
			if (e.OriginalSource is not Control { FocusState: FocusState.Keyboard }) { return; }
			_focusInRow = true;
			_hide.Stop();
		}
		private void OnRowLostFocus(object sender, RoutedEventArgs e) { _focusInRow = false; ArmHide(); }

		// The countdown runs only while NEITHER the pointer nor the focus is in the row; each arm restarts it.
		private void ArmHide()
		{
			_hide.Stop();
			if (!_pointerInRow && !_focusInRow && OpacityFlyout.IsOpen) { _hide.Start(); }
		}
	}
}
