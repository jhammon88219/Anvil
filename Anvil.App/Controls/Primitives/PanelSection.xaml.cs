using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// One section of a panel — a fixed-height header over a collapsible <see cref="Body"/> (see
	/// PanelSection.xaml). A section with a <see cref="LayerId"/> is a MAP LAYER: it gets a drag grip, and
	/// its host reads the new order back with <see cref="LayerOrderOf"/> when <see cref="Reordered"/> fires.
	/// </summary>
	[ContentProperty(Name = nameof(Body))]
	public sealed partial class PanelSection : UserControl
	{
		public PanelSection()
		{
			InitializeComponent();
			// handledEventsToo: the slider and the box mark arrow keys handled before they bubble here.
			AddHandler(KeyDownEvent, new KeyEventHandler(OnSectionKeyDown), true);
			Loaded += (_, _) => VisualStateManager.GoToState(this, "Normal", false);
		}

		// ── Properties ───────────────────────────────────────────────────────────────────────────────

		/// <summary>The section title.</summary>
		public string Header
		{
			get => (string)GetValue(HeaderProperty);
			set => SetValue(HeaderProperty, value);
		}
		public static readonly DependencyProperty HeaderProperty =
			DependencyProperty.Register(nameof(Header), typeof(string), typeof(PanelSection), new PropertyMetadata(""));

		/// <summary>The section's content. Null = a header-only section (no chevron, no toggling).</summary>
		public object? Body
		{
			get => GetValue(BodyProperty);
			set => SetValue(BodyProperty, value);
		}
		public static readonly DependencyProperty BodyProperty =
			DependencyProperty.Register(nameof(Body), typeof(object), typeof(PanelSection), new PropertyMetadata(null));

		/// <summary>Whether the body is showing. View-only state; nothing persists it.</summary>
		public bool IsExpanded
		{
			get => (bool)GetValue(IsExpandedProperty);
			set => SetValue(IsExpandedProperty, value);
		}
		public static readonly DependencyProperty IsExpandedProperty =
			DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(PanelSection), new PropertyMetadata(false));

		/// <summary>
		/// The map overlay this section IS (a <c>Models.LayerOrder</c> id). Set = the section can be dragged
		/// and its position is the layer's place in the map stack. Empty = an ordinary section, which also
		/// acts as a wall the layer sections can't be dragged past.
		/// </summary>
		public string LayerId
		{
			get => (string)GetValue(LayerIdProperty);
			set => SetValue(LayerIdProperty, value);
		}
		public static readonly DependencyProperty LayerIdProperty =
			DependencyProperty.Register(nameof(LayerId), typeof(string), typeof(PanelSection), new PropertyMetadata(""));

		/// <summary>Keep the grip and box columns even though this section has neither, so its title lines
		/// up with the layer sections around it (a non-layer section in a mixed window).</summary>
		public bool ReserveLayerColumns
		{
			get => (bool)GetValue(ReserveLayerColumnsProperty);
			set => SetValue(ReserveLayerColumnsProperty, value);
		}
		public static readonly DependencyProperty ReserveLayerColumnsProperty =
			DependencyProperty.Register(nameof(ReserveLayerColumns), typeof(bool), typeof(PanelSection), new PropertyMetadata(false));

		/// <summary>Show the header check box (a select-all, or a layer's show/hide).</summary>
		public bool ShowCheckBox
		{
			get => (bool)GetValue(ShowCheckBoxProperty);
			set => SetValue(ShowCheckBoxProperty, value);
		}
		public static readonly DependencyProperty ShowCheckBoxProperty =
			DependencyProperty.Register(nameof(ShowCheckBox), typeof(bool), typeof(PanelSection), new PropertyMetadata(false));

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
			DependencyProperty.Register(nameof(IsChecked), typeof(bool?), typeof(PanelSection), new PropertyMetadata(false));

		public bool IsCheckBoxEnabled
		{
			get => (bool)GetValue(IsCheckBoxEnabledProperty);
			set => SetValue(IsCheckBoxEnabledProperty, value);
		}
		public static readonly DependencyProperty IsCheckBoxEnabledProperty =
			DependencyProperty.Register(nameof(IsCheckBoxEnabled), typeof(bool), typeof(PanelSection), new PropertyMetadata(true));

		public string CheckBoxToolTip
		{
			get => (string)GetValue(CheckBoxToolTipProperty);
			set => SetValue(CheckBoxToolTipProperty, value);
		}
		public static readonly DependencyProperty CheckBoxToolTipProperty =
			DependencyProperty.Register(nameof(CheckBoxToolTip), typeof(string), typeof(PanelSection), new PropertyMetadata(null));

		/// <summary>Show the opacity slider + readout in the header.</summary>
		public bool ShowOpacity
		{
			get => (bool)GetValue(ShowOpacityProperty);
			set => SetValue(ShowOpacityProperty, value);
		}
		public static readonly DependencyProperty ShowOpacityProperty =
			DependencyProperty.Register(nameof(ShowOpacity), typeof(bool), typeof(PanelSection), new PropertyMetadata(false));

		/// <summary>The layer's opacity, 0-1. Bind TWO-WAY to the view model's value.</summary>
		public double OpacityValue
		{
			get => (double)GetValue(OpacityValueProperty);
			set => SetValue(OpacityValueProperty, value);
		}
		public static readonly DependencyProperty OpacityValueProperty =
			DependencyProperty.Register(nameof(OpacityValue), typeof(double), typeof(PanelSection), new PropertyMetadata(1.0));

		/// <summary>False while the layer draws nothing — the slider greys and the readout dims.</summary>
		public bool IsOpacityEnabled
		{
			get => (bool)GetValue(IsOpacityEnabledProperty);
			set => SetValue(IsOpacityEnabledProperty, value);
		}
		public static readonly DependencyProperty IsOpacityEnabledProperty =
			DependencyProperty.Register(nameof(IsOpacityEnabled), typeof(bool), typeof(PanelSection), new PropertyMetadata(true));

		public string OpacityToolTip
		{
			get => (string)GetValue(OpacityToolTipProperty);
			set => SetValue(OpacityToolTipProperty, value);
		}
		public static readonly DependencyProperty OpacityToolTipProperty =
			DependencyProperty.Register(nameof(OpacityToolTip), typeof(string), typeof(PanelSection), new PropertyMetadata(null));

		/// <summary>The header box was clicked. The handler decides (VM); see <see cref="IsChecked"/>.</summary>
		public event RoutedEventHandler? CheckBoxClick;

		/// <summary>This section was moved to a new place among its siblings (drag or Alt+Arrow).</summary>
		public event EventHandler? Reordered;

		public bool IsReorderable => !string.IsNullOrEmpty(LayerId);

		// ── x:Bind functions (layout) ────────────────────────────────────────────────────────────────

		private const double GripColumn = 18, BoxColumn = 28, TitleColumn = 118, PercentColumn = 44;

		public GridLength GripWidth(string layerId, bool reserve) =>
			new(!string.IsNullOrEmpty(layerId) || reserve ? GripColumn : 0);
		public GridLength BoxWidth(bool showBox, bool reserve) => new(showBox || reserve ? BoxColumn : 0);
		public GridLength TitleWidth(bool showOpacity) =>
			showOpacity ? new GridLength(TitleColumn) : new GridLength(1, GridUnitType.Star);
		public GridLength SliderWidth(bool showOpacity) =>
			showOpacity ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
		public GridLength PercentWidth(bool showOpacity) => new(showOpacity ? PercentColumn : 0);

		// The grip column brings its own left air; without it the chevron needs some.
		public Thickness HeaderPadding(string layerId, bool reserve) =>
			new(!string.IsNullOrEmpty(layerId) || reserve ? 2 : 6, 0, 10, 0);

		// Round only the header's top corners while the body shows beneath it.
		public CornerRadius HeaderCorners(bool expanded, object? body) =>
			expanded && body is not null ? new CornerRadius(3, 3, 0, 0) : new CornerRadius(3);

		public Visibility GripVisibility(string layerId) => BoolVisibility(!string.IsNullOrEmpty(layerId));
		public Visibility ChevronVisibility(object? body) => BoolVisibility(body is not null);
		public Visibility BodyVisibility(bool expanded, object? body) => BoolVisibility(expanded && body is not null);
		public Visibility BoolVisibility(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
		public double ChevronAngle(bool expanded) => expanded ? 90 : 0;

		public string Percent(double opacity) =>
			Math.Round(opacity * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
		// A TextBlock has no disabled state; the readout dims through Opacity (no theme brush in code).
		public double DimUnless(bool enabled) => enabled ? 1.0 : 0.4;

		public string ChevronName(string header, bool expanded) => (expanded ? "Collapse " : "Expand ") + header;
		public string CheckBoxName(string header) => "Show " + header;
		public string OpacityName(string header) => header + " opacity";

		// ── Header: expand / collapse, hover, the box ────────────────────────────────────────────────

		private void OnChevronClick(object sender, RoutedEventArgs e) => IsExpanded = !IsExpanded;

		private void OnHeaderTapped(object sender, TappedRoutedEventArgs e)
		{
			if (Body is null) { return; }
			if (e.OriginalSource is DependencyObject src &&
				(IsWithin(src, Grip) || IsWithin(src, ChevronButton) || IsWithin(src, HeaderBox) || IsWithin(src, OpacitySlider)))
			{
				return; // those have gestures of their own
			}
			IsExpanded = !IsExpanded;
		}

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

		// ── Re-ordering ──────────────────────────────────────────────────────────────────────────────
		// ⚠️ NOTHING MOVES IN THE VISUAL TREE DURING A DRAG. Removing an element cancels its pointer capture,
		// so the dragged section and the siblings it passes only TRANSLATE; the real Children.Move happens
		// once, on release. The run it can move within is the contiguous block of layer sections around it.

		private bool _dragging;
		private Panel? _host;
		private List<PanelSection> _block = new();
		private List<double> _tops = new();
		private int _from, _to;
		private double _startY, _spacing;

		private void OnGripPointerEntered(object sender, PointerRoutedEventArgs e) =>
			ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);

		private void OnGripPointerExited(object sender, PointerRoutedEventArgs e)
		{
			if (!_dragging) { ProtectedCursor = null; }
		}

		private void OnGripPressed(object sender, PointerRoutedEventArgs e)
		{
			if (!IsReorderable || Parent is not Panel host) { return; }
			if (!e.GetCurrentPoint(Grip).Properties.IsLeftButtonPressed) { return; }
			var block = BlockAround(host, this);
			if (block.Count < 2) { return; }

			_host = host;
			_block = block;
			_from = _to = block.IndexOf(this);
			_spacing = host is StackPanel sp ? sp.Spacing : 0;
			_tops = block.Select(s => s.TransformToVisual(host).TransformPoint(new Point(0, 0)).Y).ToList();
			_startY = e.GetCurrentPoint(host).Position.Y;
			_dragging = true;

			foreach (var s in block)
			{
				s.TranslationTransition = ReferenceEquals(s, this) ? null : new Vector3Transition { Duration = TimeSpan.FromMilliseconds(140) };
			}
			Canvas.SetZIndex(this, 1);
			VisualStateManager.GoToState(this, "Lifted", false);
			Grip.CapturePointer(e.Pointer);
			e.Handled = true;
		}

		private void OnGripMoved(object sender, PointerRoutedEventArgs e)
		{
			if (!_dragging || _host is null) { return; }
			double h = ActualHeight;
			double dy = e.GetCurrentPoint(_host).Position.Y - _startY;
			// Stay inside the block: no further up than its first slot, no further down than its last.
			var last = _block[^1];
			dy = Math.Clamp(dy, _tops[0] - _tops[_from], _tops[^1] + last.ActualHeight - (_tops[_from] + h));
			Translation = new Vector3(0, (float)dy, 0);

			// The dragged section's LEADING EDGE decides its slot: a sibling steps aside once that edge
			// crosses the sibling's middle, and every sibling passed slides one section-height the other way.
			// ⚠️ NOT the dragged section's middle: the clamp above stops it at the block's end, so a section
			// as tall as (or taller than) the last one could never get its middle past the last one's — the
			// end sibling would never move out of the way.
			double top = _tops[_from] + dy, bottom = top + h;
			int to = _from;
			for (int i = 0; i < _from; i++)
			{
				if (top < _tops[i] + _block[i].ActualHeight / 2) { to = i; break; }
			}
			for (int i = _block.Count - 1; i > _from; i--)
			{
				if (bottom > _tops[i] + _block[i].ActualHeight / 2) { to = i; break; }
			}
			_to = to;

			float step = (float)(h + _spacing);
			for (int i = 0; i < _block.Count; i++)
			{
				if (i == _from) { continue; }
				float y = i >= to && i < _from ? step : i <= to && i > _from ? -step : 0;
				_block[i].Translation = new Vector3(0, y, 0);
			}
		}

		private void OnGripReleased(object sender, PointerRoutedEventArgs e)
		{
			Grip.ReleasePointerCapture(e.Pointer);
			EndDrag();
		}

		private void OnGripCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag();

		private void EndDrag()
		{
			if (!_dragging || _host is null) { return; }
			_dragging = false;
			var host = _host;
			_host = null;

			// Snap everything home with no transition, then move for real in the same tick, so the layout
			// lands exactly where the translations were showing it.
			foreach (var s in _block)
			{
				s.TranslationTransition = null;
				s.Translation = Vector3.Zero;
			}
			Canvas.SetZIndex(this, 0);
			ProtectedCursor = null;
			VisualStateManager.GoToState(this, "Normal", false);

			if (_to != _from)
			{
				var target = _block[_to];
				host.Children.Move((uint)host.Children.IndexOf(this), (uint)host.Children.IndexOf(target));
				Reordered?.Invoke(this, EventArgs.Empty);
			}
			_block = new();
		}

		private void OnSectionKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (!IsReorderable || _dragging || (e.Key != VirtualKey.Up && e.Key != VirtualKey.Down)) { return; }
			if (!InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down)) { return; }
			if (Parent is not Panel host) { return; }

			var block = BlockAround(host, this);
			int at = block.IndexOf(this);
			int to = e.Key == VirtualKey.Up ? at - 1 : at + 1;
			e.Handled = true;
			if (to < 0 || to >= block.Count) { return; }

			var focused = FocusManager.GetFocusedElement(XamlRoot) as Control;
			host.Children.Move((uint)host.Children.IndexOf(this), (uint)host.Children.IndexOf(block[to]));
			Reordered?.Invoke(this, EventArgs.Empty);
			// A move can drop keyboard focus; give it back once the section is laid out again.
			DispatcherQueue.TryEnqueue(() => focused?.Focus(FocusState.Keyboard));
		}

		// The contiguous run of layer sections in the host that contains this one.
		private static List<PanelSection> BlockAround(Panel host, PanelSection section)
		{
			var kids = host.Children.ToList();
			int at = kids.IndexOf(section);
			int start = at, end = at;
			while (start > 0 && kids[start - 1] is PanelSection { IsReorderable: true }) { start--; }
			while (end < kids.Count - 1 && kids[end + 1] is PanelSection { IsReorderable: true }) { end++; }
			return kids.Skip(start).Take(end - start + 1).Cast<PanelSection>().ToList();
		}

		// ── Host helpers ─────────────────────────────────────────────────────────────────────────────

		/// <summary>The layer ids of the host's layer sections, top first — the map's order.</summary>
		public static List<string> LayerOrderOf(Panel host) =>
			host.Children.OfType<PanelSection>().Where(s => s.IsReorderable).Select(s => s.LayerId).ToList();

		/// <summary>
		/// Re-arranges the host's layer sections into a saved order (top first). Ids the list doesn't name
		/// keep their XAML order, after the named ones. Empty = leave the XAML order alone.
		/// ⚠️ Assumes the layer sections form ONE contiguous run, as they do in both temporal windows.
		/// </summary>
		public static void ApplyLayerOrder(Panel host, IReadOnlyList<string> topFirst)
		{
			if (topFirst.Count == 0) { return; }
			var slots = new List<int>();
			for (int i = 0; i < host.Children.Count; i++)
			{
				if (host.Children[i] is PanelSection { IsReorderable: true }) { slots.Add(i); }
			}
			var sorted = slots
				.Select((slot, k) => (Section: (PanelSection)host.Children[slot], k))
				.OrderBy(t => { int r = IndexOf(topFirst, t.Section.LayerId); return r < 0 ? int.MaxValue : r; })
				.ThenBy(t => t.k)
				.Select(t => t.Section)
				.ToList();
			for (int j = 0; j < sorted.Count; j++)
			{
				int cur = host.Children.IndexOf(sorted[j]);
				if (cur != slots[j]) { host.Children.Move((uint)cur, (uint)slots[j]); }
			}
		}

		private static int IndexOf(IReadOnlyList<string> list, string id)
		{
			for (int i = 0; i < list.Count; i++)
			{
				if (string.Equals(list[i], id, StringComparison.Ordinal)) { return i; }
			}
			return -1;
		}
	}
}
