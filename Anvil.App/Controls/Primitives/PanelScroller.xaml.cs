using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;

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

		private const double MinThumbHeight = 32;
		private const double RestOpacity = 0.45;
		private const double ActiveOpacity = 0.85;

		private bool _dragging;
		private double _dragStartY;
		private double _dragStartOffset;
		private bool _hovering;

		public PanelScroller()
		{
			InitializeComponent();
			GutterColumn.Width = new GridLength(GutterWidth);
		}

		/// <summary>The scrolled content.</summary>
		public object? Body
		{
			get => GetValue(BodyProperty);
			set => SetValue(BodyProperty, value);
		}
		public static readonly DependencyProperty BodyProperty =
			DependencyProperty.Register(nameof(Body), typeof(object), typeof(PanelScroller), new PropertyMetadata(null));

		// ── Geometry: viewer → thumb ──

		private double Scrollable => Math.Max(0, Viewer.ExtentHeight - Viewer.ViewportHeight);

		private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateThumb();

		private void OnGeometryChanged(object sender, SizeChangedEventArgs e) => UpdateThumb();

		private void UpdateThumb()
		{
			var track = Track.ActualHeight;
			var extent = Viewer.ExtentHeight;
			var viewport = Viewer.ViewportHeight;

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
			ThumbOffset.Y = scrollable <= 0 ? 0 : travel * Math.Clamp(Viewer.VerticalOffset / scrollable, 0, 1);
			Thumb.Visibility = Visibility.Visible;
		}

		// ── Thumb drag: thumb → viewer ──

		private void OnThumbPressed(object sender, PointerRoutedEventArgs e)
		{
			if (!e.GetCurrentPoint(Thumb).Properties.IsLeftButtonPressed) { return; }
			_dragging = Thumb.CapturePointer(e.Pointer);
			_dragStartY = e.GetCurrentPoint(Track).Position.Y;
			_dragStartOffset = Viewer.VerticalOffset;
			ApplyOpacity();
			e.Handled = true; // not a track click
		}

		private void OnThumbMoved(object sender, PointerRoutedEventArgs e)
		{
			if (!_dragging) { return; }
			var travel = Track.ActualHeight - Thumb.ActualHeight;
			if (travel <= 0) { return; }
			var dy = e.GetCurrentPoint(Track).Position.Y - _dragStartY;
			var offset = Math.Clamp(_dragStartOffset + dy * Scrollable / travel, 0, Scrollable);
			Viewer.ChangeView(null, offset, null, disableAnimation: true);
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
			if (Thumb.Visibility != Visibility.Visible) { return; }
			var y = e.GetCurrentPoint(Track).Position.Y;
			var direction = y < ThumbOffset.Y ? -1 : 1;
			var page = Math.Max(1, Viewer.ViewportHeight * 0.9);
			Viewer.ChangeView(null, Math.Clamp(Viewer.VerticalOffset + direction * page, 0, Scrollable), null);
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
