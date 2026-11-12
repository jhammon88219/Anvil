using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The one drawing of a floating plate (face + hairline + rounded shadow) as a single outline, with an optional tab
	/// standing on its top edge. Worn by every floating box: <see cref="OverlayBar"/> while floating (the pane selectors)
	/// and <see cref="RadarConsolePlate"/>. See the XAML header for the shape and the rules.
	/// </summary>
	public sealed partial class PlateOutline : UserControl
	{
		/// <summary>The plate's outer corner radius.</summary>
		private const double Corner = 8;

		/// <summary>The radius of the inside curves where a tab's sides meet the body's top edge.</summary>
		private const double InsideCurve = 8;

		private XamlRoot? _root;
		private double _scale;

		public PlateOutline()
		{
			InitializeComponent();
			ApplyFace();
			SizeChanged += (_, _) => Rebuild();
			Loaded += OnLoaded;
			Unloaded += OnUnloaded;
		}

		/// <summary>The tab's left edge in this control's coordinates. Ignored while <see cref="TabWidth"/> is 0.</summary>
		public double TabLeft
		{
			get => (double)GetValue(TabLeftProperty);
			set => SetValue(TabLeftProperty, value);
		}

		public static readonly DependencyProperty TabLeftProperty =
			DependencyProperty.Register(nameof(TabLeft), typeof(double), typeof(PlateOutline), new PropertyMetadata(0d, OnShapeChanged));

		/// <summary>The tab's width; 0 (the default) = a plain plate with no tab.</summary>
		public double TabWidth
		{
			get => (double)GetValue(TabWidthProperty);
			set => SetValue(TabWidthProperty, value);
		}

		public static readonly DependencyProperty TabWidthProperty =
			DependencyProperty.Register(nameof(TabWidth), typeof(double), typeof(PlateOutline), new PropertyMetadata(0d, OnShapeChanged));

		/// <summary>How far the tab stands above the body's top edge.</summary>
		public double TabHeight
		{
			get => (double)GetValue(TabHeightProperty);
			set => SetValue(TabHeightProperty, value);
		}

		public static readonly DependencyProperty TabHeightProperty =
			DependencyProperty.Register(nameof(TabHeight), typeof(double), typeof(PlateOutline), new PropertyMetadata(0d, OnShapeChanged));

		/// <summary>Which way the shadow is nudged: -2 = up (the default), 2 = down.</summary>
		public double ShadowOffsetY
		{
			get => (double)GetValue(ShadowOffsetYProperty);
			set => SetValue(ShadowOffsetYProperty, value);
		}

		public static readonly DependencyProperty ShadowOffsetYProperty =
			DependencyProperty.Register(nameof(ShadowOffsetY), typeof(double), typeof(PlateOutline), new PropertyMetadata(-2d));

		/// <summary>Whether the rounded shadow is drawn (the face and hairline always are).</summary>
		public bool ShowShadow
		{
			get => (bool)GetValue(ShowShadowProperty);
			set => SetValue(ShowShadowProperty, value);
		}

		public static readonly DependencyProperty ShowShadowProperty =
			DependencyProperty.Register(nameof(ShowShadow), typeof(bool), typeof(PlateOutline), new PropertyMetadata(true));

		/// <summary>Raised face (the pane selectors, the radar console) rather than the ground face.</summary>
		public bool IsRaised
		{
			get => (bool)GetValue(IsRaisedProperty);
			set => SetValue(IsRaisedProperty, value);
		}

		public static readonly DependencyProperty IsRaisedProperty =
			DependencyProperty.Register(nameof(IsRaised), typeof(bool), typeof(PlateOutline),
				new PropertyMetadata(false, (d, _) => ((PlateOutline)d).ApplyFace()));

		// x:Bind function (no converter lookup needed).
		public Visibility ShadowVisibility(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

		private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
			((PlateOutline)d).Rebuild();

		private void ApplyFace() =>
			VisualStateManager.GoToState(this, IsRaised ? "RaisedFace" : "GroundFace", false);

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			// The display scale lives on the XamlRoot, which only exists once loaded; it changes when the window moves to
			// another monitor or the user changes scaling.
			_root = XamlRoot;
			if (_root is not null)
			{
				_root.Changed += OnRootChanged;
			}
			Rebuild();
		}

		private void OnUnloaded(object sender, RoutedEventArgs e)
		{
			if (_root is not null)
			{
				_root.Changed -= OnRootChanged;
				_root = null;
			}
		}

		// XamlRoot.Changed also fires on every window resize — only a new SCALE needs a rebuild (a new size arrives
		// through SizeChanged).
		private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
		{
			if (sender.RasterizationScale != _scale)
			{
				Rebuild();
			}
		}

		// Hand every Path the outline for the current size and scale. Each Path needs its OWN geometry object (a
		// geometry cannot be shared between shapes), so the outline is built once per Path.
		// WHY CODE: the outline depends on measured size and display scale — see the XAML header.
		private void Rebuild()
		{
			double width = ActualWidth;
			double height = ActualHeight;
			if (width < 2 * Corner || height < 2 * Corner)
			{
				return;
			}

			double scale = XamlRoot?.RasterizationScale ?? 1;
			if (scale <= 0)
			{
				scale = 1;
			}
			_scale = scale;

			PlateOutlineFace.StrokeThickness = 1 / scale; // exactly one screen pixel
			PlateOutlineFace.Data = BuildOutline(width, height, scale);
			PlateOutlineShadowRing2.Data = BuildOutline(width, height, scale);
			PlateOutlineShadowRing4.Data = BuildOutline(width, height, scale);
			PlateOutlineShadowRing6.Data = BuildOutline(width, height, scale);
			PlateOutlineShadowRing8.Data = BuildOutline(width, height, scale);
			PlateOutlineShadowRing10.Data = BuildOutline(width, height, scale);
		}

		// The silhouette, clockwise from the top-left. Every straight run sits on the CENTRE of a screen pixel (edges
		// snapped to whole pixels, then half a pixel in), so the one-pixel stroke lights exactly one pixel row/column.
		private PathGeometry BuildOutline(double width, double height, double scale)
		{
			double half = 0.5 / scale;
			double Snap(double value) => Math.Round(value * scale) / scale;

			double left = half;
			double right = Snap(width) - half;
			double bottom = Snap(height) - half;
			bool hasTab = TabWidth > 0 && TabHeight > 0;
			double top = hasTab ? Snap(TabHeight) + half : half; // the body's top edge

			var figure = new PathFigure { IsClosed = true, IsFilled = true };

			void Line(double x, double y) =>
				figure.Segments.Add(new LineSegment { Point = new Point(x, y) });

			void Arc(double x, double y, double radius, bool clockwise)
			{
				if (radius <= 0)
				{
					Line(x, y);
					return;
				}
				figure.Segments.Add(new ArcSegment
				{
					Point = new Point(x, y),
					Size = new Size(radius, radius),
					SweepDirection = clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
				});
			}

			double r = Corner;
			if (hasTab)
			{
				double tabTop = half;
				double tabLeft = Snap(TabLeft) + half;
				double tabRight = Snap(TabLeft + TabWidth) - half;
				// The inside curve shrinks if the tab stands too near a body corner to fit it.
				double curve = Math.Min(InsideCurve, Math.Max(0, Math.Min(tabLeft - left - r, right - r - tabRight)));

				figure.StartPoint = new Point(tabLeft + r, tabTop);
				Line(tabRight - r, tabTop);
				Arc(tabRight, tabTop + r, r, true);            // tab top-right
				Line(tabRight, top - curve);
				Arc(tabRight + curve, top, curve, false);      // right inside curve
				Line(right - r, top);
				Arc(right, top + r, r, true);                  // body top-right
				Line(right, bottom - r);
				Arc(right - r, bottom, r, true);               // body bottom-right
				Line(left + r, bottom);
				Arc(left, bottom - r, r, true);                // body bottom-left
				Line(left, top + r);
				Arc(left + r, top, r, true);                   // body top-left
				Line(tabLeft - curve, top);
				Arc(tabLeft, top - curve, curve, false);       // left inside curve
				Line(tabLeft, tabTop + r);
				Arc(tabLeft + r, tabTop, r, true);             // tab top-left
			}
			else
			{
				figure.StartPoint = new Point(left + r, top);
				Line(right - r, top);
				Arc(right, top + r, r, true);
				Line(right, bottom - r);
				Arc(right - r, bottom, r, true);
				Line(left + r, bottom);
				Arc(left, bottom - r, r, true);
				Line(left, top + r);
				Arc(left + r, top, r, true);
			}

			var geometry = new PathGeometry();
			geometry.Figures.Add(figure);
			return geometry;
		}
	}
}
