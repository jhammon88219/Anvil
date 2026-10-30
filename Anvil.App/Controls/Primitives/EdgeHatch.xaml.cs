using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using Anvil.Models;
using Point = Windows.Foundation.Point;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// EDGE-HATCH (experimental bolt-on): a cross-hatch band at an edge of the chrome (see the XAML header). The host sets
	/// the size; the lines come from <see cref="EdgeHatchGeometry"/>, rebuilt only when the size or a setting changes.
	/// </summary>
	public sealed partial class EdgeHatch : UserControl
	{
		public EdgeHatch()
		{
			InitializeComponent();
			SizeChanged += (_, _) => Rebuild();
			Loaded += (_, _) => { ApplyInk(); Rebuild(); };
		}

		/// <summary>Which way the band runs from the edge it hangs on.</summary>
		public EdgeHatchDirection Direction
		{
			get => (EdgeHatchDirection)GetValue(DirectionProperty);
			set => SetValue(DirectionProperty, value);
		}
		public static readonly DependencyProperty DirectionProperty =
			DependencyProperty.Register(nameof(Direction), typeof(EdgeHatchDirection), typeof(EdgeHatch),
				new PropertyMetadata(EdgeHatchDirection.Up, (d, _) => ((EdgeHatch)d).Rebuild()));

		/// <summary>The gap between lines along the edge, px.</summary>
		public double Spacing
		{
			get => (double)GetValue(SpacingProperty);
			set => SetValue(SpacingProperty, value);
		}
		public static readonly DependencyProperty SpacingProperty =
			DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(EdgeHatch),
				new PropertyMetadata(EdgeHatchSizes.DefaultSpacing, (d, _) => ((EdgeHatch)d).Rebuild()));

		/// <summary>The line thickness, px.</summary>
		public double Weight
		{
			get => (double)GetValue(WeightProperty);
			set => SetValue(WeightProperty, value);
		}
		public static readonly DependencyProperty WeightProperty =
			DependencyProperty.Register(nameof(Weight), typeof(double), typeof(EdgeHatch), new PropertyMetadata(EdgeHatchSizes.DefaultWeight));

		/// <summary>Lines drop out with distance from the edge.</summary>
		public bool Thin
		{
			get => (bool)GetValue(ThinProperty);
			set => SetValue(ThinProperty, value);
		}
		public static readonly DependencyProperty ThinProperty =
			DependencyProperty.Register(nameof(Thin), typeof(bool), typeof(EdgeHatch),
				new PropertyMetadata(true, (d, _) => ((EdgeHatch)d).Rebuild()));

		/// <summary>Lines fade with distance from the edge.</summary>
		public bool Fade
		{
			get => (bool)GetValue(FadeProperty);
			set => SetValue(FadeProperty, value);
		}
		public static readonly DependencyProperty FadeProperty =
			DependencyProperty.Register(nameof(Fade), typeof(bool), typeof(EdgeHatch),
				new PropertyMetadata(true, (d, _) => ((EdgeHatch)d).ApplyInk()));

		/// <summary>The line colour: <c>#RRGGBB</c>, or "" = the surface the band hangs on (<see cref="EdgeHatchColors"/>).</summary>
		public string Ink
		{
			get => (string)GetValue(InkProperty);
			set => SetValue(InkProperty, value);
		}
		public static readonly DependencyProperty InkProperty =
			DependencyProperty.Register(nameof(Ink), typeof(string), typeof(EdgeHatch),
				new PropertyMetadata(EdgeHatchColors.Surface, (d, _) => ((EdgeHatch)d).ApplyInk()));

		/// <summary>The surface the band hangs on is the RAISED one (the tools tier) — the "surface" ink matches it.</summary>
		public bool Raised
		{
			get => (bool)GetValue(RaisedProperty);
			set => SetValue(RaisedProperty, value);
		}
		public static readonly DependencyProperty RaisedProperty =
			DependencyProperty.Register(nameof(Raised), typeof(bool), typeof(EdgeHatch),
				new PropertyMetadata(false, (d, _) => ((EdgeHatch)d).ApplyInk()));

		/// <summary>The lines' opacity, 0–1 (the user's; the canvas carries the themed strength on top).</summary>
		public double LineOpacity
		{
			get => (double)GetValue(LineOpacityProperty);
			set => SetValue(LineOpacityProperty, value);
		}
		public static readonly DependencyProperty LineOpacityProperty =
			DependencyProperty.Register(nameof(LineOpacity), typeof(double), typeof(EdgeHatch), new PropertyMetadata(1.0));

		// Not templated, so GoToState lands before Loaded too.
		private void ApplyInk()
		{
			var hex = EdgeHatchColors.Normalize(Ink);
			if (hex == EdgeHatchColors.Surface)
			{
				// The THEMED surface colour: a VisualState setter (never a theme brush resolved here). Drop any chosen
				// colour's local brush first.
				EdgeHatchPath.ClearValue(Shape.StrokeProperty);
				VisualStateManager.GoToState(this, (Raised ? "Raised" : "Surface") + (Fade ? "Fade" : "Solid"), false);
				return;
			}

			// A CHOSEN colour — DATA, a literal brush from the hex (the ColorSwatchPicker rule). It fades to its own clear
			// twin (never through black), along the Up-laid geometry, like the state brushes.
			VisualStateManager.GoToState(this, "ChosenInk", false);
			var ink = Microsoft.UI.ColorHelper.FromArgb(0xFF, Hex(hex, 1), Hex(hex, 3), Hex(hex, 5));
			EdgeHatchPath.Stroke = !Fade
				? new SolidColorBrush(ink)
				: new LinearGradientBrush
				{
					StartPoint = new Point(0, 1),
					EndPoint = new Point(0, 0),
					GradientStops =
					{
						new GradientStop { Offset = 0, Color = ink },
						new GradientStop { Offset = 1, Color = Microsoft.UI.ColorHelper.FromArgb(0, ink.R, ink.G, ink.B) },
					},
				};
		}

		private static byte Hex(string hex, int at) =>
			byte.Parse(hex.AsSpan(at, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);

		// COMPUTED GEOMETRY (the allowed exception — the Path itself is declared in XAML). Laid out UP (edge along the
		// bottom), then turned for Right / Left, so the declared gradient brushes fade the right way in every direction.
		private void Rebuild()
		{
			double w = ActualWidth, h = ActualHeight;
			if (w <= 0 || h <= 0) { EdgeHatchPath.Data = null; return; }
			bool up = Direction == EdgeHatchDirection.Up;
			double length = up ? w : h, depth = up ? h : w;

			var geometry = new PathGeometry();
			foreach (var s in EdgeHatchGeometry.Build(length, depth, Spacing, Thin))
			{
				var figure = new PathFigure { StartPoint = new Point(s.X1, depth - s.Y1), IsClosed = false, IsFilled = false };
				figure.Segments.Add(new LineSegment { Point = new Point(s.X2, depth - s.Y2) });
				geometry.Figures.Add(figure);
			}
			EdgeHatchPath.Data = geometry;
			EdgeHatchPath.RenderTransform = Direction switch
			{
				// Up (x, y) → Right: (depth − y, x) — the bottom edge becomes the left edge.
				EdgeHatchDirection.Right => new MatrixTransform { Matrix = new Matrix { M11 = 0, M12 = 1, M21 = -1, M22 = 0, OffsetX = depth, OffsetY = 0 } },
				// Up (x, y) → Left: (y, length − x) — the bottom edge becomes the right edge.
				EdgeHatchDirection.Left => new MatrixTransform { Matrix = new Matrix { M11 = 0, M12 = -1, M21 = 1, M22 = 0, OffsetX = 0, OffsetY = length } },
				_ => null,
			};
		}
	}
}
