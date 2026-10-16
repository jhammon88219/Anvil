using System;
using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Anvil.Controls.TemporalWindows.DragMoveLayerExpanders
{
	/// <summary>
	/// One MAP LAYER in a temporal window — a fixed-height header over a collapsible <see cref="Body"/>, dragged
	/// by its grip to re-order (see DragMoveLayerExpander.xaml). ⚠️ Lives ONLY in a
	/// <see cref="DragMoveLayerExpanderHost"/>, which owns the order, the drag and the persistence; outside one the
	/// grip does nothing. Non-layer sections (Settings) are <see cref="SettingsExpander"/>.
	/// </summary>
	[ContentProperty(Name = nameof(Body))]
	public sealed partial class DragMoveLayerExpander : UserControl
	{
		public DragMoveLayerExpander()
		{
			InitializeComponent();
			// handledEventsToo: the slider and the box mark arrow keys handled before they bubble here.
			AddHandler(KeyDownEvent, new KeyEventHandler(OnExpanderKeyDown), true);
			Loaded += (_, _) => VisualStateManager.GoToState(this, "Normal", false);
		}

		// ── Properties ───────────────────────────────────────────────────────────────────────────────

		/// <summary>The layer's title.</summary>
		public string Header
		{
			get => (string)GetValue(HeaderProperty);
			set => SetValue(HeaderProperty, value);
		}
		public static readonly DependencyProperty HeaderProperty =
			DependencyProperty.Register(nameof(Header), typeof(string), typeof(DragMoveLayerExpander), new PropertyMetadata(""));

		/// <summary>The expander's content. Null = a header-only layer (no chevron, no toggling).</summary>
		public object? Body
		{
			get => GetValue(BodyProperty);
			set => SetValue(BodyProperty, value);
		}
		public static readonly DependencyProperty BodyProperty =
			DependencyProperty.Register(nameof(Body), typeof(object), typeof(DragMoveLayerExpander), new PropertyMetadata(null));

		/// <summary>Whether the body is showing. Persisted only through <see cref="PersistExpansion"/>.</summary>
		public bool IsExpanded
		{
			get => (bool)GetValue(IsExpandedProperty);
			set => SetValue(IsExpandedProperty, value);
		}
		public static readonly DependencyProperty IsExpandedProperty =
			DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(DragMoveLayerExpander), new PropertyMetadata(false));

		/// <summary>
		/// REQUIRED: the map overlay this expander IS (a <c>Models.LayerOrder</c> id ⇄ <c>layers.js GROUPS</c>).
		/// Its position among its siblings is the layer's place in the map stack, and it keys the persisted
		/// open/closed state.
		/// </summary>
		public string LayerId
		{
			get => (string)GetValue(LayerIdProperty);
			set => SetValue(LayerIdProperty, value);
		}
		public static readonly DependencyProperty LayerIdProperty =
			DependencyProperty.Register(nameof(LayerId), typeof(string), typeof(DragMoveLayerExpander), new PropertyMetadata(""));

		/// <summary>Show the header check box (a select-all, or the layer's show/hide).</summary>
		public bool ShowCheckBox
		{
			get => (bool)GetValue(ShowCheckBoxProperty);
			set => SetValue(ShowCheckBoxProperty, value);
		}
		public static readonly DependencyProperty ShowCheckBoxProperty =
			DependencyProperty.Register(nameof(ShowCheckBox), typeof(bool), typeof(DragMoveLayerExpander), new PropertyMetadata(false));

		/// <summary>
		/// The box's state — bind ONE-WAY. The click goes to the view model through <see cref="CheckBoxClick"/>
		/// and its answer comes back here; null draws the partial [-].
		/// </summary>
		public bool? IsChecked
		{
			get => (bool?)GetValue(IsCheckedProperty);
			set => SetValue(IsCheckedProperty, value);
		}
		public static readonly DependencyProperty IsCheckedProperty =
			DependencyProperty.Register(nameof(IsChecked), typeof(bool?), typeof(DragMoveLayerExpander), new PropertyMetadata(false));

		public bool IsCheckBoxEnabled
		{
			get => (bool)GetValue(IsCheckBoxEnabledProperty);
			set => SetValue(IsCheckBoxEnabledProperty, value);
		}
		public static readonly DependencyProperty IsCheckBoxEnabledProperty =
			DependencyProperty.Register(nameof(IsCheckBoxEnabled), typeof(bool), typeof(DragMoveLayerExpander), new PropertyMetadata(true));

		public string CheckBoxToolTip
		{
			get => (string)GetValue(CheckBoxToolTipProperty);
			set => SetValue(CheckBoxToolTipProperty, value);
		}
		public static readonly DependencyProperty CheckBoxToolTipProperty =
			DependencyProperty.Register(nameof(CheckBoxToolTip), typeof(string), typeof(DragMoveLayerExpander), new PropertyMetadata(null));

		/// <summary>Show the opacity slider + readout in the header.</summary>
		public bool ShowOpacity
		{
			get => (bool)GetValue(ShowOpacityProperty);
			set => SetValue(ShowOpacityProperty, value);
		}
		public static readonly DependencyProperty ShowOpacityProperty =
			DependencyProperty.Register(nameof(ShowOpacity), typeof(bool), typeof(DragMoveLayerExpander), new PropertyMetadata(false));

		/// <summary>The layer's opacity, 0-1. Bind TWO-WAY to the view model's value.</summary>
		public double OpacityValue
		{
			get => (double)GetValue(OpacityValueProperty);
			set => SetValue(OpacityValueProperty, value);
		}
		public static readonly DependencyProperty OpacityValueProperty =
			DependencyProperty.Register(nameof(OpacityValue), typeof(double), typeof(DragMoveLayerExpander), new PropertyMetadata(1.0));

		/// <summary>False while the layer draws nothing — the slider greys and the readout dims.</summary>
		public bool IsOpacityEnabled
		{
			get => (bool)GetValue(IsOpacityEnabledProperty);
			set => SetValue(IsOpacityEnabledProperty, value);
		}
		public static readonly DependencyProperty IsOpacityEnabledProperty =
			DependencyProperty.Register(nameof(IsOpacityEnabled), typeof(bool), typeof(DragMoveLayerExpander), new PropertyMetadata(true));

		public string OpacityToolTip
		{
			get => (string)GetValue(OpacityToolTipProperty);
			set => SetValue(OpacityToolTipProperty, value);
		}
		public static readonly DependencyProperty OpacityToolTipProperty =
			DependencyProperty.Register(nameof(OpacityToolTip), typeof(string), typeof(DragMoveLayerExpander), new PropertyMetadata(null));

		/// <summary>
		/// A GHOST row: another window's layer, shown here only so this window's layers can be ordered around it
		/// (NowCast ⇄ ForeCast share one map stack). Everything but the GRIP dims heavily and takes no input —
		/// the drag is the only thing a ghost is for. Give it no Body.
		/// </summary>
		public bool IsGhost
		{
			get => (bool)GetValue(IsGhostProperty);
			set => SetValue(IsGhostProperty, value);
		}
		public static readonly DependencyProperty IsGhostProperty =
			DependencyProperty.Register(nameof(IsGhost), typeof(bool), typeof(DragMoveLayerExpander), new PropertyMetadata(false));

		/// <summary>The header box was clicked. The handler decides (VM); see <see cref="IsChecked"/>.</summary>
		public event RoutedEventHandler? CheckBoxClick;

		// ── x:Bind functions (layout) ────────────────────────────────────────────────────────────────

		private const double BoxColumn = 28, TitleColumn = 118, PercentColumn = 44;

		public GridLength BoxWidth(bool showBox) => new(showBox ? BoxColumn : 0);
		public GridLength TitleWidth(bool showOpacity) =>
			showOpacity ? new GridLength(TitleColumn) : new GridLength(1, GridUnitType.Star);
		public GridLength SliderWidth(bool showOpacity) =>
			showOpacity ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
		public GridLength PercentWidth(bool showOpacity) => new(showOpacity ? PercentColumn : 0);

		// Round only the header's top corners while the body shows beneath it.
		public CornerRadius HeaderCorners(bool expanded, object? body) =>
			expanded && body is not null ? new CornerRadius(3, 3, 0, 0) : new CornerRadius(3);

		public Visibility ChevronVisibility(object? body) => BoolVisibility(body is not null);
		public Visibility BodyVisibility(bool expanded, object? body) => BoolVisibility(expanded && body is not null);

		// LAZY BODY (2026-10-10): the body is handed to the presenter only once the expander has been EXPANDED, and
		// then kept (collapsing again just hides it, so its state and scroll survive and a re-open costs nothing).
		// Building the launch's Now window — every expander collapsed — still froze the UI ~1 s, because each
		// collapsed body was in the live tree, loading and setting up controls nobody could see. The body is still
		// CONSTRUCTED by its host's XAML (its x:Bind bindings live there); it just isn't attached until it's wanted.
		private bool _bodyAttached;

		public object? PresentedBody(bool expanded, object? body)
		{
			if (expanded) _bodyAttached = true;
			return _bodyAttached ? body : null;
		}

		// FIRST LAYOUT per expander, logged (radar diag "section.measure", ≥ 5 ms) — what each costs when its window opens.
		private bool _measured;

		protected override Size MeasureOverride(Size availableSize)
		{
			if (_measured) return base.MeasureOverride(availableSize);
			_measured = true;
			var clock = System.Diagnostics.Stopwatch.StartNew();
			var size = base.MeasureOverride(availableSize);
			if (clock.ElapsedMilliseconds >= 5)
			{
				Anvil.Services.RadarDiagnostics.Log("ui", "section.measure", ("section", Header),
					("ms", clock.ElapsedMilliseconds), ("expanded", IsExpanded), ("bodyAttached", _bodyAttached));
			}
			return size;
		}
		public Visibility BoolVisibility(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
		public double ChevronAngle(bool expanded) => expanded ? 90 : 0;

		public string Percent(double opacity) =>
			Math.Round(opacity * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
		// A TextBlock has no disabled state; the readout dims through Opacity (no theme brush in code).
		public double DimUnless(bool enabled) => enabled ? 1.0 : 0.4;

		// A GHOST row dims everything but its grip this far, and those cells stop taking input.
		private const double GhostOpacity = 0.25;
		public double GhostDim(bool ghost) => ghost ? GhostOpacity : 1.0;
		public double GhostDimUnless(bool enabled, bool ghost) => DimUnless(enabled) * GhostDim(ghost);
		public bool NotGhost(bool ghost) => !ghost;

		public string ChevronName(string header, bool expanded) => (expanded ? "Collapse " : "Expand ") + header;
		public string CheckBoxName(string header) => "Show " + header;
		public string OpacityName(string header) => header + " opacity";

		// ── Header: expand / collapse, hover, the box ────────────────────────────────────────────────

		private void OnChevronClick(object sender, RoutedEventArgs e) => IsExpanded = !IsExpanded;

		private void OnHeaderTapped(object sender, TappedRoutedEventArgs e)
		{
			if (Body is null || IsGhost) { return; }
			if (e.OriginalSource is DependencyObject src &&
				(IsWithin(src, Grip) || IsWithin(src, ChevronButton) || IsWithin(src, HeaderBox) || IsWithin(src, OpacitySlider)))
			{
				return; // those have gestures of their own
			}
			// ⚠️ A DISABLED box is not hit-testable, so its tap lands on the header grid itself and the source
			// test above misses it — the box's footprint is tested by POSITION instead, so clicking a greyed box
			// never opens the expander.
			if (ShowCheckBox && IsOver(e.GetPosition(HeaderBox), HeaderBox)) { return; }
			IsExpanded = !IsExpanded;
		}

		private static bool IsOver(Point p, FrameworkElement element) =>
			p.X >= 0 && p.Y >= 0 && p.X <= element.ActualWidth && p.Y <= element.ActualHeight;

		private static bool IsWithin(DependencyObject src, DependencyObject container)
		{
			for (var d = src; d is not null; d = VisualTreeHelper.GetParent(d))
			{
				if (ReferenceEquals(d, container)) { return true; }
			}
			return false;
		}

		private void OnHeaderPointerEntered(object sender, PointerRoutedEventArgs e)
		{
			if (!_dragging) { VisualStateManager.GoToState(this, "PointerOver", false); }
		}

		private void OnHeaderPointerExited(object sender, PointerRoutedEventArgs e)
		{
			if (!_dragging) { VisualStateManager.GoToState(this, "Normal", false); }
		}

		private void OnHeaderBoxClick(object sender, RoutedEventArgs e)
		{
			CheckBoxClick?.Invoke(this, e);
			// The box toggled itself before Click; if the VM's answer left IsChecked where it was, no change
			// notification arrives to put it back — so put it back here.
			HeaderBox.IsChecked = IsChecked;
		}

		// ── Grip: reports to the host ────────────────────────────────────────────────────────────────
		// The drag itself — bounds, slots, siblings stepping aside, the final move, Reordered — is the HOST's
		// (TemporalWindows/DragMoveLayerExpanders/DragMoveLayerExpanderHost). This side only owns the pointer: cursor, capture, the Lifted look.

		private DragMoveLayerExpanderHost? Host => Parent as DragMoveLayerExpanderHost;

		private bool _dragging;

		private void OnGripPointerEntered(object sender, PointerRoutedEventArgs e) =>
			ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);

		private void OnGripPointerExited(object sender, PointerRoutedEventArgs e)
		{
			if (!_dragging) { ProtectedCursor = null; }
		}

		private void OnGripPressed(object sender, PointerRoutedEventArgs e)
		{
			if (Host is not { } host) { return; }
			if (!e.GetCurrentPoint(Grip).Properties.IsLeftButtonPressed) { return; }
			if (!host.BeginDrag(this, e.GetCurrentPoint(host).Position.Y)) { return; }
			_dragging = true;
			VisualStateManager.GoToState(this, "Lifted", false);
			Grip.CapturePointer(e.Pointer);
			e.Handled = true;
		}

		private void OnGripMoved(object sender, PointerRoutedEventArgs e)
		{
			if (_dragging && Host is { } host) { host.DragTo(e.GetCurrentPoint(host).Position.Y); }
		}

		private void OnGripReleased(object sender, PointerRoutedEventArgs e)
		{
			Grip.ReleasePointerCapture(e.Pointer);
			EndDrag();
		}

		private void OnGripCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag();

		private void EndDrag()
		{
			if (!_dragging) { return; }
			// Cleared FIRST: the host's Children.Move can cost the grip its capture, which re-enters here.
			_dragging = false;
			ProtectedCursor = null;
			VisualStateManager.GoToState(this, "Normal", false);
			Host?.EndDrag();
		}

		private void OnExpanderKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (_dragging || (e.Key != VirtualKey.Up && e.Key != VirtualKey.Down)) { return; }
			if (!InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down)) { return; }
			if (Host is not { } host) { return; }
			e.Handled = true;

			var focused = FocusManager.GetFocusedElement(XamlRoot) as Control;
			if (!host.Step(this, e.Key == VirtualKey.Up ? -1 : 1)) { return; }
			// A move can drop keyboard focus; give it back once the expander is laid out again.
			DispatcherQueue.TryEnqueue(() => focused?.Focus(FocusState.Keyboard));
		}
	}
}

