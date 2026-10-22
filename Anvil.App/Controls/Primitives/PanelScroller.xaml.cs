using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The hand-rolled vertical scroller for panel windows (see the XAML header): a ScrollViewer with its own
	/// bar hidden, plus a thumb in a FIXED gutter that never collapses. The math is all here — thumb size
	/// and position from the viewer's viewport / extent / offset, and the reverse for a drag.
	/// </summary>
	[ContentProperty(Name = nameof(Body))]
	public sealed partial class PanelScroller : UserControl
	{
		/// <summary>The gutter's width in px — ALWAYS claimed, thumb or not. Anything outside the scroller
		/// that must line up with its content reserves this much on its right.</summary>
		public const double GutterWidth = 16;

		/// <summary>The LIST LANE's width (<see cref="LaneOnLeft"/>) — narrower than a panel's gutter.</summary>
		public const double ListLaneWidth = 12;

		private const double MinThumbHeight = 32;
		private const double RestOpacity = 0.45;
		private const double ActiveOpacity = 0.85;

		private bool _dragging;
		private double _dragStartY;
		private double _dragStartOffset;
		private bool _hovering;

		// The viewer the thumb reads and drives: our own Viewer for a plain body, the list's inner ScrollViewer
		// for a list body (see ApplyBody). Null only while a list body's template hasn't applied yet.
		private ScrollViewer? _viewer;
		private long _extentToken;
		private ListViewBase? _pendingList;

		public PanelScroller()
		{
			InitializeComponent();
			GutterColumn.Width = new GridLength(GutterWidth);
			ApplyBody();
			ApplyLane();
		}

		/// <summary>The scrolled content. A <see cref="ListViewBase"/> body is scrolled by its OWN viewer.</summary>
		public object? Body
		{
			get => GetValue(BodyProperty);
			set => SetValue(BodyProperty, value);
		}
		public static readonly DependencyProperty BodyProperty =
			DependencyProperty.Register(nameof(Body), typeof(object), typeof(PanelScroller),
				new PropertyMetadata(null, (d, _) => ((PanelScroller)d).ApplyBody()));

		/// <summary>The gutter sits on the LEFT, <see cref="ListLaneWidth"/> wide — the expander lists' lane.</summary>
		public bool LaneOnLeft
		{
			get => (bool)GetValue(LaneOnLeftProperty);
			set => SetValue(LaneOnLeftProperty, value);
		}
		public static readonly DependencyProperty LaneOnLeftProperty =
			DependencyProperty.Register(nameof(LaneOnLeft), typeof(bool), typeof(PanelScroller),
				new PropertyMetadata(false, (d, _) => ((PanelScroller)d).ApplyLane()));

		/// <summary>A LIST body stops growing at this many rows and scrolls (0 = no cap; the list's own MaxHeight rules).</summary>
		public int MaxRows
		{
			get => (int)GetValue(MaxRowsProperty);
			set => SetValue(MaxRowsProperty, value);
		}
		public static readonly DependencyProperty MaxRowsProperty =
			DependencyProperty.Register(nameof(MaxRows), typeof(int), typeof(PanelScroller),
				new PropertyMetadata(0, (d, _) => ((PanelScroller)d).ApplyRowCap()));

		// Geometry only (columns + the thumb's inset) — the lane's look stays the XAML thumb's.
		private void ApplyLane()
		{
			if (Track is null) { return; }
			var left = LaneOnLeft;
			LeftGutterColumn.Width = new GridLength(left ? ListLaneWidth : 0);
			GutterColumn.Width = new GridLength(left ? 0 : GutterWidth);
			Grid.SetColumn(Track, left ? 0 : 2);
			Thumb.Margin = left ? new Thickness(2, 0, 4, 0) : new Thickness(4, 0, 0, 0);
		}

		// MaxRows × the first row's REAL height: rows are sized by their container style, so the cap follows it.
		private void ApplyRowCap()
		{
			if (MaxRows <= 0 || Body is not ListViewBase list) { return; }
			if (list.ContainerFromIndex(0) is FrameworkElement row && row.ActualHeight > 0)
			{
				var cap = MaxRows * row.ActualHeight;
				if (Math.Abs(list.MaxHeight - cap) > 0.5) { list.MaxHeight = cap; }
			}
		}

		// ── Body: which viewer the thumb drives ──

		private void ApplyBody()
		{
			if (Presenter is null) { return; } // DP default fires before InitializeComponent
			Attach(null);
			if (_pendingList is not null) { _pendingList.Loaded -= OnListLoaded; _pendingList = null; }

			if (Body is ListViewBase list)
			{
				Presenter.Content = null;
				Viewer.Visibility = Visibility.Collapsed;
				ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
				ListPresenter.Content = list;
				ListPresenter.Visibility = Visibility.Visible;
				if (FindViewer(list) is { } inner) { Attach(inner); }
				else { _pendingList = list; list.Loaded += OnListLoaded; }
			}
			else
			{
				ListPresenter.Content = null;
				ListPresenter.Visibility = Visibility.Collapsed;
				Viewer.Visibility = Visibility.Visible;
				Presenter.Content = Body;
				Attach(Viewer);
			}
		}

		private void OnListLoaded(object sender, RoutedEventArgs e)
		{
			var list = (ListViewBase)sender;
			list.Loaded -= OnListLoaded;
			_pendingList = null;
			if (ReferenceEquals(list, Body) && FindViewer(list) is { } inner) { Attach(inner); }
		}

		private void Attach(ScrollViewer? viewer)
		{
			if (_viewer is not null)
			{
				_viewer.ViewChanged -= OnViewChanged;
				_viewer.SizeChanged -= OnGeometryChanged;
				_viewer.UnregisterPropertyChangedCallback(ScrollViewer.ExtentHeightProperty, _extentToken);
			}
			_viewer = viewer;
			if (_viewer is not null)
			{
				_viewer.ViewChanged += OnViewChanged;
				_viewer.SizeChanged += OnGeometryChanged;
				// A list's extent grows as rows arrive/filter without a ViewChanged — follow it directly (and re-read
				// the row cap: the first row only has a height once it is realized).
				_extentToken = _viewer.RegisterPropertyChangedCallback(ScrollViewer.ExtentHeightProperty, (_, _) =>
				{
					ApplyRowCap();
					UpdateThumb();
				});
			}
			UpdateThumb();
		}

		private static ScrollViewer? FindViewer(DependencyObject root)
		{
			for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
			{
				var child = VisualTreeHelper.GetChild(root, i);
				if (child is ScrollViewer sv) { return sv; }
				if (FindViewer(child) is { } found) { return found; }
			}
			return null;
		}

		// ── Geometry: viewer → thumb ──

		private double Scrollable => _viewer is null ? 0 : Math.Max(0, _viewer.ExtentHeight - _viewer.ViewportHeight);

		private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateThumb();

		private void OnGeometryChanged(object sender, SizeChangedEventArgs e) => UpdateThumb();

		private void UpdateThumb()
		{
			if (_viewer is null)
			{
				Thumb.Visibility = Visibility.Collapsed;
				return;
			}
			var track = Track.ActualHeight;
			var extent = _viewer.ExtentHeight;
			var viewport = _viewer.ViewportHeight;

			// Fits (or not measured yet): no thumb. ⚠️ Only the THUMB goes — the gutter keeps its width.
			if (track <= 0 || extent <= 0 || viewport <= 0 || extent <= viewport + 0.5)
			{
				Thumb.Visibility = Visibility.Collapsed;
				return;
			}

			var height = Math.Min(track, Math.Max(MinThumbHeight, track * viewport / extent));
			Thumb.Height = height;
			var travel = track - height;
			var scrollable = Scrollable;
			ThumbOffset.Y = scrollable <= 0 ? 0 : travel * Math.Clamp(_viewer.VerticalOffset / scrollable, 0, 1);
			Thumb.Visibility = Visibility.Visible;
		}

		// ── Thumb drag: thumb → viewer ──

		private void OnThumbPressed(object sender, PointerRoutedEventArgs e)
		{
			if (_viewer is null || !e.GetCurrentPoint(Thumb).Properties.IsLeftButtonPressed) { return; }
			_dragging = Thumb.CapturePointer(e.Pointer);
			_dragStartY = e.GetCurrentPoint(Track).Position.Y;
			_dragStartOffset = _viewer.VerticalOffset;
			ApplyOpacity();
			e.Handled = true; // not a track click
		}

		private void OnThumbMoved(object sender, PointerRoutedEventArgs e)
		{
			if (!_dragging || _viewer is null) { return; }
			var travel = Track.ActualHeight - Thumb.ActualHeight;
			if (travel <= 0) { return; }
			var dy = e.GetCurrentPoint(Track).Position.Y - _dragStartY;
			var offset = Math.Clamp(_dragStartOffset + dy * Scrollable / travel, 0, Scrollable);
			_viewer.ChangeView(null, offset, null, disableAnimation: true);
			e.Handled = true;
		}

		private void OnThumbReleased(object sender, PointerRoutedEventArgs e)
		{
			Thumb.ReleasePointerCapture(e.Pointer);
			EndDrag();
			e.Handled = true;
		}

		private void OnThumbCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag();

		private void EndDrag()
		{
			_dragging = false;
			ApplyOpacity();
		}

		// ── Track click: page toward the click ──

		private void OnTrackPressed(object sender, PointerRoutedEventArgs e)
		{
			if (_viewer is null || Thumb.Visibility != Visibility.Visible) { return; }
			var y = e.GetCurrentPoint(Track).Position.Y;
			var direction = y < ThumbOffset.Y ? -1 : 1;
			var page = Math.Max(1, _viewer.ViewportHeight * 0.9);
			_viewer.ChangeView(null, Math.Clamp(_viewer.VerticalOffset + direction * page, 0, Scrollable), null);
			e.Handled = true;
		}

		// ── Hover ──

		private void OnThumbEntered(object sender, PointerRoutedEventArgs e)
		{
			_hovering = true;
			ApplyOpacity();
		}

		private void OnThumbExited(object sender, PointerRoutedEventArgs e)
		{
			_hovering = false;
			ApplyOpacity();
		}

		private void ApplyOpacity() => Thumb.Opacity = _dragging || _hovering ? ActiveOpacity : RestOpacity;
	}
}
