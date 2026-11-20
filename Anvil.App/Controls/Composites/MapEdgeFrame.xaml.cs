using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Anvil.Controls.Primitives;
using Anvil.Models;
using Anvil.ViewModels;
using Point = Windows.Foundation.Point;

namespace Anvil.Controls.Composites
{
	/// <summary>What is beyond one side of a map opening — the chrome the effect hangs on, or nothing (the window's edge).</summary>
	public enum MapEdgeSide
	{
		/// <summary>The window's edge: no effect on this side, and the corners beside it stay square.</summary>
		None,
		/// <summary>Ground-face chrome (a temporal pane, the top bar).</summary>
		Ground,
		/// <summary>Raised-face chrome (a pane header, the rail, the time strip).</summary>
		Raised,
	}

	/// <summary>
	/// The edge effect traced once round a map opening, its sides mitred so it TURNS at every corner. See the XAML header.
	/// The host places it on the opening and says what is beyond each side (<see cref="SetSides"/>).
	/// </summary>
	public sealed partial class MapEdgeFrame : UserControl
	{
		/// <summary>The black shadow's depth, px (the light cast shade every bar used).</summary>
		private const double ShadeDepth = 14;

		private MapEdgeSide _top, _left, _right, _bottom;

		public MapEdgeFrame()
		{
			InitializeComponent();
		}

		/// <summary>The edge settings (Settings → Appearance → Edges); bound from the host.</summary>
		public EdgeHatchViewModel ViewModel
		{
			get => (EdgeHatchViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(EdgeHatchViewModel), typeof(MapEdgeFrame), new PropertyMetadata(null));

		public Visibility ShadeVisibility(bool isHatch) => isHatch ? Visibility.Collapsed : Visibility.Visible;
		public Visibility HatchVisibility(bool isHatch) => isHatch ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>What is beyond each side. A side next to the window's edge (<see cref="MapEdgeSide.None"/>) draws
		/// nothing; a corner is mitred only where BOTH its sides draw.</summary>
		public void SetSides(MapEdgeSide top, MapEdgeSide left, MapEdgeSide right, MapEdgeSide bottom)
		{
			if ((_top, _left, _right, _bottom) == (top, left, right, bottom)) { return; }
			(_top, _left, _right, _bottom) = (top, left, right, bottom);
			Apply();
		}

		// (The hatch bands rebuild themselves when the band-width setting resizes them — EdgeHatch's own SizeChanged.)
		private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply();

		private void Apply()
		{
			double w = ActualWidth, h = ActualHeight;
			bool top = _top != MapEdgeSide.None, left = _left != MapEdgeSide.None;
			bool right = _right != MapEdgeSide.None, bottom = _bottom != MapEdgeSide.None;

			// HATCH: each band's ends mitred where its neighbour draws too. START = left (Down/Up), top (Right), bottom (Left).
			Band(MapEdgeFrameHatchTop, _top, mitreStart: left, mitreEnd: right);
			Band(MapEdgeFrameHatchBottom, _bottom, mitreStart: left, mitreEnd: right);
			Band(MapEdgeFrameHatchLeft, _left, mitreStart: top, mitreEnd: bottom);
			Band(MapEdgeFrameHatchRight, _right, mitreStart: bottom, mitreEnd: top);

			// SHADE: the same mitres as trapezoids, in the frame's own coordinates (the brush maps to each one's bounds).
			MapEdgeFrameShadeTop.Data = top && w > 0 ? Polygon(Map(EdgeMitre.Trapezoid(w, ShadeDepth, left, right), (x, y) => (x, y))) : null;
			MapEdgeFrameShadeBottom.Data = bottom && w > 0 ? Polygon(Map(EdgeMitre.Trapezoid(w, ShadeDepth, left, right), (x, y) => (x, h - y))) : null;
			MapEdgeFrameShadeLeft.Data = left && h > 0 ? Polygon(Map(EdgeMitre.Trapezoid(h, ShadeDepth, top, bottom), (x, y) => (y, x))) : null;
			MapEdgeFrameShadeRight.Data = right && h > 0 ? Polygon(Map(EdgeMitre.Trapezoid(h, ShadeDepth, top, bottom), (x, y) => (w - y, x))) : null;
		}

		private static void Band(EdgeHatch band, MapEdgeSide side, bool mitreStart, bool mitreEnd)
		{
			band.Visibility = side == MapEdgeSide.None ? Visibility.Collapsed : Visibility.Visible;
			band.Raised = side == MapEdgeSide.Raised;
			band.MitreStart = mitreStart;
			band.MitreEnd = mitreEnd;
		}

		private static (double X, double Y)[] Map((double X, double Y)[] points, System.Func<double, double, (double, double)> to)
		{
			var mapped = new (double X, double Y)[points.Length];
			for (int i = 0; i < points.Length; i++) { mapped[i] = to(points[i].X, points[i].Y); }
			return mapped;
		}

		// COMPUTED GEOMETRY (the allowed exception — the Paths are declared in XAML): one closed, filled polygon.
		private static PathGeometry Polygon((double X, double Y)[] points)
		{
			var figure = new PathFigure { StartPoint = new Point(points[0].X, points[0].Y), IsClosed = true, IsFilled = true };
			for (int i = 1; i < points.Length; i++) { figure.Segments.Add(new LineSegment { Point = new Point(points[i].X, points[i].Y) }); }
			var geometry = new PathGeometry();
			geometry.Figures.Add(figure);
			return geometry;
		}
	}
}
