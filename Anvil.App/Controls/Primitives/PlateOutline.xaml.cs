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

		/// <summary>
		/// INSET CORNER mode (the Settings window, 2026-11-16): instead of a floating plate, the outline of a BODY that a
		/// raised band (above) and rail (left) wrap like an L — ONE hairline down its left edge, curving
		/// <see cref="InsetCornerRadius"/> into its top edge, plus the raised fill outside that curve so the band's grey
		/// flows round into the rail's. Right and bottom are not stroked. Set <see cref="IsRaised"/> (the wedge is the
		/// band/rail grey) and <see cref="ShowShadow"/> False with it.
		/// </summary>
		public bool IsInsetCorner
		{
			get => (bool)GetValue(IsInsetCornerProperty);
			set => SetValue(IsInsetCornerProperty, value);
		}

		public static readonly DependencyProperty IsInsetCornerProperty =
			DependencyProperty.Register(nameof(IsInsetCorner), typeof(bool), typeof(PlateOutline), new PropertyMetadata(false, (d, e) =>
			{
				((PlateOutline)d).ApplyFace();
				OnShapeChanged(d, e);
			}));

		/// <summary>The inset corner's radius.</summary>
		private const double InsetCornerRadius = 10;

		// ── THE JOIN (inset corner only, 2026-11-17): the host's SELECTED rail entry cut from the body's own ground ──────
		/// <summary>The fillet where the join's top / bottom edge meets the body's left edge.</summary>
		private const double JoinFillet = 8;

		/// <summary>The join's own outer corners (= a rail row's CornerRadius).</summary>
		private const double JoinCorner = 4;

		private double _joinLeft;
		private double _joinTop;
		private double _joinBottom;

		/// <summary>
		/// The body's ground, which the JOIN is filled with — bind it to whatever paints the body (the Settings window binds
		/// its root's Background), so the selected rail entry always takes the page's ground, never a colour of its own.
		/// </summary>
		public Brush? JoinFill
		{
			get => (Brush?)GetValue(JoinFillProperty);
			set => SetValue(JoinFillProperty, value);
		}

		public static readonly DependencyProperty JoinFillProperty =
			DependencyProperty.Register(nameof(JoinFill), typeof(Brush), typeof(PlateOutline), new PropertyMetadata(null));

		/// <summary>
		/// INSET CORNER: the body reaches out over the rail around the box <paramref name="left"/> (negative — left of the
		/// body's edge) · <paramref name="top"/> · <paramref name="bottom"/>, in this control's coordinates, and the hairline
		/// runs ROUND it. The host measures the selected entry and calls this (computed geometry — see the XAML header).
		/// </summary>
		public void SetJoin(double left, double top, double bottom)
		{
			if (left == _joinLeft && top == _joinTop && bottom == _joinBottom) { return; }
			(_joinLeft, _joinTop, _joinBottom) = (left, top, bottom);
			Rebuild();
		}

		/// <summary>No join: the plain inset corner.</summary>
		public void ClearJoin() => SetJoin(0, 0, 0);

		private bool HasJoin => IsInsetCorner && _joinLeft < -2 * JoinCorner && _joinBottom - _joinTop >= 2 * JoinCorner;

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

		private void ApplyFace()
		{
			VisualStateManager.GoToState(this, IsRaised ? "RaisedFace" : "GroundFace", false);
			VisualStateManager.GoToState(this, IsInsetCorner ? "InsetLine" : "FloatingLine", false);
		}

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			// The display scale lives on the XamlRoot, which only exists once loaded; it changes when the window moves to
			// another monitor or the user changes scaling.
			_root = XamlRoot;
			if (_root is not null)
			{
				_root.Changed += OnRootChanged;
			}
			ApplyFace(); // a state set before the first layout can be lost — re-assert once loaded
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
			PlateOutlineWedge.Data = IsInsetCorner ? BuildInsetWedge(scale) : null;
			PlateOutlineJoin.Data = HasJoin ? BuildJoinFill(scale) : null;
		}

		// The silhouette, clockwise from the top-left. Every straight run sits on the CENTRE of a screen pixel (edges
		// snapped to whole pixels, then half a pixel in), so the one-pixel stroke lights exactly one pixel row/column.
		private PathGeometry BuildOutline(double width, double height, double scale)
		{
			if (IsInsetCorner)
			{
				return BuildInsetCorner(width, height, scale);
			}

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

		// INSET CORNER — three Paths share ONE silhouette (each built here, so they meet exactly):
		//   PlateOutlineFace  = the LINE (open, unfilled): up the body's left edge, round the JOIN if there is one, round the
		//                       10 px corner, along the top to the right;
		//   PlateOutlineWedge = the raised fill outside the corner curve (band + rail read as one grey L) — no stroke;
		//   PlateOutlineJoin  = the join's fill (JoinFill = the body's ground) — no stroke.
		// When the join's top fillet would run into the corner curve (the FIRST rail entry), the two become ONE S-curve
		// from the join's top edge to the body's top edge. Every straight run sits on a pixel centre, like the plate's.
		private readonly record struct InsetFrame(double Left, double Top, double Right, double Bottom, double Half,
			double JoinLeft, double JoinTop, double JoinBottom, bool MeetsTop);

		private InsetFrame Frame(double scale)
		{
			double half = 0.5 / scale;
			double Snap(double value) => Math.Round(value * scale) / scale;
			double joinTop = Snap(_joinTop) + half;
			return new InsetFrame(half, half, Snap(ActualWidth), Snap(ActualHeight), half,
				Snap(_joinLeft) + half, joinTop, Snap(_joinBottom) - half,
				MeetsTop: joinTop - JoinFillet < half + InsetCornerRadius);
		}

		private PathGeometry BuildInsetCorner(double width, double height, double scale)
		{
			_ = (width, height);
			var f = Frame(scale);
			double r = InsetCornerRadius;
			var line = new PathFigure { StartPoint = new Point(f.Left, f.Bottom), IsClosed = false, IsFilled = false };

			if (HasJoin)
			{
				line.Segments.Add(new LineSegment { Point = new Point(f.Left, f.JoinBottom + JoinFillet) });
				AddJoinRun(line, f);
			}
			if (!HasJoin || !f.MeetsTop)
			{
				line.Segments.Add(new LineSegment { Point = new Point(f.Left, f.Top + r) });
				line.Segments.Add(new ArcSegment { Point = new Point(f.Left + r, f.Top), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise });
			}
			line.Segments.Add(new LineSegment { Point = new Point(f.Right, f.Top) });

			var geometry = new PathGeometry();
			geometry.Figures.Add(line);
			return geometry;
		}

		// The raised fill outside the corner. With the S-curve it reaches left over the rail (the rail's own grey, so unseen).
		private PathGeometry BuildInsetWedge(double scale)
		{
			var f = Frame(scale);
			double r = InsetCornerRadius;
			PathFigure wedge;
			if (HasJoin && f.MeetsTop)
			{
				wedge = new PathFigure { StartPoint = new Point(f.Left - JoinFillet, f.Top), IsClosed = true, IsFilled = true };
				wedge.Segments.Add(new LineSegment { Point = new Point(f.Left + r, f.Top) });
				wedge.Segments.Add(new BezierSegment
				{
					Point1 = new Point(f.Left, f.Top),
					Point2 = new Point(f.Left, f.JoinTop),
					Point3 = new Point(f.Left - JoinFillet, f.JoinTop),
				});
			}
			else
			{
				wedge = new PathFigure { StartPoint = new Point(f.Left, f.Top), IsClosed = true, IsFilled = true };
				wedge.Segments.Add(new LineSegment { Point = new Point(f.Left + r, f.Top) });
				wedge.Segments.Add(new ArcSegment { Point = new Point(f.Left, f.Top + r), Size = new Size(r, r), SweepDirection = SweepDirection.Counterclockwise });
			}
			var geometry = new PathGeometry();
			geometry.Figures.Add(wedge);
			return geometry;
		}

		// The join's fill: the same run as the line, closed back along the body's edge (inside the body = its ground anyway).
		private PathGeometry BuildJoinFill(double scale)
		{
			var f = Frame(scale);
			var fill = new PathFigure { StartPoint = new Point(f.Left, f.JoinBottom + JoinFillet), IsClosed = true, IsFilled = true };
			AddJoinRun(fill, f);
			if (f.MeetsTop)
			{
				fill.Segments.Add(new LineSegment { Point = new Point(f.Left + InsetCornerRadius, f.JoinBottom + JoinFillet) });
			}
			var geometry = new PathGeometry();
			geometry.Figures.Add(fill);
			return geometry;
		}

		// From (body edge, under the join) round the join to the body: bottom fillet, the join's bottom, its rounded left
		// end, its top, then the top fillet (ends on the body's edge) — or, when it meets the top, the S-curve (ends on the
		// body's top edge, past the corner).
		private static void AddJoinRun(PathFigure figure, InsetFrame f)
		{
			double fl = JoinFillet;
			double c = JoinCorner;
			var segments = figure.Segments;
			segments.Add(new ArcSegment { Point = new Point(f.Left - fl, f.JoinBottom), Size = new Size(fl, fl), SweepDirection = SweepDirection.Counterclockwise });
			segments.Add(new LineSegment { Point = new Point(f.JoinLeft + c, f.JoinBottom) });
			segments.Add(new ArcSegment { Point = new Point(f.JoinLeft, f.JoinBottom - c), Size = new Size(c, c), SweepDirection = SweepDirection.Clockwise });
			segments.Add(new LineSegment { Point = new Point(f.JoinLeft, f.JoinTop + c) });
			segments.Add(new ArcSegment { Point = new Point(f.JoinLeft + c, f.JoinTop), Size = new Size(c, c), SweepDirection = SweepDirection.Clockwise });
			segments.Add(new LineSegment { Point = new Point(f.Left - fl, f.JoinTop) });
			if (f.MeetsTop)
			{
				segments.Add(new BezierSegment
				{
					Point1 = new Point(f.Left, f.JoinTop),
					Point2 = new Point(f.Left, f.Top),
					Point3 = new Point(f.Left + InsetCornerRadius, f.Top),
				});
			}
			else
			{
				segments.Add(new ArcSegment { Point = new Point(f.Left, f.JoinTop - fl), Size = new Size(fl, fl), SweepDirection = SweepDirection.Counterclockwise });
			}
		}
	}
}
